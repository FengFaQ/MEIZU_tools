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

    /// <summary>
    /// 在 Fastboot 下把分区格式化成安卓真实支持的文件系统。
    /// <para>
    /// <see cref="FileSystemKind.Default"/> 走 <c>fastboot erase</c>（只擦，让设备自己重建）；
    /// <see cref="FileSystemKind.F2fs"/> / <see cref="FileSystemKind.Ext4"/> 走
    /// <c>fastboot format:&lt;type&gt;</c>（主机端生成镜像再整块刷下去）。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 在 Fastboot 下把分区格式化成安卓真实支持的某个文件系统。
    /// <para>
    /// <b>不走 adb 推 mkfs。</b>安卓能启动的文件系统只有 ext4 与 f2fs，
    /// 而 <c>fastboot format</c> 的实现是在<b>主机端</b>调用同目录的
    /// <c>mke2fs.exe</c> / <c>make_f2fs.exe</c> 生成镜像再整块 flash 下去
    /// （<c>system/core/fastboot/fs.cpp</c> 的 <c>generators[]</c> 只有这两项）。
    /// 这样既符合安卓的真实分区格式，也绕开了"设备上没有 mkfs 二进制"的问题。
    /// </para>
    /// <para>
    /// <see cref="FileSystemKind.Default"/> 走 <c>fastboot erase</c>：
    /// 只擦块设备，让设备下次开机时由 <c>fs_mgr</c> 的 <c>formattable</c> 标志
    /// 自动重建成 fstab 里写的正确类型。这是唯一不会选错的方式。
    /// </para>
    /// </summary>
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

        var name = partitionName.Trim();

        // 硬拒绝会毁设备的分区，而不是让用户点了才失败。
        if (FileSystemKindNames.IsProtectedPartition(name))
        {
            return FlashResult.Fail(
                $"拒绝操作：{name} 是只读系统分区或关键元数据分区。" +
                "格式化它会毁掉 AVB 校验或永久丢失校准数据，设备将无法启动。");
        }

        try
        {
            var fastboot = _tools.FastbootPath;
            if (fastboot is null)
            {
                return FlashResult.Fail("找不到 fastboot.exe");
            }

            // fastboot format 依赖同目录的 mke2fs / make_f2fs，缺一个就会失败。
            if (fileSystem.FormatType() is not null && !HasFormatGenerators())
            {
                return FlashResult.Fail(
                    "格式化需要 fastboot 同目录下的 mke2fs.exe / make_f2fs.exe / mke2fs.conf，" +
                    "当前工具目录缺少其中至少一个。请重新获取完整的工具链。");
            }

            var workDir = Path.GetDirectoryName(fastboot);

            if (fileSystem.FormatType() is { } type)
            {
                progress?.Report(new FlashProgress($"格式化 {name} 为 {type}…"));

                var result = await _runner.RunAsync(
                    fastboot,
                    ["format:" + type, name],
                    workDirectory: workDir,
                    timeout: EraseTimeout,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var text = result.Combined;
                if (!result.Success || text.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
                {
                    return FlashResult.Fail(
                        $"格式化 {name} 为 {type} 失败：{FirstLine(text)}");
                }

                return FlashResult.Ok($"已把 {name} 格式化为 {type}");
            }

            // Default：只擦，让设备自己按 fstab 重建。
            progress?.Report(new FlashProgress($"擦除 {name}，设备下次开机会自动重建文件系统…"));

            var erase = await _runner.RunAsync(
                fastboot,
                ["erase", name],
                workDirectory: workDir,
                timeout: EraseTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var eraseText = erase.Combined;
            if (!erase.Success || eraseText.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
            {
                return FlashResult.Fail($"擦除 {name} 失败：{FirstLine(eraseText)}");
            }

            return FlashResult.Ok(
                $"已擦除 {name}。下次开机时设备会按自身 fstab 自动重建为正确类型，" +
                "首次开机可能需要几分钟，请不要断电。");
        }
        catch (Exception ex)
        {
            _logger.Error($"格式化分区 {partitionName} 失败", ex);
            return FlashResult.Fail($"格式化失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 检查 fastboot 同目录下是否有格式化所需的主机端工具。
    /// <para>
    /// 官方 platform-tools 里只有 <c>mke2fs.exe</c> 与 <c>make_f2fs.exe</c>
    /// （<c>fastboot/Android.bp</c> 的 <c>required</c> 字段），没有 mkfs.exfat / mkntfs，
    /// 因为 fastboot 自己也只支持 ext4 与 f2fs。
    /// </para>
    /// </summary>
    private bool HasFormatGenerators()
    {
        var winDir = _tools.ToolsDirectory;
        return File.Exists(Path.Combine(winDir, "mke2fs.exe"))
            && File.Exists(Path.Combine(winDir, "make_f2fs.exe"));
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
