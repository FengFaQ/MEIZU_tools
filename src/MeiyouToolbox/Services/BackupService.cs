using System.Globalization;
using System.Text;
using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>一次备份任务的执行结果。</summary>
public sealed record BackupResult(bool Success, string Message, string? OutputPath = null)
{
    public static BackupResult Ok(string message, string? outputPath = null) =>
        new(true, message, outputPath);

    public static BackupResult Fail(string message, string? outputPath = null) =>
        new(false, message, outputPath);
}

/// <summary>备份进度的回调载荷。</summary>
public sealed record BackupProgress(string Message, double Percent = double.NaN)
{
    public bool IsIndeterminate => double.IsNaN(Percent);
}

/// <summary>备份方式。</summary>
public enum BackupScope
{
    /// <summary>整个分区表 + 大部分分区，产出可直接 9008 线刷的包。</summary>
    AllPartitions,

    /// <summary>只备份分区表（GPT）。</summary>
    PartitionTable,

    /// <summary>只备份一个分区。</summary>
    SinglePartition,

    /// <summary>QCN 基带（不在普通分区里，必须走 DIAG）。</summary>
    Qcn,
}

/// <summary>一次备份请求。</summary>
public sealed class BackupRequest
{
    /// <summary>保存到哪里。程序会给它建一个带机型名与时间戳的子目录。</summary>
    public required string TargetDirectory { get; init; }

    public BackupScope Scope { get; init; } = BackupScope.AllPartitions;

    /// <summary>机型代号，用于目录名与清单文件。</summary>
    public string? Product { get; init; }

    public string? Port { get; init; }

    public StorageKind Memory { get; init; } = StorageKind.Auto;

    /// <summary>指定要备份的分区名（<see cref="BackupScope.SinglePartition"/> 时必填）。</summary>
    public string? PartitionName { get; init; }

    /// <summary>要跳过的分区（默认已含 persist，避免覆盖后损坏 tee）。</summary>
    public IReadOnlySet<string> SkipPartitions { get; init; } = DefaultSkip;

