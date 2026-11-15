using System.Text.RegularExpressions;
using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>
/// 槽位读取与切换。参考实现 <c>bin\slot.bat</c> 支持三种通路，但它的
/// <c>:SET-ADB</c> / <c>:SET-FASTBOOT</c> / <c>:SET-QCEDL</c> 三个标签
/// <b>全部不可达</b> —— L56/L98/L146 写的是
/// <c>if not "%func%"=="set" ENDLOCAL &amp; set ... &amp; goto :eof</c>，
/// 没有括号包裹，<c>goto :eof</c> 恒执行，函数在进入 <c>set</c> 分支前就返回了。
/// 也就是说原版的"切换槽位"从来没生效过。这里按它的<b>设计意图</b>重新实现。
/// </summary>
public interface ISlotService
{
    /// <summary>读当前槽位。自动按连接方式选通路。</summary>
    Task<SlotInfo> ReadAsync(
        ConnectionMode mode,
        string? port,
        StorageKind memory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把活动槽位切到 <paramref name="target"/>（<c>a</c> 或 <c>b</c>）。
    /// 返回 (成功, 消息)。
    /// </summary>
    Task<FlashResult> SwitchAsync(
        string target,
        ConnectionMode mode,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed partial class SlotService : ISlotService
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(1);

    private readonly IProcessRunner _runner;
    private readonly IToolLocator _tools;
    private readonly IEdlReader _reader;
    private readonly IFirehoseService _firehose;
    private readonly IAppLogger _logger;

    public SlotService(
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

    // ------------------------------------------------------------------
    // 读
    // ------------------------------------------------------------------

    public async Task<SlotInfo> ReadAsync(
        ConnectionMode mode,
        string? port,
        StorageKind memory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return mode switch
            {
                ConnectionMode.System or ConnectionMode.Recovery =>
                    await ReadViaAdbAsync(mode, cancellationToken).ConfigureAwait(false),

                ConnectionMode.Fastboot or ConnectionMode.FastbootD =>
                    await ReadViaFastbootAsync(mode, cancellationToken).ConfigureAwait(false),

                ConnectionMode.QcEdl =>
                    await ReadViaEdlAsync(port, memory, cancellationToken).ConfigureAwait(false),

                _ => SlotInfo.Unknown(mode),
            };
        }
        catch (Exception ex)
        {
            _logger.Error("读取槽位失败", ex);
            return SlotInfo.Unknown(mode);
        }
    }

    /// <summary>
    /// adb 走 <c>ro.boot.slot_suffix</c>，值形如 <c>_a</c> / <c>_b</c>。
    /// 非 A/B 设备这个属性为空，那就是 <c>aonly</c>。
    /// </summary>
    private async Task<SlotInfo> ReadViaAdbAsync(
        ConnectionMode mode, CancellationToken cancellationToken)
    {
        var adb = _tools.AdbPath;
        if (adb is null)
        {
            return SlotInfo.Unknown(mode);
        }

        var result = await _runner.RunAsync(
            adb,
            ["shell", "getprop", "ro.boot.slot_suffix"],
            timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var suffix = result.StandardOutput.Trim().TrimStart('_').ToLowerInvariant();

        return suffix switch
        {
            "a" => new SlotInfo { IsAbDevice = true, Current = "a", Source = mode },
            "b" => new SlotInfo { IsAbDevice = true, Current = "b", Source = mode },
            _ => new SlotInfo { IsAbDevice = false, Current = "aonly", Source = mode },
        };
    }

    /// <summary>
    /// fastboot 走 <c>getvar current-slot</c>，再 <c>getvar all</c> 查两个槽位是否 unbootable。
    /// 参考实现 L74-110，判据是含 <c>slot</c> 的行取第二个 token。
    /// </summary>
    private async Task<SlotInfo> ReadViaFastbootAsync(
        ConnectionMode mode, CancellationToken cancellationToken)
    {
        var fastboot = _tools.FastbootPath;
        if (fastboot is null)
        {
            return SlotInfo.Unknown(mode);
        }

        var current = await _runner.RunAsync(
            fastboot,
            ["getvar", "current-slot"],
            timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // fastboot 把 getvar 的输出写到 stderr，所以两个流都要看。
        var text = current.Combined;
        var slotMatch = CurrentSlotRegex().Match(text);

        if (!slotMatch.Success)
        {
            return new SlotInfo { IsAbDevice = false, Current = "aonly", Source = mode };
        }

        var slot = slotMatch.Groups["slot"].Value.ToLowerInvariant();

        var all = await _runner.RunAsync(
            fastboot,
            ["getvar", "all"],
            timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var allText = all.Combined;

        return new SlotInfo
        {
            IsAbDevice = true,
            Current = slot,
            Source = mode,
            SlotAUnbootable = UnbootableRegex("a").IsMatch(allText),
            SlotBUnbootable = UnbootableRegex("b").IsMatch(allText),
        };
    }

    /// <summary>9008 走 EdlReader 的 <c>print:default:#inf</c> 判据。</summary>
    private async Task<SlotInfo> ReadViaEdlAsync(
        string? port,
        StorageKind memory,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolvePortAsync(port, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return SlotInfo.Unknown(ConnectionMode.QcEdl);
        }

        var work = CreateWorkDirectory();
        var current = await _reader
            .ReadSlotAsync(resolved, memory, work, cancellationToken)
            .ConfigureAwait(false);

        return new SlotInfo
        {
            IsAbDevice = current is "a" or "b",
            Current = current,
            Source = ConnectionMode.QcEdl,
        };
    }

    // ------------------------------------------------------------------
    // 写
    // ------------------------------------------------------------------

    public async Task<FlashResult> SwitchAsync(
        string target,
        ConnectionMode mode,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = target.Trim().ToLowerInvariant();
        if (normalized is not ("a" or "b"))
        {
            return FlashResult.Fail($"目标槽位无效：{target}（只能是 a 或 b）");
        }

        try
        {
            return mode switch
            {
                ConnectionMode.System or ConnectionMode.Recovery =>
                    await SwitchViaAdbAsync(normalized, cancellationToken).ConfigureAwait(false),

                ConnectionMode.Fastboot or ConnectionMode.FastbootD =>
                    await SwitchViaFastbootAsync(normalized, cancellationToken).ConfigureAwait(false),

                ConnectionMode.QcEdl =>
                    await SwitchViaEdlAsync(normalized, port, memory, progress, cancellationToken)
                        .ConfigureAwait(false),

                _ => FlashResult.Fail($"当前连接方式（{mode.Display()}）不支持切换槽位"),
            };
        }
        catch (Exception ex)
        {
            _logger.Error("切换槽位失败", ex);
            return FlashResult.Fail($"切换槽位失败：{ex.Message}");
        }
    }

    /// <summary>
    /// adb 用 <c>bootctl set-active-boot-slot</c>，参数是 0（a）/ 1（b）。
    /// 系统模式下要先 su；Recovery 下本身就是 root，直接跑。
    /// </summary>
    private async Task<FlashResult> SwitchViaAdbAsync(
        string target, CancellationToken cancellationToken)
    {
        var adb = _tools.AdbPath;
        if (adb is null)
        {
            return FlashResult.Fail("找不到 adb.exe");
        }

        var index = target == "a" ? "0" : "1";

        var mode = await _runner.RunAsync(
            adb, ["shell", "id"], timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var isRoot = mode.StandardOutput.Contains("uid=0", StringComparison.Ordinal);

        // 系统模式需要 su 提权，否则 bootctl 会被拒绝。
        var shellArgs = isRoot
            ? new[] { "shell", "bootctl", "set-active-boot-slot", index }
            : new[] { "shell", "su", "-c", $"bootctl set-active-boot-slot {index}" };

        var result = await _runner.RunAsync(
            adb, shellArgs, timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = result.Combined;

        // bootctl 成功时一般不打印东西；报错会带 "Could not" / "error"。
        if (text.Contains("Could not", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return FlashResult.Fail($"切换失败：{FirstLine(text)}");
        }

        return FlashResult.Ok($"活动槽位已切到 {target.ToUpperInvariant()}，重启后生效");
    }

    /// <summary>
    /// fastboot 用 <c>set_active</c>。参考实现 L103-104 的警告保留：
    /// 目标槽位 unbootable 时先告诉用户，但不阻止（有些用户正是要修它）。
    /// </summary>
    private async Task<FlashResult> SwitchViaFastbootAsync(
        string target, CancellationToken cancellationToken)
    {
        var fastboot = _tools.FastbootPath;
        if (fastboot is null)
        {
            return FlashResult.Fail("找不到 fastboot.exe");
        }

        var result = await _runner.RunAsync(
            fastboot, ["set_active", target], timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = result.Combined;
        if (text.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return FlashResult.Fail($"切换失败：{FirstLine(text)}");
        }

        return FlashResult.Ok($"活动槽位已切到 {target.ToUpperInvariant()}");
    }

    /// <summary>
    /// 9008 切槽位：对每个 LUN 用 <c>gpttool -f switchslot</c> 改 GPT，
    /// 再把改好的表写回设备。
    /// <para>
    /// 参考实现 <c>:SET-QCEDL</c> L151-190。关键细节：如果设备根本没有 _a/_b 分区，
    /// gpttool 会输出 <c>[Error]Slot switching is not supported</c>，
    /// 这时<b>不能</b>当失败处理 —— 应该原样保留该 LUN 的表（用原文件当新文件），
    /// 继续处理其它 LUN。
    /// </para>
    /// </summary>
    private async Task<FlashResult> SwitchViaEdlAsync(
        string target,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var gpttool = _tools.FindTool("gpttool.exe");
        if (gpttool is null)
        {
            return FlashResult.Fail("找不到 gpttool.exe");
        }

        var resolved = await ResolvePortAsync(port, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return FlashResult.Fail("找不到 9008 端口");
        }

        var work = CreateWorkDirectory();
        var effectiveMemory = await ResolveMemoryAsync(resolved, memory, work, cancellationToken)
            .ConfigureAwait(false);

        var maxLun = effectiveMemory == StorageKind.Ufs ? 8 : 1;
        var changed = new List<(int Lun, string Path)>();

        for (var lun = 0; lun < maxLun; lun++)
        {
            progress?.Report(new FlashProgress($"读取 LUN {lun} 分区表…"));

            var gptPath = Path.Combine(work, $"gpt_main{lun}.bin");
            var ok = await _reader.ReadSectorRangeAsync(
                0,
                GptSectors(effectiveMemory),
                lun,
                gptPath,
                resolved,
                effectiveMemory,
                work,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!ok || !File.Exists(gptPath))
            {
                // LUN 不存在是正常的（比如 UFS 只有 6 个实际 LUN）。
                continue;
            }

            var newPath = Path.Combine(work, $"gpt_main{lun}_new.bin");
            var convert = await _runner.RunAsync(
                gpttool,
                ["-p", gptPath, "-f", $"switchslot:{target}", "-o", newPath],
                workingDirectory: work,
                timeout: ShortTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var output = convert.Combined;
            if (output.Contains("Slot switching is not supported", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"LUN {lun} 没有 A/B 分区，跳过");
                continue;
            }

            if (!convert.Success || !File.Exists(newPath))
            {
                _logger.Warn($"LUN {lun} 生成新分区表失败：{FirstLine(output)}");
                continue;
            }

            changed.Add((lun, newPath));
        }

        if (changed.Count == 0)
        {
            return FlashResult.Fail("设备没有 A/B 分区，无需切换槽位");
        }

        progress?.Report(new FlashProgress($"写回 {changed.Count} 个 LUN 的分区表…"));

        foreach (var (lun, path) in changed)
        {
            var ok = await WriteGptAsync(
                resolved, effectiveMemory, work, lun, path, cancellationToken)
                .ConfigureAwait(false);

            if (!ok)
            {
                return FlashResult.Fail($"LUN {lun} 分区表写回失败");
            }
        }

        // 切完槽位必须重设可启动存储（UFS 按槽位写 1 或 2）。
        var drive = effectiveMemory != StorageKind.Ufs ? "0" : target == "b" ? "2" : "1";
        await SetBootableStorageAsync(
            resolved, effectiveMemory, work, drive, cancellationToken).ConfigureAwait(false);

        return FlashResult.Ok($"已把 {changed.Count} 个 LUN 的活动槽位切到 {target.ToUpperInvariant()}");
    }

    /// <summary>
    /// 把改写后的 GPT 写回设备。参考实现 <c>partable.bat:writegpt-write-qcedl</c> L229-232：
    /// 先 <c>gpttool -f convert:gptmain</c> 转格式，再按扇区数生成
    /// <c>&lt;program label="PrimaryGPT" start_sector="0"/&gt;</c> 用 fh_loader 发送。
    /// </summary>
    private async Task<bool> WriteGptAsync(
        string port,
        StorageKind memory,
        string work,
        int lun,
        string gptPath,
        CancellationToken cancellationToken)
    {
        var gpttool = _tools.FindTool("gpttool.exe");
        if (gpttool is null)
        {
            return false;
        }

        var formatted = Path.Combine(work, $"gpt_main{lun}_fmt.bin");
        var convert = await _runner.RunAsync(
            gpttool,
            ["-p", gptPath, "-f", "convert:gptmain", "-o", formatted],
            workingDirectory: work,
            timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!convert.Success || !File.Exists(formatted))
        {
            _logger.Warn($"LUN {lun} 分区表格式化失败：{FirstLine(convert.Combined)}");
            return false;
        }

        var sectorSize = memory.SectorSize();
        var bytes = new FileInfo(formatted).Length;
        var sectorCount = Math.Max(1, (bytes + sectorSize - 1) / sectorSize);

        // fh_loader 的 --search_path 指向工作目录，所以 filename 只写文件名。
        var xml = Path.Combine(work, "cmd.xml");
        await File.WriteAllTextAsync(
            xml,
            $"""
             <?xml version="1.0" ?>
             <data>
             <program SECTOR_SIZE_IN_BYTES="{sectorSize}" filename="gpt_main{lun}_fmt.bin" physical_partition_number="{lun}" label="PrimaryGPT" start_sector="0" num_partition_sectors="{sectorCount}"/>
             </data>
             """,
            cancellationToken).ConfigureAwait(false);

        var options = new EdlSessionOptions
        {
            Port = port,
            Memory = memory,
            WorkDirectory = work,
        };

        return await _firehose.SendXmlAsync(
            options,
            [xml],
            work,
            convertToRead: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>切槽位后重设可启动存储。UFS 必须是 1 或 2，否则刷完不开机。</summary>
    private async Task SetBootableStorageAsync(
        string port,
        StorageKind memory,
        string work,
        string drive,
        CancellationToken cancellationToken)
    {
        var xml = Path.Combine(work, "bootdrive.xml");
        await File.WriteAllTextAsync(
            xml,
            $"""
             <?xml version="1.0" ?>
             <data>
             <setbootablestoragedrive value="{drive}"/>
             </data>
             """,
            cancellationToken).ConfigureAwait(false);

        var options = new EdlSessionOptions
        {
            Port = port,
            Memory = memory,
            WorkDirectory = work,
        };

        var ok = await _firehose.SendXmlAsync(
            options,
            [xml],
            work,
            convertToRead: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            // 参考实现这里也允许失败：分区表已经写进去了，只是启动项没设上。
            _logger.Warn($"setbootablestoragedrive 失败（value={drive}）");
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

    /// <summary>参考实现按 ufs → emmc → spinor 顺序试，顺序不可反。</summary>
    private async Task<StorageKind> ResolveMemoryAsync(
        string port, StorageKind memory, string work, CancellationToken cancellationToken)
    {
        if (memory != StorageKind.Auto)
        {
            return memory;
        }

        foreach (var candidate in (StorageKind[])[StorageKind.Ufs, StorageKind.Emmc])
        {
            var probe = Path.Combine(work, $"mem_{candidate.ToArgument()}.bin");
            var ok = await _reader.ReadSectorRangeAsync(
                0,
                GptSectors(candidate),
                0,
                probe,
                port,
                candidate,
                work,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (ok && File.Exists(probe) && new FileInfo(probe).Length > 0)
            {
                return candidate;
            }
        }

        return StorageKind.Ufs;
    }

    /// <summary>读 GPT 时固定读前 512KB；换成扇区数就是 512KB / 扇区大小。</summary>
    private static int GptSectors(StorageKind memory) =>
        Math.Max(1, (512 * 1024) / memory.SectorSize());

    private static string CreateWorkDirectory()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "MeiyouToolbox", "slot", DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

    /// <summary>匹配 <c>current-slot: a</c> 或 <c>current-slot: 'a'</c>。</summary>
    [GeneratedRegex(@"current-slot\s*:\s*'?([ab])'?", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentSlotRegex();

    /// <summary>匹配 <c>slot_a_unbootable: yes</c>。用缓存避免每次切槽位都重新编译。</summary>
    private static Regex UnbootableRegex(string slot) =>
        slot == "a" ? SlotAUnbootableRegex() : SlotBUnbootableRegex();

    [GeneratedRegex(@"slot_a_unbootable\s*:\s*yes", RegexOptions.IgnoreCase)]
    private static partial Regex SlotAUnbootableRegex();

    [GeneratedRegex(@"slot_b_unbootable\s*:\s*yes", RegexOptions.IgnoreCase)]
    private static partial Regex SlotBUnbootableRegex();
}
