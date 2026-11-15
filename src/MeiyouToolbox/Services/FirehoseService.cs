using System.Text;
using System.Text.RegularExpressions;
using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>9008 端口的存储介质。</summary>
public enum StorageKind
{
    Auto,
    Emmc,
    Ufs,
    Spinor,
    Skip,
}

public static class StorageKindNames
{
    /// <summary>传给 fh_loader 的 --memoryname 值。Skip 时调用方不应带该参数。</summary>
    public static string ToArgument(this StorageKind kind) => kind switch
    {
        StorageKind.Emmc => "emmc",
        StorageKind.Ufs => "ufs",
        StorageKind.Spinor => "spinor",
        _ => "auto",
    };

    public static string Display(this StorageKind kind) => kind switch
    {
        StorageKind.Emmc => "eMMC",
        StorageKind.Ufs => "UFS",
        StorageKind.Spinor => "SPI NOR",
        StorageKind.Skip => "跳过配置",
        _ => "自动识别",
    };

    /// <summary>扇区大小：eMMC 512 字节，UFS 4096 字节。写 GPT / 分区时必用。</summary>
    public static int SectorSize(this StorageKind kind) =>
        kind == StorageKind.Ufs ? 4096 : 512;
}

/// <summary>一次 9008 操作的进展。界面直接显示 <see cref="Message"/>。</summary>
public sealed record EdlProgress(string Message, double Percent = double.NaN)
{
    public bool IsIndeterminate => double.IsNaN(Percent);
}

