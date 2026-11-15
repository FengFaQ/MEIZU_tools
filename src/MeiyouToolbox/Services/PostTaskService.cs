using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>
/// 一个任务结束后能做的两件事：清空数据并开机、直接开机。
/// 对应参考实现里每个危险操作收尾时那句"是否清空数据"的询问，
/// 但这里把动作本身抽出来，供「完成任务提示」在任何场景复用。
/// </summary>
public interface IPostTaskService
{
    /// <summary>清空用户数据后开机。用于解锁 BL 之类的场景 —— 解锁后不清数据会卡在验证界面。</summary>
    Task<FlashResult> WipeDataAndBootAsync(
        string? port = null,
        StorageKind memory = StorageKind.Auto,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>直接重启到系统，不动数据。</summary>
    Task<FlashResult> BootAsync(
        string? port = null,
        StorageKind memory = StorageKind.Auto,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class PostTaskService : IPostTaskService
{
    private readonly IDeviceService _devices;
    private readonly IFlashService _flash;
    private readonly ICleanService _clean;
    private readonly IProcessRunner _runner;
    private readonly IToolLocator _tools;
    private readonly IAppLogger _logger;

    public PostTaskService(
        IDeviceService devices,
        IFlashService flash,
        ICleanService clean,
        IProcessRunner runner,
        IToolLocator tools,
        IAppLogger logger)
    {
        _devices = devices;
        _flash = flash;
        _clean = clean;
        _runner = runner;
        _tools = tools;
        _logger = logger;
    }

    private static readonly TimeSpan FastbootTimeout = TimeSpan.FromMinutes(10);

    public async Task<FlashResult> BootAsync(
        string? port = null,
        StorageKind memory = StorageKind.Auto,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (info.Mode == ConnectionMode.None)
        {
            return FlashResult.Fail("未检测到设备，无法开机。请确认数据线已连接。");
        }

        if (info.Mode == ConnectionMode.System)
        {
            return FlashResult.Ok("设备已处于开机状态");
        }

        progress?.Report(new FlashProgress($"正在把设备从 {info.Mode.Display()} 重启到系统…"));

        var result = await _flash.RebootAsync(
            info.Mode, "system", port, memory, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            _logger.Warn($"开机失败：{result.Message}");
        }

        return result;
    }

    public async Task<FlashResult> WipeDataAndBootAsync(
        string? port = null,
        StorageKind memory = StorageKind.Auto,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (info.Mode == ConnectionMode.None)
        {
            return FlashResult.Fail("未检测到设备，无法清空数据。请确认数据线已连接。");
        }

        FlashResult wipeResult;

        switch (info.Mode)
        {
            case ConnectionMode.Fastboot or ConnectionMode.FastbootD:
                wipeResult = await FastbootWipeAsync(info.Mode, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case ConnectionMode.QcEdl:
                progress?.Report(new FlashProgress("正在通过 9008 擦除 userdata 分区…"));
                wipeResult = await _clean.ErasePartitionAsync(
                    "userdata", port, memory, progress, cancellationToken).ConfigureAwait(false);
                break;

            case ConnectionMode.System or ConnectionMode.Recovery or ConnectionMode.Sideload:
                // adb 下不能可靠地直接抹数据，先进 fastbootd 再 -w。
                progress?.Report(new FlashProgress("正在进入 FastbootD 以清空数据…"));
                var toFastboot = await _flash.RebootAsync(
                    info.Mode, "fastbootd", port, memory, cancellationToken).ConfigureAwait(false);

                if (!toFastboot.Success)
                {
                    return FlashResult.Fail(
                        $"无法进入 FastbootD 清空数据：{toFastboot.Message}。" +
                        "请手动进入 Fastboot 后重试。");
                }

                var detected = await WaitForFastbootAsync(cancellationToken).ConfigureAwait(false);
                if (detected is null)
                {
                    return FlashResult.Fail("进入 FastbootD 后未能重新识别设备，请手动重试。");
                }

                wipeResult = await FastbootWipeAsync(detected.Value, progress, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                return FlashResult.Fail($"当前模式（{info.Mode.Display()}）不支持清空数据。");
        }

        if (!wipeResult.Success)
        {
            return FlashResult.Fail($"清空数据失败：{wipeResult.Message}");
        }

        progress?.Report(new FlashProgress("数据已清空，正在开机…"));
        var boot = await BootAsync(port, memory, progress, cancellationToken).ConfigureAwait(false);

        return boot.Success
            ? FlashResult.Ok("数据已清空，设备正在开机")
            : FlashResult.Fail($"数据已清空，但开机失败：{boot.Message}");
    }

    private async Task<ConnectionMode?> WaitForFastbootAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            var info = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (info.Mode is ConnectionMode.Fastboot or ConnectionMode.FastbootD)
            {
                return info.Mode;
            }
        }

        return null;
    }

    /// <summary>
    /// fastboot 清数据：优先 <c>-w</c>（等价 erase userdata + cache）。
    /// 老版本 fastboot 不认 <c>-w</c>，退回逐分区 erase。
    /// </summary>
    private async Task<FlashResult> FastbootWipeAsync(
        ConnectionMode mode,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fastboot = _tools.FastbootPath;
        if (fastboot is null)
        {
            return FlashResult.Fail("找不到 fastboot 工具");
        }

        progress?.Report(new FlashProgress("正在执行 fastboot -w…"));

        var wipe = await _runner.RunAsync(
            fastboot, ["-w"], timeout: FastbootTimeout, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (wipe.Success)
        {
            return FlashResult.Ok("fastboot -w 完成");
        }

        // 退回逐分区擦除：至少把 userdata 与 metadata 清掉。
        _logger.Warn($"fastboot -w 失败（{FirstLine(wipe.Combined)}），改为逐分区擦除");

        var failed = new List<string>();
        foreach (var partition in new[] { "userdata", "metadata", "cache" })
        {
            progress?.Report(new FlashProgress($"正在擦除 {partition}…"));

            var erase = await _runner.RunAsync(
                fastboot, ["erase", partition], timeout: FastbootTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // 分区不存在是正常的（很多机型没有 cache 分区），只记失败不中止。
            if (!erase.Success && !LooksMissing(erase.Combined))
            {
                failed.Add(partition);
            }
        }

        return failed.Count == 0
            ? FlashResult.Ok("已擦除 userdata 等分区")
            : FlashResult.Fail("以下分区擦除失败：" + string.Join("、", failed));
    }

    private static bool LooksMissing(string output) =>
        output.Contains("partition not found", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("No such partition", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("unknown partition", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string text)
    {
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return text.Trim();
    }
}
