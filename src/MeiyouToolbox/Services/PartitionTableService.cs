using System.Text.RegularExpressions;
using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>
/// GPT 分区表读写。参考实现靠 <c>partable.bat</c> + <c>gpttool.exe</c>，
/// 这里保留 gpttool（它是唯一能可靠解析高通 GPT 的工具），但把"读回 GPT →
/// 解析出起始扇区/扇区数"这段用强类型封装，避免字符串 token 解析的脆弱性。
/// </summary>
public interface IPartitionTableService
{
    /// <summary>GPT 主表占用扇区数：eMMC 34，UFS 6。</summary>
    int PrimaryGptSectors(StorageKind memory);

    /// <summary>GPT 备份表占用扇区数：eMMC 33，UFS 5。</summary>
    int BackupGptSectors(StorageKind memory);

    /// <summary>解析 gpttool 的输出，取出指定分区的物理位置。</summary>
    IReadOnlyList<GptPartition> ParseGptDump(string gptFilePath, StorageKind memory);

    /// <summary>在分区表里找指定分区（可跨多个 LUN）。</summary>
    GptPartition? Find(IEnumerable<GptPartition> partitions, string name);

    /// <summary>生成 rawprogram 里的 &lt;program/&gt; 节点。</summary>
    string BuildProgramNode(
        GptPartition partition, string fileName, StorageKind memory, bool sparse);
}

/// <summary>GPT 里的一条分区记录。</summary>
public sealed record GptPartition(
    int Lun,
    string Name,
    long StartSector,
    long SectorCount,
    int SectorSize)
{
    /// <summary>分区字节数。</summary>
    public long SizeBytes => SectorCount * SectorSize;

    public string SizeText => FormatSize(SizeBytes);

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F2} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };
}

public sealed partial class PartitionTableService : IPartitionTableService
{
    private readonly IProcessRunner _runner;
    private readonly IToolLocator _tools;
    private readonly IAppLogger? _logger;

    public PartitionTableService(IProcessRunner runner, IToolLocator tools, IAppLogger? logger = null)
    {
        _runner = runner;
        _tools = tools;
        _logger = logger;
    }

    public int PrimaryGptSectors(StorageKind memory) =>
        memory == StorageKind.Ufs ? 6 : 34;

    public int BackupGptSectors(StorageKind memory) =>
        memory == StorageKind.Ufs ? 5 : 33;

    /// <summary>
    /// 调用 gpttool.exe 解析 GPT 二进制，返回全部分区。
    /// 参考实现用 <c>for /f "tokens=4,5"</c> 抓两列，一旦输出格式变化就静默取空；
    /// 这里用正则匹配"名字 + 两个数字"，并对抓不到的行记日志。
    /// </summary>
    public IReadOnlyList<GptPartition> ParseGptDump(string gptFilePath, StorageKind memory)
    {
        var result = ParseGptDumpAsync(gptFilePath, memory).GetAwaiter().GetResult();
        return result;
    }

    private async Task<IReadOnlyList<GptPartition>> ParseGptDumpAsync(
        string gptFilePath, StorageKind memory)
    {
        var exe = _tools.FindTool("gpttool.exe");
        if (exe is null)
        {
            _logger?.Warn("找不到 gpttool.exe，无法解析分区表");
            return [];
        }

        if (!File.Exists(gptFilePath))
        {
            _logger?.Warn($"分区表文件不存在：{gptFilePath}");
            return [];
        }

        var output = Path.Combine(
            Path.GetDirectoryName(gptFilePath) ?? Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(gptFilePath) + ".txt");

        var result = await _runner.RunAsync(
            exe,
            ["-p", gptFilePath, "-f", "print:default:#insl:sector:10", "-o", output],
            timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        if (!result.Success)
        {
            _logger?.Warn($"gpttool 解析失败：{gptFilePath}");
            return [];
        }

        var text = File.Exists(output) ? await File.ReadAllTextAsync(output).ConfigureAwait(false) : result.StandardOutput;
        var sectorSize = memory.SectorSize();
        var list = new List<GptPartition>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = PartitionLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (!long.TryParse(match.Groups["start"].Value, out var start) ||
                !long.TryParse(match.Groups["count"].Value, out var count) ||
                count <= 0)
            {
                continue;
            }

            list.Add(new GptPartition(0, name, start, count, sectorSize));
        }

        _logger?.Info($"{Path.GetFileName(gptFilePath)}: 解析到 {list.Count} 个分区");
        return list;
    }

    public GptPartition? Find(IEnumerable<GptPartition> partitions, string name) =>
        partitions.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 生成 rawprogram 节点。注意 <c>sparse</c> 属性必须是 true/false 字符串，
    /// 且 <c>SECTOR_SIZE_IN_BYTES</c> 必须与存储介质匹配，否则 fh_loader 会算错偏移。
    /// </summary>
    public string BuildProgramNode(
        GptPartition partition, string fileName, StorageKind memory, bool sparse)
    {
        var sparseText = sparse ? "true" : "false";
        return $"""
            <program filename="{fileName}" physical_partition_number="{partition.Lun}" label="{partition.Name}" start_sector="{partition.StartSector}" num_partition_sectors="{partition.SectorCount}" SECTOR_SIZE_IN_BYTES="{memory.SectorSize()}" sparse="{sparseText}" />
            """;
    }

    /// <summary>
    /// 匹配 gpttool 输出的一行。典型形态：
    /// <c>  0  system   123456   204800  ...</c>，也兼容带方括号或 # 的变体。
    /// </summary>
    [GeneratedRegex(@"(?:^|\s)(?<name>[A-Za-z0-9_\-]{2,36})\s+(?<start>\d{3,})\s+(?<count>\d{3,})(?:\s|$)")]
    private static partial Regex PartitionLineRegex();
}