/// <summary>
/// 9008 / Firehose 通信。
/// <para>
/// 四层协议（与参考实现一致，因为这是高通工具链的硬约束）：
/// <code>
/// QSaharaServer.exe -p \\.\COM&lt;n&gt; -s 13:&lt;programmer&gt;   ← 推引导
/// fh_loader.exe --nop                                     ← 探测是否要签名
/// qcedlcmdhelper.exe -p COM&lt;n&gt; -m sig -d &lt;授权码&gt;        ← 出示签名
/// fh_loader.exe --configure=&lt;xml&gt;                        ← 初始化存储
/// fh_loader.exe --sendxml=&lt;xml&gt; [--convertprogram2read]   ← 读 / 写
/// </code>
/// </para>
/// <para>
/// 相比参考实现的改动：
/// <list type="bullet">
/// <item>configure 自适应重试保留（这是全工程最精巧的一段），但改成读 trace 文本后
/// 精确改 XML 节点，不再用 <c>busybox sed</c> 正则硬替换。</item>
/// <item>参考实现里 <c>set /a num+=1</c> 用了未初始化的 <c>num</c>（初始化的是 <c>n</c>），
/// 导致偶发"首轮即判重试超限"；这里用显式循环变量。</item>
/// <item>参考实现从不校验授权码与端口状态的因果，这里每一步都有明确的成功判据。</item>
/// </list>
/// </para>
/// </summary>
public interface IFirehoseService
{
    /// <summary>列出所有疑似 9008 端口（COM 号）。</summary>
    Task<IReadOnlyList<string>> ListPortsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送引导并（按需）出示签名、配置端口。成功后可立即 sendxml。
    /// </summary>
    Task<bool> PrepareAsync(
        EdlSessionOptions options,
        IProgress<EdlProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>按 rawprogram XML 刷入（整包线刷）。</summary>
    Task<bool> SendXmlAsync(
        EdlSessionOptions options,
        IEnumerable<string> xmlFiles,
        string searchPath,
        bool convertToRead,
        IProgress<EdlProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>直接复位 / 重启到系统或 EDL。</summary>
    Task<bool> ResetAsync(
        string port,
        StorageKind memory,
        bool backToEdl,
        CancellationToken cancellationToken = default);

    /// <summary>从 port_trace.txt 里收集被跳过的文件（找不到 / sparse 解析失败 / 大小为 0）。</summary>
    IReadOnlyList<string> LastSkippedFiles { get; }
}

/// <summary>一次 9008 会话的参数。</summary>
public sealed class EdlSessionOptions
{
    public required string Port { get; init; }

    /// <summary>Sahara 引导文件（prog_firehose_ddr.elf 之类）。为空则跳过发送引导。</summary>
    public string? ProgrammerPath { get; init; }

    /// <summary>授权码，仅当设备要求签名时使用。为空则跳过签名。</summary>
    public string? AuthCode { get; init; }

    public StorageKind Memory { get; init; } = StorageKind.Auto;

    /// <summary>临时目录：port_trace.txt 与中间 XML 都写在这里。</summary>
    public string? WorkDirectory { get; init; }
}

public sealed partial class FirehoseService : IFirehoseService
{
    private readonly IProcessRunner _runner;
    private readonly IToolLocator _tools;
    private readonly IAppLogger _logger;
    private readonly List<string> _skipped = [];

    public FirehoseService(IProcessRunner runner, IToolLocator tools, IAppLogger logger)
    {
        _runner = runner;
        _tools = tools;
        _logger = logger;
    }

    public IReadOnlyList<string> LastSkippedFiles => _skipped;

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ConfigureTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Sahara image ID 13 = Firehose programmer。</summary>
    private const string SaharaImageId = "13";

    public async Task<IReadOnlyList<string>> ListPortsAsync(CancellationToken cancellationToken = default)
    {
        const string script =
            "Get-CimInstance Win32_PnPEntity | " +
            "Where-Object { $_.Name -match 'QDLoader|9008|900E' } | " +
            "ForEach-Object { if ($_.Name -match '\\(COM(\\d+)\\)') { $matches[1] } }";

        var result = await _runner.RunAsync(
            "powershell",
            ["-NoProfile", "-NonInteractive", "-Command", script],
            timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return [];
        }

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l.All(char.IsDigit))
            .Distinct()
            .ToList();
    }

    public async Task<bool> PrepareAsync(
        EdlSessionOptions options,
        IProgress<EdlProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var port = options.Port;
        var work = EnsureWorkDirectory(options.WorkDirectory);

        if (!string.IsNullOrWhiteSpace(options.ProgrammerPath))
        {
            progress?.Report(new EdlProgress("正在发送引导…"));
            if (!await SendProgrammerAsync(port, options.ProgrammerPath!, work, cancellationToken)
                    .ConfigureAwait(false))
            {
                return false;
            }
        }
        else
        {
            progress?.Report(new EdlProgress("跳过发送引导（未指定引导文件）"));
        }

        // 是否需要授权：只有设备明确回 Authenticate failed 才出示签名。
        progress?.Report(new EdlProgress("正在检查设备是否需要授权…"));
        var needsAuth = await ProbeAuthRequirementAsync(port, work, cancellationToken).ConfigureAwait(false);

        if (needsAuth)
        {
            if (string.IsNullOrWhiteSpace(options.AuthCode))
            {
                progress?.Report(new EdlProgress(
                    "设备要求授权，但尚未导入授权码。请先在「导入授权资料」里导入该机型的授权码。"));
                _logger.Warn($"{port}: 设备要求授权但无授权码");
                return false;
            }

            progress?.Report(new EdlProgress("设备需要授权，正在出示签名…"));
            if (!await SendSignatureAsync(port, options.AuthCode!, work, cancellationToken)
                    .ConfigureAwait(false))
            {
                return false;
            }
        }

        progress?.Report(new EdlProgress($"正在配置端口（{options.Memory.Display()}）…"));
        return await ConfigureAsync(port, options.Memory, work, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> SendProgrammerAsync(
        string port, string programmer, string work, CancellationToken cancellationToken)
    {
        if (!File.Exists(programmer))
        {
            _logger.Error($"{port}: 找不到引导文件 {programmer}");
            return false;
        }

        var exe = _tools.FindTool("QSaharaServer.exe");
        if (exe is null)
        {
            _logger.Error("找不到 QSaharaServer.exe");
            return false;
        }

        var output = Path.Combine(work, "sahara.txt");
        var result = await _runner.RunAsync(
            exe,
            ["-p", $@"\\.\COM{port}", "-s", $"{SaharaImageId}:{programmer}"],
            workingDirectory: work,
            timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(output, result.Combined, cancellationToken).ConfigureAwait(false);
        var text = result.Combined;

        if (text.Contains("File transferred successfully", StringComparison.Ordinal))
        {
            _logger.Info($"{port}: 引导发送成功");
            return true;
        }

        // 端口已经在 firehose 状态时不用再推一次引导。
        if (text.Contains("[portstatus]firehose", StringComparison.Ordinal))
        {
            _logger.Info($"{port}: 端口已在 firehose 状态，跳过发送引导");
            return true;
        }

        _logger.Error($"{port}: 引导发送失败 {FirstLines(text)}");
        return false;
    }

    private async Task<bool> ProbeAuthRequirementAsync(
        string port, string work, CancellationToken cancellationToken)
    {
        var exe = _tools.FindTool("fh_loader.exe");
        if (exe is null)
        {
            return false;
        }

        var result = await _runner.RunAsync(
            exe,
            ["--port", $@"\\.\COM{port}", "--nop",
             "--mainoutputdir", work, "--skip_configure", "--noprompt"],
            workingDirectory: work,
            timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Combined.Contains("Authenticate failed", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> SendSignatureAsync(
        string port, string authCode, string work, CancellationToken cancellationToken)
    {
        var exe = _tools.FindTool("qcedlcmdhelper.exe");
        if (exe is null)
        {
            _logger.Error("找不到 qcedlcmdhelper.exe");
            return false;
        }

        var code = CredentialStore.Normalize(authCode);
        var check = CredentialStore.ValidateCode(code, "授权码");
        if (!check.Success)
        {
            _logger.Error($"{port}: {check.Message}");
            return false;
        }

        var result = await _runner.RunAsync(
            exe,
            ["-p", $"COM{port}", "-m", "sig", "-d", code],
            workingDirectory: work,
            timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Combined.Contains("Authenticate success", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Info($"{port}: 签名成功");
            return true;
        }

        _logger.Error($"{port}: 签名失败 {FirstLines(result.Combined)}");
        return false;
    }

    /// <summary>
    /// 配置端口，最多尝试 5 次。每次失败后读 <c>port_trace.txt</c> 判断原因并修 XML：
    /// <list type="bullet">
    /// <item>哈希表过大 → 去掉 <c>MaxDigestTableSizeInBytes</c>。</item>
    /// <item>工具不是中兴出品 → 给 configure 节点加 <c>Oem="ZTE"</c>。</item>
    /// <item>载荷过大 → 用设备回报的 <c>MaxPayloadSizeToTargetInBytesSupported</c> 替换。</item>
    /// </list>
    /// </summary>
    private async Task<bool> ConfigureAsync(
        string port,
        StorageKind memory,
        string work,
        IProgress<EdlProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (memory == StorageKind.Skip)
        {
            progress?.Report(new EdlProgress("已跳过配置端口（读写可能异常）"));
            return true;
        }

        var exe = _tools.FindTool("fh_loader.exe");
        if (exe is null)
        {
            _logger.Error("找不到 fh_loader.exe");
            return false;
        }

        var candidates = memory == StorageKind.Auto
            ? new[] { StorageKind.Ufs, StorageKind.Emmc, StorageKind.Spinor }
            : [memory];

        foreach (var candidate in candidates)
        {
            if (await TryConfigureOnceAsync(port, candidate, exe, work, progress, cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }

        _logger.Warn($"{port}: 所有存储类型均配置失败");
        return false;
    }

    private async Task<bool> TryConfigureOnceAsync(
        string port,
        StorageKind memory,
        string exe,
        string work,
        IProgress<EdlProgress>? progress,
        CancellationToken cancellationToken)
    {
        var xmlPath = Path.Combine(work, $"configure-{memory.ToArgument()}.xml");
        var xml = BuildConfigureXml(memory, maxPayload: 1_048_576, maxDigestTable: 8192, useZte: false);
        await File.WriteAllTextAsync(xmlPath, xml, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            progress?.Report(new EdlProgress(
                $"配置端口 {memory.Display()}（第 {attempt}/{maxAttempts} 次）…",
                attempt / (double)maxAttempts * 100));

            var result = await _runner.RunAsync(
                exe,
                ["--port", $@"\\.\COM{port}", "--memoryname", memory.ToArgument(),
                 "--configure", xmlPath, "--search_path", work,
                 "--mainoutputdir", work, "--noprompt"],
                workingDirectory: work,
                timeout: ConfigureTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var trace = ReadTrace(work);
            if (result.Success && trace.Contains("Got the ACK for the <configure>", StringComparison.Ordinal))
            {
                _logger.Info($"{port}: {memory.ToArgument()} 配置成功");
                return true;
            }

            if (!TryPatchConfigureXml(trace, ref xml))
            {
                _logger.Warn($"{port}: {memory.ToArgument()} 配置失败且无法自动修正 {FirstLines(trace)}");
                return false;
            }

            await File.WriteAllTextAsync(xmlPath, xml, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// 按 port_trace.txt 报错修一次 XML。修不动就返回 false，由调用方停止重试。
    /// </summary>
    private bool TryPatchConfigureXml(string trace, ref string xml)
    {
        // ① 哈希表比设备支持的还大 → 直接去掉这个属性，让工具有默认值。
        if (trace.Contains("Hash table 8192 larger than supported", StringComparison.Ordinal))
        {
            var patched = DigestTableRegex().Replace(xml, string.Empty);
            if (patched == xml)
            {
                return false;
            }

            _logger.Info("配置修正：移除 MaxDigestTableSizeInBytes");
            xml = patched;
            return true;
        }

        // ② 工具厂商校验 → 声明自己来自 ZTE。
        if (trace.Contains("Tool doesn't come from ZTE", StringComparison.OrdinalIgnoreCase))
        {
            if (xml.Contains("Oem=\"ZTE\"", StringComparison.Ordinal))
            {
                return false;
            }

            _logger.Info("配置修正：增加 Oem=\"ZTE\"");
            xml = xml.Replace("<configure ", "<configure Oem=\"ZTE\" ", StringComparison.Ordinal);
            return true;
        }

        // ③ 载荷超限 → 用设备自己报的上限替换。
        if (trace.Contains("payload to target size is too large", StringComparison.OrdinalIgnoreCase))
        {
            var match = PayloadSupportedRegex().Match(trace);
            if (!match.Success)
            {
                return false;
            }

            var supported = match.Groups[1].Value;
            var patched = MaxPayloadRegex().Replace(
                xml, $"MaxPayloadSizeToTargetInBytes=\"{supported}\" ");
            if (patched == xml)
            {
                return false;
            }

            _logger.Info($"配置修正：MaxPayloadSizeToTargetInBytes={supported}");
            xml = patched;
            return true;
        }

        return false;
    }

    public async Task<bool> SendXmlAsync(
        EdlSessionOptions options,
        IEnumerable<string> xmlFiles,
        string searchPath,
        bool convertToRead,
        IProgress<EdlProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var exe = _tools.FindTool("fh_loader.exe");
        if (exe is null)
        {
            _logger.Error("找不到 fh_loader.exe");
            return false;
        }

        var work = EnsureWorkDirectory(options.WorkDirectory);
        var files = xmlFiles.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            _logger.Error("没有可用的 rawprogram XML");
            return false;
        }

        _skipped.Clear();

        var args = new List<string>
        {
            "--port", $@"\\.\COM{options.Port}",
            "--memoryname", options.Memory.ToArgument(),
            "--search_path", searchPath,
            "--sendxml", string.Join(',', files),
            "--mainoutputdir", work,
            "--skip_configure",
            "--showpercentagecomplete",
            "--noprompt",
        };

        if (convertToRead)
        {
            args.Add("--convertprogram2read");
        }

        progress?.Report(new EdlProgress(
            convertToRead ? "正在从设备回读…" : $"正在刷入（{files.Count} 个 XML）…"));

        var result = await _runner.RunStreamingAsync(
            exe,
            args,
            line =>
            {
                var percent = PercentRegex().Match(line);
                if (percent.Success && double.TryParse(percent.Groups[1].Value, out var value))
                {
                    progress?.Report(new EdlProgress(line.Trim(), value));
                }
                else
                {
                    progress?.Report(new EdlProgress(line.Trim()));
                }
            },
            workingDirectory: work,
            timeout: SendTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        CollectSkippedFiles(work);

        if (result.Success)
        {
            _logger.Info($"{options.Port}: {(convertToRead ? "回读" : "刷入")}完成");
            return true;
        }

        _logger.Error($"{options.Port}: {(convertToRead ? "回读" : "刷入")}失败 {FirstLines(result.Combined)}");
        return false;
    }

    public async Task<bool> ResetAsync(
        string port, StorageKind memory, bool backToEdl, CancellationToken cancellationToken = default)
    {
        var exe = _tools.FindTool("fh_loader.exe");
        if (exe is null)
        {
            return false;
        }

        var work = EnsureWorkDirectory(null);
        var xmlPath = Path.Combine(work, "reset.xml");
        var action = backToEdl ? "reset_to_edl" : "reset";
        var xml = $"""
            <?xml version="1.0" ?>
            <data><power value="{action}" /></data>
            """;
        await File.WriteAllTextAsync(xmlPath, xml, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        // 与参考实现一致：ufs / emmc / spinor 依次尝试，任一成功即算复位成功。
        foreach (var kind in new[] { StorageKind.Ufs, StorageKind.Emmc, StorageKind.Spinor })
        {
            var result = await _runner.RunAsync(
                exe,
                ["--port", $@"\\.\COM{port}", "--memoryname", kind.ToArgument(),
                 "--sendxml", xmlPath, "--mainoutputdir", work,
                 "--skip_configure", "--noprompt"],
                workingDirectory: work,
                timeout: ShortTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                return true;
            }
        }

        return false;
    }

    private void CollectSkippedFiles(string work)
    {
        var trace = ReadTrace(work);
        if (trace.Length == 0)
        {
            return;
        }

        foreach (var line in trace.Split('\n'))
        {
            if (line.Contains("Couldn't find the file", StringComparison.Ordinal) ||
                line.Contains("Failed to parse sparse file", StringComparison.Ordinal) ||
                line.Contains("Filesize is 0 bytes", StringComparison.Ordinal))
            {
                _skipped.Add(line.Trim());
            }
        }
    }

    private static string ReadTrace(string work)
    {
        var path = Path.Combine(work, "port_trace.txt");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildConfigureXml(
        StorageKind memory, int maxPayload, int maxDigestTable, bool useZte)
    {
        var oem = useZte ? " Oem=\"ZTE\"" : string.Empty;
        return $"""
            <?xml version="1.0" ?>
            <data>
            <configure{oem} MemoryName="{memory.ToArgument()}" Verbose="0" AlwaysValidate="0" MaxDigestTableSizeInBytes="{maxDigestTable}" MaxPayloadSizeToTargetInBytes="{maxPayload}" ZlpAwareHost="1" SkipStorageInit="0" />
            </data>
            """;
    }

    private static string EnsureWorkDirectory(string? preferred)
    {
        var dir = string.IsNullOrWhiteSpace(preferred)
            ? Path.Combine(Path.GetTempPath(), "MeiyouToolbox", "edl")
            : preferred;

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            dir = Path.GetTempPath();
        }

        return dir;
    }

    private static string FirstLines(string text, int count = 3) =>
        string.Join(" / ", text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Take(count)
            .Select(l => l.Trim()));

    [GeneratedRegex(@"MaxDigestTableSizeInBytes=""[0-9]+""\s*")]
    private static partial Regex DigestTableRegex();

    [GeneratedRegex(@"MaxPayloadSizeToTargetInBytes=""[0-9]+""\s*")]
    private static partial Regex MaxPayloadRegex();

    [GeneratedRegex(@"MaxPayloadSizeToTargetInBytesSupported\s*=\s*(\d+)")]
    private static partial Regex PayloadSupportedRegex();

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex PercentRegex();
}