    /// <summary>
    /// 默认跳过的分区。参考实现只跳了 userdata / last_parti / windows 三个，
    /// 但 <c>persist</c> 一旦被回写会让指纹与传感器永久失效，所以这里额外加上。
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultSkip = new HashSet<string>(
        ["userdata", "last_parti", "mindowsesp", "mindowswin", "mindowsdat", "persist"],
        StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 备份业务：把参考实现里 <c>mzbox.bat:edlreadall</c>、
/// <c>主脚本:EDLREADALLPAR</c>、<c>主脚本:QCN</c>、<c>read.bat:qcedl</c> 重写。
/// <para>
/// 产出的目录结构与参考实现保持一致（<c>images\</c> + <c>for_ROM_maker\</c>），
/// 这样备份出来的东西能直接拿去 9008 线刷，也能被别的工具识别。
/// </para>
/// </summary>
public interface IBackupService
{
    /// <summary>列出设备当前的分区（需要设备已在 9008 且 firehose 就绪）。</summary>
    Task<IReadOnlyList<GptPartition>> ListPartitionsAsync(
        string? port,
        StorageKind memory,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>执行备份。</summary>
    Task<BackupResult> RunAsync(
        BackupRequest request,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>QCN 恢复（DIAG 模式）。</summary>
    Task<BackupResult> RestoreQcnAsync(
        string? port,
        string qcnFilePath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class BackupService : IBackupService
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(30);

    private readonly IProcessRunner _runner;
    private readonly IToolLocator _tools;
    private readonly IFirehoseService _firehose;
    private readonly IPartitionTableService _partitions;
    private readonly IEdlReader _reader;
    private readonly IAppLogger _logger;

    public BackupService(
        IProcessRunner runner,
        IToolLocator tools,
        IFirehoseService firehose,
        IPartitionTableService partitions,
        IEdlReader reader,
        IAppLogger logger)
    {
        _runner = runner;
        _tools = tools;
        _firehose = firehose;
        _partitions = partitions;
        _reader = reader;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GptPartition>> ListPartitionsAsync(
        string? port,
        StorageKind memory,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolvePortAsync(port, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return [];
        }

        var work = CreateWorkDirectory();
        var effective = await ResolveMemoryAsync(resolved, memory, work, cancellationToken)
            .ConfigureAwait(false);

        return await _reader
            .ReadPartitionsAsync(resolved, effective, work, ToFlashProgress(progress), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BackupResult> RunAsync(
        BackupRequest request,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return request.Scope switch
            {
                BackupScope.Qcn => await BackupQcnAsync(request, progress, cancellationToken)
                    .ConfigureAwait(false),
                BackupScope.PartitionTable => await BackupTableAsync(request, progress, cancellationToken)
                    .ConfigureAwait(false),
                BackupScope.SinglePartition => await BackupSingleAsync(request, progress, cancellationToken)
                    .ConfigureAwait(false),
                _ => await BackupAllAsync(request, progress, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException)
        {
            return BackupResult.Fail("备份已取消");
        }
        catch (Exception ex)
        {
            _logger.Error("备份失败", ex);
            return BackupResult.Fail($"备份失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 全分区备份
    // ------------------------------------------------------------------

    private async Task<BackupResult> BackupAllAsync(
        BackupRequest request,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var port = await ResolvePortAsync(request.Port, cancellationToken).ConfigureAwait(false);
        if (port is null)
        {
            return BackupResult.Fail("未检测到 9008 端口。请先把设备进入 9008 模式。");
        }

        var target = CreateSessionDirectory(request);
        var imagesDir = Path.Combine(target, "images");
        var romDir = Path.Combine(target, "for_ROM_maker");
        var origDir = Path.Combine(romDir, "orig");
        var generateDir = Path.Combine(romDir, "generate");

        foreach (var dir in new[] { imagesDir, romDir, origDir, generateDir })
        {
            Directory.CreateDirectory(dir);
        }

        CopyReadmeFiles(romDir);

        var work = CreateWorkDirectory();
        var memory = await ResolveMemoryAsync(port, request.Memory, work, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new BackupProgress("正在读取分区表…", 0.05));

        var all = await _reader
            .ReadPartitionsAsync(port, memory, work, ToFlashProgress(progress), cancellationToken)
            .ConfigureAwait(false);

        if (all.Count == 0)
        {
            return BackupResult.Fail("读不到分区表。请确认设备已发送引导并完成端口配置。", target);
        }

        var sectorSize = memory.SectorSize();
        var lunCount = all.Max(p => p.Lun) + 1;

        var partitionXml = new StringBuilder();
        partitionXml.AppendLine("<?xml version=\"1.0\" ?>");
        partitionXml.AppendLine("<configuration>");
        partitionXml.AppendLine("<parser_instructions>");
        partitionXml.AppendLine("WRITE_PROTECT_BOUNDARY_IN_KB=0");
        partitionXml.AppendLine($"SECTOR_SIZE_IN_BYTES = {sectorSize}");
        partitionXml.AppendLine("GROW_LAST_PARTITION_TO_FILL_DISK=true");
        partitionXml.AppendLine("</parser_instructions>");

        var allXmls = new List<string>();
        var backedUp = 0;
        var total = all.Count;

        for (var lun = 0; lun < lunCount; lun++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var gptMainPath = Path.Combine(origDir, $"gpt_main{lun}.bin");
            var gptBackupPath = Path.Combine(origDir, $"gpt_backup{lun}.bin");

            // 主 / 备分区表各回读 512KB 再转换，与参考实现 readgpt 完全一致。
            var mainOk = await ReadGptAsync(
                port, memory, work, lun, "gptmain", gptMainPath, progress, cancellationToken)
                .ConfigureAwait(false);
            var backupOk = await ReadGptAsync(
                port, memory, work, lun, "gptbackup", gptBackupPath, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!mainOk)
            {
                _logger.Warn($"LUN {lun} 分区表读取失败，跳过该 LUN");
                continue;
            }

            // 用 gpttool 直接生成精确的 <partition> 段，比手工拼接可靠。
            var partitionNodes = await GetPartitionXmlAsync(gptMainPath, cancellationToken)
                .ConfigureAwait(false);
            partitionXml.AppendLine("<physical_partition>");
            foreach (var node in partitionNodes)
            {
                partitionXml.AppendLine(node);
            }

            partitionXml.AppendLine("</physical_partition>");

            var rawProgram = new StringBuilder();
            rawProgram.AppendLine("<?xml version=\"1.0\" ?>");
            rawProgram.AppendLine("<data>");

            foreach (var part in all.Where(p => p.Lun == lun))
            {
                if (request.SkipPartitions.Contains(part.Name))
                {
                    rawProgram.AppendLine(
                        $"<program filename=\"\" label=\"{part.Name}\" physical_partition_number=\"{lun}\" start_sector=\"{part.StartSector}\" num_partition_sectors=\"{part.SectorCount}\" SECTOR_SIZE_IN_BYTES=\"{sectorSize}\" />");
                    _logger.Info($"跳过 {part.Name}");
                    continue;
                }

                progress?.Report(new BackupProgress(
                    $"正在回读 {part.Name}（{part.SizeText}）…", (double)backedUp / Math.Max(1, total)));

                var destination = Path.Combine(imagesDir, $"{part.Name}.img");
                var result = await _reader.ReadPartitionAsync(
                    part with { Lun = lun },
                    destination, port, memory, work,
                    ToFlashProgress(progress), cancellationToken).ConfigureAwait(false);

                if (result.Success)
                {
                    backedUp++;
                    rawProgram.AppendLine(
                        $"<program filename=\"{part.Name}.img\" label=\"{part.Name}\" physical_partition_number=\"{lun}\" start_sector=\"{part.StartSector}\" num_partition_sectors=\"{part.SectorCount}\" SECTOR_SIZE_IN_BYTES=\"{sectorSize}\" sparse=\"false\" />");
                }
                else
                {
                    // 单个分区失败不中止整包备份，但要在 XML 里留空节点并记日志。
                    _logger.Warn($"{part.Name} 回读失败：{result.Message}");
                    rawProgram.AppendLine(
                        $"<program filename=\"\" label=\"{part.Name}\" physical_partition_number=\"{lun}\" start_sector=\"{part.StartSector}\" num_partition_sectors=\"{part.SectorCount}\" SECTOR_SIZE_IN_BYTES=\"{sectorSize}\" />");
                }
            }

            // 分区表节点必须放在最后，与参考实现顺序一致（先分区数据后 GPT）。
            AppendGptNodes(rawProgram, lun, sectorSize, mainOk, backupOk);

            rawProgram.AppendLine("</data>");
            var rawProgramPath = Path.Combine(origDir, $"rawprogram{lun}.xml");
            await File.WriteAllTextAsync(rawProgramPath, rawProgram.ToString(), cancellationToken)
                .ConfigureAwait(false);
            allXmls.Add(rawProgramPath);
        }

        partitionXml.AppendLine("</configuration>");
        var partitionXmlPath = Path.Combine(origDir, "partition.xml");
        await File.WriteAllTextAsync(partitionXmlPath, partitionXml.ToString(), cancellationToken)
            .ConfigureAwait(false);

        // 把 rawprogram 复制到 images，让整个目录自成一套可刷的包。
        foreach (var xml in allXmls)
        {
            File.Copy(xml, Path.Combine(imagesDir, Path.GetFileName(xml)), overwrite: true);
        }

        await WriteManifestAsync(target, request, memory, all, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new BackupProgress("备份完成", 1.0));
        _logger.Info($"全分区备份完成：{backedUp}/{total} 个分区 → {target}");

        return BackupResult.Ok(
            $"已备份 {backedUp}/{total} 个分区到 {Path.GetFileName(target)}", target);
    }

    /// <summary>
    /// 用 gpttool 的 <c>print:qcedl_partitionxml</c> 生成 &lt;partition&gt; 段。
    /// </summary>
    private async Task<IReadOnlyList<string>> GetPartitionXmlAsync(
        string gptMainPath, CancellationToken cancellationToken)
    {
        var exe = _tools.FindTool("gpttool.exe");
        if (exe is null || !File.Exists(gptMainPath))
        {
            return [];
        }

        var result = await _runner.RunAsync(
            exe,
            ["-p", gptMainPath, "-f", "print:qcedl_partitionxml"],
            timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return [];
        }

        var nodes = new List<string>();
        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var line = raw.Trim();
            var index = line.IndexOf("<partition ", StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            // 未分配空间（Unalloc_xxx）不是真分区，写进 partition.xml 会让 ptool 困惑。
            if (line.Contains("Unalloc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            nodes.Add(line[index..].TrimEnd());
        }

        return nodes;
    }

    private static void AppendGptNodes(
        StringBuilder rawProgram, int lun, int sectorSize, bool mainOk, bool backupOk)
    {
        // 与参考实现 mzbox.bat:EDLREADALL-4 的常量完全一致：
        // ufs 主 6 备 5（从 0 开始 / NUM_DISK_SECTORS-5.），其余主 34 备 33（末尾那个点号是语法）。
        var isUfs = sectorSize == 4096;
        var mainCount = isUfs ? 6 : 34;
        var backupCount = isUfs ? 5 : 33;
        var backupStart = isUfs ? "NUM_DISK_SECTORS-5." : "NUM_DISK_SECTORS-33.";

        if (mainOk)
        {
            rawProgram.AppendLine(
                $"<program filename=\"gpt_main{lun}.bin\" label=\"PrimaryGPT\" physical_partition_number=\"{lun}\" start_sector=\"0\" num_partition_sectors=\"{mainCount}\" SECTOR_SIZE_IN_BYTES=\"{sectorSize}\" />");
        }

        if (backupOk)
        {
            rawProgram.AppendLine(
                $"<program filename=\"gpt_backup{lun}.bin\" label=\"BackupGPT\" physical_partition_number=\"{lun}\" start_sector=\"{backupStart}\" num_partition_sectors=\"{backupCount}\" SECTOR_SIZE_IN_BYTES=\"{sectorSize}\" />");
        }
    }

    /// <summary>
    /// 回读一份分区表并转成 gptmain / gptbackup。
    /// 这里必须读原始 512KB 再 <c>gpttool convert</c>，原始数据不能直接解析。
    /// </summary>
    private async Task<bool> ReadGptAsync(
        string port,
        StorageKind memory,
        string work,
        int lun,
        string outputType,
        string destination,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var raw = Path.Combine(work, $"gpt_{lun}_{outputType}_raw.bin");
        var sectorSize = memory.SectorSize();
        var sectorCount = Math.Max(1, 512 * 1024 / sectorSize);

        var ok = await _reader.ReadSectorRangeAsync(
            0, sectorCount, lun, raw, port, memory, work,
            ToFlashProgress(progress), cancellationToken).ConfigureAwait(false);

        if (!ok || !File.Exists(raw))
        {
            return false;
        }

        var exe = _tools.FindTool("gpttool.exe");
        if (exe is null)
        {
            return false;
        }

        var convert = await _runner.RunAsync(
            exe,
            ["-p", raw, "-f", $"convert:{outputType}", "-o", destination],
            timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return convert.Success && File.Exists(destination);
    }

    // ------------------------------------------------------------------
    // 分区表 / 单分区
    // ------------------------------------------------------------------

    private async Task<BackupResult> BackupTableAsync(
        BackupRequest request,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var port = await ResolvePortAsync(request.Port, cancellationToken).ConfigureAwait(false);
        if (port is null)
        {
            return BackupResult.Fail("未检测到 9008 端口");
        }

        var target = CreateSessionDirectory(request);
        Directory.CreateDirectory(target);
        var work = CreateWorkDirectory();
        var memory = await ResolveMemoryAsync(port, request.Memory, work, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new BackupProgress("正在回读分区表…"));

        var written = 0;
        // LUN 数量：UFS 一般 6 个，eMMC 1 个；这里按上限探，读不到就停。
        var maxLun = memory == StorageKind.Ufs ? 8 : 1;
        for (var lun = 0; lun < maxLun; lun++)
        {
            var main = Path.Combine(target, $"gpt_main{lun}.bin");
            if (!await ReadGptAsync(
                    port, memory, work, lun, "gptmain", main, progress, cancellationToken)
                .ConfigureAwait(false))
            {
                break;
            }

            written++;
            await ReadGptAsync(
                port, memory, work, lun, "gptbackup",
                Path.Combine(target, $"gpt_backup{lun}.bin"), progress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (written == 0)
        {
            return BackupResult.Fail("分区表回读失败", target);
        }

        return BackupResult.Ok($"已备份 {written} 份分区表到 {Path.GetFileName(target)}", target);
    }

    private async Task<BackupResult> BackupSingleAsync(
        BackupRequest request,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PartitionName))
        {
            return BackupResult.Fail("未指定要备份的分区");
        }

        var port = await ResolvePortAsync(request.Port, cancellationToken).ConfigureAwait(false);
        if (port is null)
        {
            return BackupResult.Fail("未检测到 9008 端口");
        }

        var target = CreateSessionDirectory(request);
        Directory.CreateDirectory(target);

        var work = CreateWorkDirectory();
        var memory = await ResolveMemoryAsync(port, request.Memory, work, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new BackupProgress("正在定位分区…"));

        var all = await _reader
            .ReadPartitionsAsync(port, memory, work, ToFlashProgress(progress), cancellationToken)
            .ConfigureAwait(false);

        var part = _partitions.Find(all, request.PartitionName);
        if (part is null)
        {
            return BackupResult.Fail($"分区表里找不到 {request.PartitionName}", target);
        }

        var destination = Path.Combine(target, $"{part.Name}.img");
        var result = await _reader.ReadPartitionAsync(
            part, destination, port, memory, work,
            ToFlashProgress(progress), cancellationToken).ConfigureAwait(false);

        return result.Success
            ? BackupResult.Ok($"已备份 {part.Name}（{part.SizeText}）", target)
            : BackupResult.Fail(result.Message, target);
    }

    // ------------------------------------------------------------------
    // QCN
    // ------------------------------------------------------------------

    private async Task<BackupResult> BackupQcnAsync(
        BackupRequest request,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var exe = _tools.FindTool("QCNTool.exe");
        if (exe is null)
        {
            return BackupResult.Fail("找不到 QCNTool.exe。请确认工具目录里有它。");
        }

        var port = await ResolveDiagPortAsync(request.Port, cancellationToken).ConfigureAwait(false);
        if (port is null)
        {
            return BackupResult.Fail(
                "未找到 QCN 端口。请先用拨号盘输入 *#*#88#*#* 选择 QPST，或 *#*#3838664#*#* 选择 usb mode 后再试。");
        }

        var target = CreateSessionDirectory(request);
        Directory.CreateDirectory(target);

        var fileName = $"qcnbak_{DateTime.Now:yyyyMMdd_HHmmss}";

        progress?.Report(new BackupProgress("正在从设备读取 QCN…"));

        // 读：-f 是目录、-n 是文件名（与写操作的参数格式不同，这是参考实现就有的差异）。
        var result = await _runner.RunAsync(
            exe,
            ["-r", "-p", port, "-f", target, "-n", fileName],
            timeout: ReadTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var combined = result.Combined;
        if (!combined.Contains("Reading QCN from phone... OK", StringComparison.OrdinalIgnoreCase))
        {
            return BackupResult.Fail($"QCN 读取失败：{FirstLine(combined)}", target);
        }

        // 注意：不要把 QCN 工具的输出原样写进日志 —— 里面有 IMEI 等隐私信息。
        _logger.Info($"QCN 已备份到 {target}");

        return BackupResult.Ok("QCN 备份完成", target);
    }

    public async Task<BackupResult> RestoreQcnAsync(
        string? port,
        string qcnFilePath,
        IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var exe = _tools.FindTool("QCNTool.exe");
        if (exe is null)
        {
            return BackupResult.Fail("找不到 QCNTool.exe");
        }

        if (!File.Exists(qcnFilePath))
        {
            return BackupResult.Fail($"文件不存在：{qcnFilePath}");
        }

        var resolved = await ResolveDiagPortAsync(port, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return BackupResult.Fail("未找到 QCN 端口，请先让设备进入 DIAG 模式");
        }

        progress?.Report(new BackupProgress("正在写入 QCN…"));

        // 写：-f 是完整路径（与读操作的格式不同）。
        var result = await _runner.RunAsync(
            exe,
            ["-w", "-p", resolved, "-f", qcnFilePath],
            timeout: ReadTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var combined = result.Combined;
        if (!combined.Contains("Writing Data File to phone... OK", StringComparison.OrdinalIgnoreCase))
        {
            return BackupResult.Fail($"QCN 写入失败：{FirstLine(combined)}");
        }

        return BackupResult.Ok("QCN 恢复完成，请重启设备");
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    /// <summary>
    /// 建立一个带机型与时间戳的会话目录，避免多次备份互相覆盖。
    /// 参考实现命名：<c>&lt;product&gt;_MEIZUBOX_ParBak_&lt;time&gt;</c>。
    /// </summary>
    private static string CreateSessionDirectory(BackupRequest request)
    {
        var product = string.IsNullOrWhiteSpace(request.Product) ? "unknown" : request.Product;
        var suffix = request.Scope switch
        {
            BackupScope.Qcn => "QcnBak",
            BackupScope.PartitionTable => "GptBak",
            BackupScope.SinglePartition => $"{request.PartitionName}Bak",
            _ => "ParBak",
        };

        var name = $"{product}_{suffix}_{DateTime.Now:yyyyMMdd_HHmmss}";
        var full = Path.Combine(request.TargetDirectory, name);
        Directory.CreateDirectory(full);
        return full;
    }

    private void CopyReadmeFiles(string romDir)
    {
        var candidates = new[]
        {
            Path.Combine(_tools.ToolsDirectory, "qctool", "ReadAllFunc_Readme_Chs.txt"),
            Path.Combine(_tools.ToolsDirectory, "ReadAllFunc_Readme_Chs.txt"),
        };

        foreach (var source in candidates.Where(File.Exists))
        {
            try
            {
                File.Copy(source, Path.Combine(romDir, "本目录说明.txt"), overwrite: true);
                break;
            }
            catch (IOException)
            {
                // 说明文件复制失败不影响备份本身。
            }
        }
    }

    private async Task WriteManifestAsync(
        string target,
        BackupRequest request,
        StorageKind memory,
        IReadOnlyList<GptPartition> partitions,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 魅友工具箱备份清单");
        sb.AppendLine();
        sb.AppendLine($"- 机型：{request.Product ?? "未知"}");
        sb.AppendLine($"- 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 存储类型：{memory.Display()}");
        sb.AppendLine($"- 分区数：{partitions.Count}");
        sb.AppendLine();
        sb.AppendLine("## 注意");
        sb.AppendLine();
        sb.AppendLine("- 本备份包本身就是一个 9008 线刷包，`images/` 里是镜像，`for_ROM_maker/orig/` 里是原始 XML。");
        sb.AppendLine("- 包里含基带、串号等本机独有数据，**只能用于同一台设备**，刷到别的机器上会导致无信号。");
        sb.AppendLine("- 不含用户数据（userdata / last_parti 默认不备份）。");
        sb.AppendLine("- `persist` 分区默认不备份：覆盖它会让指纹与传感器**永久失效且不可修复**。");
        sb.AppendLine("- 魅族的 `private` / `mzkv` 分区内容决定 Bootloader 锁状态。**解锁前做的备份，在解锁后使用会重新上锁**。");
        sb.AppendLine();
        sb.AppendLine("## 分区一览");
        sb.AppendLine();
        sb.AppendLine("| LUN | 分区 | 起始扇区 | 扇区数 | 大小 |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var p in partitions.OrderBy(p => p.Lun).ThenBy(p => p.StartSector))
        {
            var skipped = request.SkipPartitions.Contains(p.Name) ? "（已跳过）" : string.Empty;
            sb.AppendLine(
                string.Create(CultureInfo.InvariantCulture,
                    $"| {p.Lun} | {p.Name}{skipped} | {p.StartSector} | {p.SectorCount} | {p.SizeText} |"));
        }

        await File.WriteAllTextAsync(
            Path.Combine(target, "备份说明.md"), sb.ToString(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string?> ResolvePortAsync(string? port, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(port))
        {
            return Normalize(port);
        }

        var ports = await _firehose.ListPortsAsync(cancellationToken).ConfigureAwait(false);
        return ports.Count == 0 ? null : ports[0];
    }

    /// <summary>QCN 走 DIAG，不带 COM 前缀；端口号本身就是数字。</summary>
    private async Task<string?> ResolveDiagPortAsync(string? port, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(port))
        {
            return Normalize(port);
        }

        var ports = await _firehose.ListPortsAsync(cancellationToken).ConfigureAwait(false);
        return ports.Count == 0 ? null : ports[0];
    }

    private static string Normalize(string port)
    {
        var trimmed = port.Trim();
        if (trimmed.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[3..];
        }

        return trimmed;
    }

    /// <summary>
    /// 存储类型为 auto 时逐个试探。参考实现先试 ufs 再试 emmc ——
    /// 部分 UFS 设备先按 emmc 取会掉端口，所以顺序不能反。
    /// </summary>
    private async Task<StorageKind> ResolveMemoryAsync(
        string port,
        StorageKind requested,
        string work,
        CancellationToken cancellationToken)
    {
        if (requested != StorageKind.Auto)
        {
            return requested;
        }

        foreach (var kind in new[] { StorageKind.Ufs, StorageKind.Emmc, StorageKind.Spinor })
        {
            var probe = await _reader
                .ReadPartitionsAsync(port, kind, work, null, cancellationToken)
                .ConfigureAwait(false);

            if (probe.Count > 0)
            {
                _logger.Info($"自动识别存储类型：{kind.Display()}");
                return kind;
            }
        }

        _logger.Warn("存储类型自动识别失败，回退到 ufs");
        return StorageKind.Ufs;
    }

    private static IProgress<FlashProgress>? ToFlashProgress(IProgress<BackupProgress>? progress) =>
        progress is null
            ? null
            : new Progress<FlashProgress>(p => progress.Report(
                new BackupProgress(p.Message, p.Percent)));

    private static string CreateWorkDirectory()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "MeiyouToolbox", "backup",
            DateTime.Now.ToString("HHmmssfff"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? string.Empty;
}
