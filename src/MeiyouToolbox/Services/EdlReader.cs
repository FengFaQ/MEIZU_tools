using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>
/// 9008 下的"读取"基础设施：读 GPT、按分区名回读、读槽位。
/// <para>
/// 单独抽出来是因为刷机页与备份页都要用，而且参考实现里这段逻辑分散在
/// <c>partable.bat:readgpt</c>、<c>read.bat:qcedl</c>、<c>slot.bat:chk-qcedl</c>
/// 三处，各自复制了一遍"读 512KB → 转换 → 解析"的流程。
/// </para>
/// <para>
/// <b>这里修掉了刷机页原实现的一个致命缺陷</b>：<c>ReadPartitionsAsync</c> 与
/// <c>ReadPartitionRawAsync</c> 互相调用（前者读 GPT 时调后者，后者找分区时又调前者），
/// 一旦真的去读分区表就会无限递归直到栈溢出。现在 GPT 读取走本类，
/// 分区回读不再回头查分区表，递归链被彻底切断。
/// </para>
/// </summary>
public interface IEdlReader
{
    /// <summary>
    /// 读取全部 LUN 的分区表。返回的分区带正确的 <see cref="GptPartition.Lun"/>。
    /// </summary>
    Task<IReadOnlyList<GptPartition>> ReadPartitionsAsync(
        string port,
        StorageKind memory,
        string workDirectory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>按分区名回读原始镜像。</summary>
    Task<FlashResult> ReadPartitionAsync(
        GptPartition partition,
        string destination,
        string port,
        StorageKind memory,
        string workDirectory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读一段连续扇区（不查分区表）。GPT 就是这样读的：主表固定在扇区 0。
    /// </summary>
    Task<bool> ReadSectorRangeAsync(
        long startSector,
        long sectorCount,
        int lun,
        string destination,
        string port,
        StorageKind memory,
        string workDirectory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>回读当前活动槽位（A/B 设备）。返回 <c>a</c> / <c>b</c> / <c>aonly</c> / <c>unknown</c>。</summary>
    Task<string> ReadSlotAsync(
        string port,
        StorageKind memory,
        string workDirectory,
        CancellationToken cancellationToken = default);
}

public sealed class EdlReader : IEdlReader
{
    /// <summary>参考实现读分区表固定读前 512KB（不是精确 GPT 大小），再用 gpttool 转换。</summary>
    private const long GptReadBytes = 512 * 1024;

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(20);

    private readonly IProcessRunner _runner;
    private readonly IToolLocator _tools;
    private readonly IFirehoseService _firehose;
    private readonly IPartitionTableService _partitions;
    private readonly IAppLogger _logger;

    public EdlReader(
        IProcessRunner runner,
        IToolLocator tools,
        IFirehoseService firehose,
        IPartitionTableService partitions,
        IAppLogger logger)
    {
        _runner = runner;
        _tools = tools;
        _firehose = firehose;
        _partitions = partitions;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GptPartition>> ReadPartitionsAsync(
        string port,
        StorageKind memory,
        string workDirectory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<GptPartition>();
        var sectorSize = memory.SectorSize();
        var sectorCount = Math.Max(1, GptReadBytes / sectorSize);

        // LUN 数量：UFS 最多 8 个，eMMC / spinor 只有 1 个。
        var maxLun = memory == StorageKind.Ufs ? 8 : 1;

        for (var lun = 0; lun < maxLun; lun++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new FlashProgress($"正在读取分区表 LUN {lun}…"));

            var rawPath = Path.Combine(workDirectory, $"lun{lun}_raw.bin");
            var gptPath = Path.Combine(workDirectory, $"gpt_main{lun}.bin");

            var ok = await ReadSectorRangeAsync(
                startSector: 0,
                sectorCount: sectorCount,
                lun: lun,
                destination: rawPath,
                port: port,
                memory: memory,
                workDirectory: workDirectory,
                progress: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!ok || !File.Exists(rawPath) || new FileInfo(rawPath).Length < 1024)
            {
                // 第一个 LUN 都读不到说明存储类型判断错了，后面也不用试。
                break;
            }

            // 原始 512KB 不是纯 GPT，必须让 gpttool 转成 gptmain 格式。
            if (!await ConvertToGptMainAsync(rawPath, gptPath, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            var parsed = _partitions.ParseGptDump(gptPath, memory);
            if (parsed.Count == 0)
            {
                break;
            }

            result.AddRange(parsed.Select(p => p with { Lun = lun, SectorSize = sectorSize }));
        }

        _logger.Info($"分区表读取完成：{result.Count} 个分区");
        return result;
    }

    public async Task<FlashResult> ReadPartitionAsync(
        GptPartition partition,
        string destination,
        string port,
        StorageKind memory,
        string workDirectory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var xmlPath = Path.Combine(workDirectory, $"read-{partition.Name}.xml");
        var node = _partitions.BuildProgramNode(
            partition, Path.GetFileName(destination), memory, sparse: false);
        await File.WriteAllTextAsync(
            xmlPath, $"<?xml version=\"1.0\" ?><data>{node}</data>", cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new FlashProgress(
            $"正在回读 {partition.Name}（{partition.SizeText}）…"));

        var options = new EdlSessionOptions
        {
            Port = port,
            Memory = memory,
            WorkDirectory = workDirectory,
        };

        var ok = await _firehose.SendXmlAsync(
            options,
            [xmlPath],
            directory ?? workDirectory,
            convertToRead: true,
            progress: ToEdlProgress(progress),
            cancellationToken: cancellationToken,
            outputDirectory: directory ?? workDirectory).ConfigureAwait(false);

        if (!ok || !File.Exists(destination))
        {
            return FlashResult.Fail($"回读 {partition.Name} 失败");
        }

        var size = new FileInfo(destination).Length;
        if (size == 0)
        {
            return FlashResult.Fail($"回读 {partition.Name} 得到空文件");
        }

        return FlashResult.Ok($"已回读 {partition.Name}（{size / 1024.0 / 1024:F1} MB）");
    }

    public async Task<bool> ReadSectorRangeAsync(
        long startSector,
        long sectorCount,
        int lun,
        string destination,
        string port,
        StorageKind memory,
        string workDirectory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var xmlPath = Path.Combine(workDirectory, $"readsec-{lun}-{startSector}.xml");
        var node =
            $"""
            <program filename="{Path.GetFileName(destination)}" physical_partition_number="{lun}" label="Segment" start_sector="{startSector}" num_partition_sectors="{sectorCount}" SECTOR_SIZE_IN_BYTES="{memory.SectorSize()}" sparse="false" />
            """;
        await File.WriteAllTextAsync(
            xmlPath, $"<?xml version=\"1.0\" ?><data>{node}</data>", cancellationToken)
            .ConfigureAwait(false);

        var options = new EdlSessionOptions
        {
            Port = port,
            Memory = memory,
            WorkDirectory = workDirectory,
        };

        return await _firehose.SendXmlAsync(
            options,
            [xmlPath],
            directory ?? workDirectory,
            convertToRead: true,
            progress: ToEdlProgress(progress),
            cancellationToken: cancellationToken,
            outputDirectory: directory ?? workDirectory).ConfigureAwait(false);
    }

    /// <summary>
    /// 读当前槽位。参考实现靠 <c>gpttool -f print:default:#inf:sector:10</c> 输出里的
    /// <c>[BasicInfo]Slot_Current</c> 行判断，<c>nonexistent</c> 表示非 A/B 设备。
    /// </summary>
    public async Task<string> ReadSlotAsync(
        string port,
        StorageKind memory,
        string workDirectory,
        CancellationToken cancellationToken = default)
    {
        var exe = _tools.FindTool("gpttool.exe");
        if (exe is null)
        {
            _logger.Warn("找不到 gpttool.exe，无法判断槽位");
            return "unknown";
        }

        var sectorSize = memory.SectorSize();
        var sectorCount = Math.Max(1, GptReadBytes / sectorSize);
        var rawPath = Path.Combine(workDirectory, "slot_raw.bin");

        var ok = await ReadSectorRangeAsync(
            0, sectorCount, 0, rawPath, port, memory, workDirectory,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!ok || !File.Exists(rawPath))
        {
            return "unknown";
        }

        var result = await _runner.RunAsync(
            exe,
            ["-p", rawPath, "-f", "print:default:#inf:sector:10"],
            workingDirectory: workDirectory,
            timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = result.StandardOutput + "\n" + result.StandardError;

        // "nonexistent" 是非 A/B 设备；"undefined" 也表示单槽。
        if (text.Contains("Slot_Current nonexistent", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Slot_Current undefined", StringComparison.OrdinalIgnoreCase))
        {
            return "aonly";
        }

        if (text.Contains("Slot_Current a ", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Slot_Current a\r", StringComparison.OrdinalIgnoreCase))
        {
            return "a";
        }

        if (text.Contains("Slot_Current b ", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Slot_Current b\r", StringComparison.OrdinalIgnoreCase))
        {
            return "b";
        }

        return "unknown";
    }

    /// <summary>
    /// 把 9008 回读出来的原始 512KB 转成 gptmain 格式。
    /// 参考实现：<c>gpttool -p tmp.bin -f convert:gptmain -o gpt_main&lt;n&gt;.bin</c>。
    /// </summary>
    private async Task<bool> ConvertToGptMainAsync(
        string rawPath, string gptPath, CancellationToken cancellationToken)
    {
        var exe = _tools.FindTool("gpttool.exe");
        if (exe is null)
        {
            _logger.Warn("找不到 gpttool.exe，无法转换分区表");
            return false;
        }

        var result = await _runner.RunAsync(
            exe,
            ["-p", rawPath, "-f", "convert:gptmain", "-o", gptPath],
            timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Success || !File.Exists(gptPath))
        {
            _logger.Warn($"分区表转换失败：{FirstLines(result.Combined)}");
            return false;
        }

        return true;
    }

    private static IProgress<EdlProgress>? ToEdlProgress(IProgress<FlashProgress>? progress) =>
        progress is null
            ? null
            : new Progress<EdlProgress>(p => progress.Report(
                new FlashProgress(p.Message, p.Percent)));

    private static string FirstLines(string text) =>
        string.Join(" | ", text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(3));

    internal static TimeSpan ReadOperationTimeout => ReadTimeout;
}
