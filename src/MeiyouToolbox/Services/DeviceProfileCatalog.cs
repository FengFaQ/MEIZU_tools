using System.Text;
using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>
/// 机型档案加载。数据源是随程序发布的 <c>Assets\devices.csv</c>。
/// <para>
/// 与参考实现的差异（都是有意的修正）：
/// <list type="bullet">
/// <item>CSV 用英文表头（product,model,…），不再用 <c>[product]</c> 这种伪表头。</item>
/// <item>坏行只跳过并记录告警，不会让整个列表少一项而用户毫无察觉。</item>
/// <item>未知方案不再映射到"某条能跑通的分支"，而是 <see cref="UnlockPlan.Unknown"/>，
/// 界面上明确禁用。</item>
/// </list>
/// </para>
/// </summary>
public interface IDeviceProfileCatalog
{
    IReadOnlyList<DeviceProfile> All { get; }

    /// <summary>按代号找机型，找不到返回 null。</summary>
    DeviceProfile? Find(string? product);

    /// <summary>
    /// 从设备读数猜一个机型档案。参考实现是让用户先手选机型，
    /// 这里额外提供自动匹配，匹配不到时返回 null 由界面让用户手选。
    /// </summary>
    DeviceProfile? Guess(IEnumerable<string?> hints);
}

public sealed class DeviceProfileCatalog : IDeviceProfileCatalog
{
    private readonly List<DeviceProfile> _all = [];
    private readonly Dictionary<string, DeviceProfile> _byProduct =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IAppLogger? _logger;

    public DeviceProfileCatalog(IToolLocator tools, IAppLogger? logger = null)
    {
        _logger = logger;
        var csvPaths = new[]
        {
            Path.Combine(tools.AppDirectory, "Assets", "devices.csv"),
            Path.Combine(tools.AppDirectory, "devices.csv"),
        };

        foreach (var path in csvPaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            LoadFrom(path);
            break;
        }

        // 磁盘上没有就退回程序集内嵌的那份（AvaloniaResource 已嵌入）。
        // 用户误删 Assets\devices.csv 时机型表仍然可用。
        if (_all.Count == 0)
        {
            LoadFromEmbedded();
        }

        if (_all.Count == 0)
        {
            _logger?.Warn($"机型表未找到或为空，刷机页将要求手动选择机型。已尝试: {string.Join(" | ", csvPaths)} 以及程序集内嵌资源");
        }
        else
        {
            _logger?.Info($"机型表已加载：{_all.Count} 个机型");
        }
    }

    public IReadOnlyList<DeviceProfile> All => _all;

    public DeviceProfile? Find(string? product)
    {
        if (string.IsNullOrWhiteSpace(product))
        {
            return null;
        }

        return _byProduct.TryGetValue(product.Trim(), out var profile) ? profile : null;
    }

    public DeviceProfile? Guess(IEnumerable<string?> hints)
    {
        foreach (var hint in hints)
        {
            var hit = Find(hint);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private void LoadFrom(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger?.Error($"读取机型表失败: {path}", ex);
            return;
        }

        ParseLines(lines);
    }

    /// <summary>磁盘上的机型表缺失时，从程序集内嵌的 Avalonia 资源读一份。</summary>
    private void LoadFromEmbedded()
    {
        try
        {
            var uri = new Uri("avares://MeiyouToolbox/Assets/devices.csv");
            if (!Avalonia.Platform.AssetLoader.Exists(uri))
            {
                return;
            }

            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var lines = text.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .ToArray();

            ParseLines(lines);
            _logger?.Info("机型表从程序集内嵌资源载入");
        }
        catch (Exception ex)
        {
            _logger?.Error("读取内嵌机型表失败", ex);
        }
    }

    private void ParseLines(IEnumerable<string> lines)
    {
        var lineNumber = 0;
        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();

            // 跳过空行、注释、表头
            if (line.Length == 0 || line.StartsWith('#') ||
                line.StartsWith("product,", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cells = line.Split(',').Select(c => c.Trim()).ToArray();
            var product = cells.Length > 0 ? cells[0] : string.Empty;

            if (string.IsNullOrWhiteSpace(product))
            {
                _logger?.Warn($"机型表第 {lineNumber} 行缺少代号，已跳过：{line}");
                continue;
            }

            string Cell(int index) => index < cells.Length ? cells[index] : string.Empty;

            var profile = new DeviceProfile
            {
                Product = product,
                Model = Cell(1),
                Cpu = Cell(2),
                StorageType = Cell(3).ToLowerInvariant(),
                UnlockPlan = ParseUnlockPlan(Cell(4), product, lineNumber),
                Layout = ParseLayout(Cell(5)),
                BootPartition = string.IsNullOrWhiteSpace(Cell(6)) ? "boot" : Cell(6),
                EdlPlan = ParseEdlPlan(Cell(7)),
            };

            _all.Add(profile);
            _byProduct[profile.Product] = profile;
        }
    }

    private UnlockPlan ParseUnlockPlan(string raw, string product, int lineNumber)
    {
        var plan = raw.ToLowerInvariant() switch
        {
            "auth-private" => UnlockPlan.AuthPrivate,
            "auth-mzkv-sig" => UnlockPlan.AuthMzkvSig,
            "auth-mzkv-nosig" => UnlockPlan.AuthMzkvNoSig,
            "odm-1" => UnlockPlan.Odm1,
            "odm-2" => UnlockPlan.Odm2,
            "odm-note8" => UnlockPlan.OdmNote8,
            "" => UnlockPlan.Unknown,
            _ => UnlockPlan.Unknown,
        };

        if (plan == UnlockPlan.Unknown)
        {
            _logger?.Warn($"机型表第 {lineNumber} 行（{product}）解锁方案无效：'{raw}'，该机型将被标记为不可操作");
        }

        return plan;
    }

    private static PartitionLayout ParseLayout(string raw) => raw.ToLowerInvariant() switch
    {
        "ab" => PartitionLayout.Ab,
        "ab_rec" => PartitionLayout.AbRec,
        "aonly" => PartitionLayout.AOnly,
        _ => PartitionLayout.AOnly,
    };

    private static EdlPlan ParseEdlPlan(string raw) => raw.ToLowerInvariant() switch
    {
        "cmd" => EdlPlan.Cmd,
        "edlcable" => EdlPlan.EdlCable,
        "key" => EdlPlan.Key,
        _ => EdlPlan.Key,
    };
}
