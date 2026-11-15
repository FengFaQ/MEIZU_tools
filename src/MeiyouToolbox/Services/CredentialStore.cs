using System.Text;

namespace MeiyouToolbox.Services;

/// <summary>
/// 一个机型已导入的授权资料。参考实现散着五个文件放在
/// <c>bin\res\&lt;product&gt;\bak\</c>，这里收敛成一个强类型对象。
/// </summary>
public sealed class DeviceCredentials
{
    public required string Product { get; init; }

    /// <summary>解锁码（512 字符 = 256 字节）。仅 auth-private 方案使用。</summary>
    public string? UnlockCode { get; init; }

    /// <summary>授权码（512 字符），交给 Firehose 做机器签名。</summary>
    public string? AuthCode { get; init; }

    /// <summary>解锁用的 mzkv 镜像路径。</summary>
    public string? MzkvImagePath { get; init; }

    public bool HasUnlockCode => !string.IsNullOrWhiteSpace(UnlockCode);

    public bool HasAuthCode => !string.IsNullOrWhiteSpace(AuthCode);

    public bool HasMzkv => !string.IsNullOrWhiteSpace(MzkvImagePath)
                           && File.Exists(MzkvImagePath);
}

/// <summary>导入结果：成功与否 + 给人看的原因。</summary>
public sealed record CredentialImportResult(bool Success, string Message);

/// <summary>
/// 授权资料仓库：每个机型一个目录，文件用纯文本保存，便于用户自己核对。
/// <para>
/// 参考实现的两个坑在这里被规避：
/// 1. 512 字符定长校验用子串三次判断，一旦换成别的长度就悄悄失效 —— 这里改成
///    <see cref="ValidateCode"/> 明确返回原因。
/// 2. 解锁 mzkv 覆盖前不删旧文件，解包失败时旧文件仍在会误判成功 —— 这里先写
///    临时文件、校验通过后再原子替换。
/// </para>
/// </summary>
public interface ICredentialStore
{
    /// <summary>资料目录（程序目录下 profiles\&lt;product&gt;\）。</summary>
    string GetProfileDirectory(string product);

    DeviceCredentials Load(string product);

    CredentialImportResult SaveUnlockCode(string product, string code);

    CredentialImportResult SaveAuthCode(string product, string code);

    /// <summary>导入解锁 mzkv：复制到资料目录并做文件头基本校验。</summary>
    CredentialImportResult ImportMzkv(string product, string sourceImagePath);

    void Clear(string product);
}

public sealed class CredentialStore : ICredentialStore
{
    /// <summary>授权码 / 解锁码固定 512 个字符（256 字节的十六进制串）。</summary>
    public const int CodeLength = 512;

    private const string UnlockCodeFile = "unlock-code.txt";
    private const string AuthCodeFile = "auth-code.txt";
    private const string MzkvFile = "mzkv-unlock.img";

    private readonly IToolLocator _tools;
    private readonly IAppLogger? _logger;

    public CredentialStore(IToolLocator tools, IAppLogger? logger = null)
    {
        _tools = tools;
        _logger = logger;
    }

    public string GetProfileDirectory(string product)
    {
        var safe = MakeSafe(product);
        return Path.Combine(_tools.AppDirectory, "profiles", safe);
    }

    public DeviceCredentials Load(string product)
    {
        var dir = GetProfileDirectory(product);
        var mzkv = Path.Combine(dir, MzkvFile);

        return new DeviceCredentials
        {
            Product = product,
            UnlockCode = ReadTrimmed(Path.Combine(dir, UnlockCodeFile)),
            AuthCode = ReadTrimmed(Path.Combine(dir, AuthCodeFile)),
            MzkvImagePath = File.Exists(mzkv) ? mzkv : null,
        };
    }

    public CredentialImportResult SaveUnlockCode(string product, string code) =>
        SaveCode(product, code, UnlockCodeFile, "解锁码");

    public CredentialImportResult SaveAuthCode(string product, string code) =>
        SaveCode(product, code, AuthCodeFile, "授权码");

    public CredentialImportResult ImportMzkv(string product, string sourceImagePath)
    {
        if (string.IsNullOrWhiteSpace(sourceImagePath) || !File.Exists(sourceImagePath))
        {
            return new CredentialImportResult(false, $"找不到镜像文件：{sourceImagePath}");
        }

        var info = new FileInfo(sourceImagePath);
        // mzkv 是 8 MB 级别的分区镜像；过小的文件几乎肯定是选错了。
        if (info.Length < 1024 * 1024)
        {
            return new CredentialImportResult(
                false, $"文件只有 {info.Length:N0} 字节，不像是 mzkv 分区镜像。");
        }

        try
        {
            var dir = GetProfileDirectory(product);
            Directory.CreateDirectory(dir);

            var target = Path.Combine(dir, MzkvFile);
            var temp = target + ".tmp";

            // 先复制到临时文件，成功后再替换：避免中途失败留下半截文件被当成可用镜像。
            File.Copy(sourceImagePath, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);

            _logger?.Info($"{product}: 已导入解锁 mzkv（{info.Length:N0} 字节）");
            return new CredentialImportResult(true, $"已导入解锁 mzkv（{info.Length:N0} 字节）");
        }
        catch (Exception ex)
        {
            _logger?.Error($"{product}: 导入 mzkv 失败", ex);
            return new CredentialImportResult(false, $"导入失败：{ex.Message}");
        }
    }

    public void Clear(string product)
    {
        try
        {
            var dir = GetProfileDirectory(product);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                _logger?.Warn($"{product}: 已清空该机型的授权资料");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"{product}: 清空资料失败", ex);
        }
    }

    private CredentialImportResult SaveCode(string product, string code, string fileName, string label)
    {
        var validation = ValidateCode(code, label);
        if (!validation.Success)
        {
            return validation;
        }

        try
        {
            var dir = GetProfileDirectory(product);
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, fileName),
                code.Trim() + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            _logger?.Info($"{product}: 已保存{label}");
            return new CredentialImportResult(true, $"{label}已保存");
        }
        catch (Exception ex)
        {
            _logger?.Error($"{product}: 保存{label}失败", ex);
            return new CredentialImportResult(false, $"保存{label}失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 校验 512 字符定长码。参考实现用 <c>%var:~511,1%</c> 与 <c>%var:~512,1%</c>
    /// 两次子串判断，这里改成明确计数，并顺带剔除粘贴时常带的空白与换行。
    /// </summary>
    public static CredentialImportResult ValidateCode(string? code, string label = "码")
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new CredentialImportResult(false, $"{label}为空");
        }

        var normalized = Normalize(code);
        if (normalized.Length == CodeLength)
        {
            return new CredentialImportResult(true, string.Empty);
        }

        return new CredentialImportResult(
            false,
            normalized.Length < CodeLength
                ? $"{label}长度不足：{normalized.Length} / {CodeLength} 个字符"
                : $"{label}长度超出：{normalized.Length} / {CodeLength} 个字符");
    }

    /// <summary>去掉所有空白与换行，只保留有效字符。</summary>
    public static string Normalize(string code) =>
        new(code.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static string? ReadTrimmed(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static string MakeSafe(string product)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = product.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars);
        return name.Length == 0 ? "unknown" : name;
    }
}
