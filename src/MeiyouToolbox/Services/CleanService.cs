using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>
/// 清理服务：擦除分区、格式化分区、清空 userdata。
/// <para>
/// 参考实现 <c>bin\clean.bat</c> 的 9008 擦除走 <c>&lt;erase/&gt;</c> 节点，
/// 每次擦完都检查 <c>port_trace.txt</c> 里是否出现
/// <c>Erasing the whole disk</c> —— 那是参数错误导致整盘被擦的信号，
/// 必须立刻中止并报警。这个检查在这里原样保留，因为整盘擦除是不可逆的。
/// </para>
/// </summary>
public interface ICleanService
{
    /// <summary>9008 下按分区名擦除。</summary>
    Task<FlashResult> ErasePartitionAsync(
        string partitionName,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>adb 下把分区格式化成指定文件系统。需要 root。</summary>
    Task<FlashResult> FormatPartitionAsync(
        string partitionName,
        FileSystemKind fileSystem,
        string? label,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class CleanService : ICleanService
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan EraseTimeout = TimeSpan.FromMinutes(10);

    private readonly IProcessRunner _runner;
    private readonly IToolLocator _tools;
    private readonly IEdlReader _reader;
    private readonly IFirehoseService _firehose;
    private readonly IAppLogger _logger;

    public CleanService(
        IProcessRunner runner,
        IToolLocator tools,
        IEdlReader reader,
        IFirehoseService firehose,
        IAppLogger logger)
    {
        _runner = runner;
        _tools = tools;
        _reader = reader;
        _firehose = firehose;
        _logger = logger;
    }

    public async Task<FlashResult> ErasePartitionAsync(
        string partitionName,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            return FlashResult.Fail("分区名不能为空");
        }

        try
        {
            var resolved = await ResolvePortAsync(port, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                return FlashResult.Fail("找不到 9008 端口");
            }

            var work = CreateWorkDirectory();
            progress?.Report(new FlashProgress("读取分区表…"));

            var partitions = await _reader
                .ReadPartitionsAsync(resolved, memory, work, progress, cancellationToken)
                .ConfigureAwait(false);

            var target = partitions.FirstOrDefault(p =>
                p.Name.Equals(partitionName, StringComparison.OrdinalIgnoreCase));

            if (target is null)
            {
                return FlashResult.Fail($"找不到分区：{partitionName}");
            }

            progress?.Report(new FlashProgress(
                $"擦除 {target.Name}（LUN {target.Lun}，起始扇区 {target.StartSector}）…"));

            var xml = Path.Combine(work, "erase.xml");
            await File.WriteAllTextAsync(
                xml,
                $"""
                 <?xml version="1.0" ?>
                 <data>
                 <erase SECTOR_SIZE_IN_BYTES="{target.SectorSize}" physical_partition_number="{target.Lun}" label="{target.Name}" start_sector="{target.StartSector}" num_partition_sectors="{target.SectorCount}"/>
                 </data>
                 """,
                cancellationToken).ConfigureAwait(false);

            var options = new EdlSessionOptions
            {
                Port = resolved,
                Memory = memory,
                WorkDirectory = work,
            };

            var ok = await _firehose.SendXmlAsync(
                options,
                [xml],
                work,
                convertToRead: false,
                progress: ToEdlProgress(progress),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // 这一步最重要：检查是否误擦整盘。参考实现 clean.bat:55 同款判据。
            var trace = ReadTrace(work);
            if (trace.Contains("Erasing the whole disk", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Error($"擦除 {target.Name} 时清空了整块存储");
                return FlashResult.Fail(
                    "危险：设备报告已擦除整个存储，可能是参数错误。请立即停止操作并检查日志。");
            }

            if (!ok)
            {
                return FlashResult.Fail($"擦除 {target.Name} 失败：{DescribeSkipped()}");
            }

            return FlashResult.Ok($"已擦除分区 {target.Name}");
        }
        catch (Exception ex)
        {
            _logger.Error($"擦除分区 {partitionName} 失败", ex);
            return FlashResult.Fail($"擦除失败：{ex.Message}");
        }
    }

    public async Task<FlashResult> FormatPartitionAsync(
        string partitionName,
        FileSystemKind fileSystem,
        string? label,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            return FlashResult.Fail("分区名不能为空");
        }

        try
        {
            var adb = _tools.AdbPath;
            if (adb is null)
            {
                return FlashResult.Fail("找不到 adb.exe");
            }

            // 先确认是 root，否则 mkfs 会 Permission denied。
            var id = await _runner.RunAsync(
                adb, ["shell", "id"], timeout: ShortTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!id.StandardOutput.Contains("uid=0", StringComparison.Ordinal))
            {
                return FlashResult.Fail("格式化需要 root 权限，请先解锁并授权 su");
            }

            var devicePath = $"/dev/block/by-name/{partitionName}";

            progress?.Report(new FlashProgress($"卸载 {devicePath}…"));

            // 卸载失败是正常的：分区本来就没挂载。
            await _runner.RunAsync(
                adb, ["shell", "umount", "-f", "-d", devicePath],
                timeout: ShortTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

            var tool = fileSystem.ToolName();
            var args = new List<string> { "shell", "./" + tool };
            args.AddRange(fileSystem.BuildArguments(devicePath, label));

            progress?.Report(new FlashProgress($"格式化 {partitionName} 为 {fileSystem.Display()}…"));

            var result = await _runner.RunAsync(
                adb,
                args,
                timeout: EraseTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var text = result.Combined;
            if (!result.Success ||
                text.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return FlashResult.Fail(
                    $"格式化失败：找不到 {tool} 或执行出错。{FirstLine(text)}");
            }

            return FlashResult.Ok($"已把 {partitionName} 格式化为 {fileSystem.Display()}");
        }
        catch (Exception ex)
        {
            _logger.Error($"格式化分区 {partitionName} 失败", ex);
            return FlashResult.Fail($"格式化失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private async Task<string?> ResolvePortAsync(
        string? port, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(port))
        {
            return port.Trim();
        }

        var ports = await _firehose.ListPortsAsync(cancellationToken).ConfigureAwait(false);
        return ports.Count > 0 ? ports[0] : null;
    }

    /// <summary>fh_loader 的 trace 文件在 --mainoutputdir 下，叫 port_trace.txt。</summary>
    private static string ReadTrace(string work)
    {
        var path = Path.Combine(work, "port_trace.txt");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private string DescribeSkipped()
    {
        var skipped = _firehose.LastSkippedFiles;
        return skipped.Count == 0
            ? "详见日志"
            : "被跳过：" + string.Join(", ", skipped);
    }

    private static IProgress<EdlProgress>? ToEdlProgress(IProgress<FlashProgress>? progress) =>
        progress is null
            ? null
            : new Progress<EdlProgress>(p => progress.Report(
                new FlashProgress(p.Message, p.Percent)));

    private static string CreateWorkDirectory()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "MeiyouToolbox", "clean", DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
}
