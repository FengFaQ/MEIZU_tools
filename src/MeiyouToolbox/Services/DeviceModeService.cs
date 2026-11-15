using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>
/// 把设备送到某个指定连接模式。参考实现里这一步是靠人肉完成的：
/// 界面上写一句"请把手机进入 9008"，然后等用户自己按键。
/// 这里改成自动导航：探测当前模式 → 计算一条重启路径 → 执行 → 轮询等待目标模式出现。
/// <para>
/// 为什么要有单独的路径计算：9008 并不能从任意模式一步到达。
/// 例如 fastboot 到 9008 需要 <c>oem edl</c>，而系统模式下要 <c>reboot edl</c>；
/// 反过来 9008 不能直接回 fastboot，必须先复位到系统再进 bootloader。
/// </para>
/// </summary>
public interface IDeviceModeService
{
    /// <summary>
    /// 确保设备处于 <paramref name="target"/> 模式。已在目标模式时不重启。
    /// </summary>
    /// <param name="target">目标模式。</param>
    /// <param name="port">9008 端口，可不传。</param>
    /// <param name="memory">存储类型，9008 复位要用。</param>
    /// <param name="progress">进度回调（会报告每一步）。</param>
    /// <param name="cancellationToken">取消。</param>
    Task<ModeNavigateResult> EnsureModeAsync(
        ConnectionMode target,
        string? port = null,
        StorageKind memory = StorageKind.Auto,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>设备当前实际模式（实时探测，不是缓存）。</summary>
    Task<ConnectionMode> DetectAsync(CancellationToken cancellationToken = default);
}

/// <summary>模式导航结果。<see cref="Info"/> 是导航结束后的设备快照，供调用方继续用。</summary>
public sealed record ModeNavigateResult(bool Success, string Message, DeviceInfo Info)
{
    public ConnectionMode Mode => Info.Mode;

    public static ModeNavigateResult Ok(string message, DeviceInfo info) => new(true, message, info);

    public static ModeNavigateResult Fail(string message, DeviceInfo info) => new(false, message, info);
}

public sealed class DeviceModeService : IDeviceModeService
{
    private readonly IFlashService _flash;
    private readonly IDeviceService _devices;
    private readonly IAppLogger _logger;

    public DeviceModeService(IFlashService flash, IDeviceService devices, IAppLogger logger)
    {
        _flash = flash;
        _devices = devices;
        _logger = logger;
    }

    /// <summary>单次等待目标模式出现的最长时间。9008 从系统重启通常 8~15 秒。</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(90);

    /// <summary>轮询间隔。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public async Task<ConnectionMode> DetectAsync(CancellationToken cancellationToken = default)
    {
        var info = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
        return info.Mode;
    }

    public async Task<ModeNavigateResult> EnsureModeAsync(
        ConnectionMode target,
        string? port = null,
        StorageKind memory = StorageKind.Auto,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var info = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);

        if (info.Mode == target)
        {
            progress?.Report(new FlashProgress($"设备已在 {target.Display()}，无需切换"));
            return ModeNavigateResult.Ok($"设备已在 {target.Display()}", info);
        }

        if (info.Mode == ConnectionMode.None)
        {
            // 没探测到设备时不要急着报错：用户可能刚插上、还在枚举。
            progress?.Report(new FlashProgress("未检测到设备，正在等待接入…"));
            info = await WaitForAsync(_ => true, progress, cancellationToken).ConfigureAwait(false);
            if (info.Mode == ConnectionMode.None)
            {
                return ModeNavigateResult.Fail("未检测到设备。请确认数据线已连接、驱动正常。", info);
            }

            if (info.Mode == target || !TryPlan(info.Mode, target, out var planned))
            {
                return info.Mode == target
                    ? ModeNavigateResult.Ok($"设备已在 {target.Display()}", info)
                    : ModeNavigateResult.Fail(
                        $"无法从 {info.Mode.Display()} 直接切换到 {target.Display()}。", info);
            }

            info = await NavigateAsync(info, planned, target, port, memory, progress, cancellationToken)
                .ConfigureAwait(false);
            return info.Mode == target
                ? ModeNavigateResult.Ok($"已切换到 {target.Display()}", info)
                : ModeNavigateResult.Fail(BuildTimeoutMessage(target, info), info);
        }

        if (!TryPlan(info.Mode, target, out var steps))
        {
            return ModeNavigateResult.Fail(
                $"无法从 {info.Mode.Display()} 切换到 {target.Display()}。" +
                "请手动把设备调到目标状态后重试。", info);
        }

        info = await NavigateAsync(info, steps, target, port, memory, progress, cancellationToken)
            .ConfigureAwait(false);

        return info.Mode == target
            ? ModeNavigateResult.Ok($"已切换到 {target.Display()}", info)
            : ModeNavigateResult.Fail(BuildTimeoutMessage(target, info), info);
    }

    private static string BuildTimeoutMessage(ConnectionMode target, DeviceInfo info) =>
        $"等待 {target.Display()} 超时，设备当前处于 {info.Mode.Display()}。" +
        (target == ConnectionMode.QcEdl && info.Mode is ConnectionMode.Fastboot or ConnectionMode.FastbootD
            ? "该机型可能不支持 fastboot 直接进 9008，请用按键组合手动进入。"
            : "请检查数据线与驱动后重试。");

    /// <summary>
    /// 计算从 <paramref name="from"/> 到 <paramref name="to"/> 的重启步骤。
    /// 返回的每个元素都是 <see cref="IFlashService.RebootAsync"/> 的 <c>toMode</c> 参数。
    /// </summary>
    private static bool TryPlan(ConnectionMode from, ConnectionMode to, out string[] steps)
    {
        // 一步直达的组合。
        var direct = (from, to) switch
        {
            (ConnectionMode.System or ConnectionMode.Recovery or ConnectionMode.Sideload, ConnectionMode.System) => "system",
            (ConnectionMode.System or ConnectionMode.Sideload, ConnectionMode.Recovery) => "recovery",
            (ConnectionMode.System or ConnectionMode.Recovery or ConnectionMode.Sideload, ConnectionMode.Fastboot) => "fastboot",
            (ConnectionMode.System or ConnectionMode.Recovery or ConnectionMode.Sideload, ConnectionMode.FastbootD) => "fastbootd",
            (ConnectionMode.System, ConnectionMode.QcEdl) => "qcedl",
            (ConnectionMode.Fastboot or ConnectionMode.FastbootD, ConnectionMode.System) => "system",
            (ConnectionMode.Fastboot, ConnectionMode.Recovery) => "recovery",
            (ConnectionMode.Fastboot, ConnectionMode.FastbootD) => "fastbootd",
            (ConnectionMode.FastbootD, ConnectionMode.Fastboot) => "fastboot",
            (ConnectionMode.Fastboot or ConnectionMode.FastbootD, ConnectionMode.QcEdl) => "qcedl",
            (ConnectionMode.QcEdl, ConnectionMode.System) => "system",
            _ => null,
        };

        if (direct is not null)
        {
            steps = [direct];
            return true;
        }

        // 两跳：9008 回不到 fastboot，只能先复位到系统再进 bootloader。
        if (from == ConnectionMode.QcEdl)
        {
            steps = to switch
            {
                ConnectionMode.Fastboot => ["system", "fastboot"],
                ConnectionMode.Recovery => ["system", "recovery"],
                ConnectionMode.FastbootD => ["system", "fastbootd"],
                _ => [],
            };

            return steps.Length > 0;
        }

        // recovery 下进 9008 不可靠，先回系统。
        if (from == ConnectionMode.Recovery && to == ConnectionMode.QcEdl)
        {
            steps = ["system", "qcedl"];
            return true;
        }

        steps = [];
        return false;
    }

    private async Task<DeviceInfo> NavigateAsync(
        DeviceInfo start,
        string[] steps,
        ConnectionMode target,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var info = start;

        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            progress?.Report(new FlashProgress(
                $"正在把设备切换到 {step}（第 {i + 1}/{steps.Length} 步）…"));

            var result = await _flash.RebootAsync(
                info.Mode, step, port, memory, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.Warn($"重启到 {step} 失败：{result.Message}");
                progress?.Report(new FlashProgress($"重启失败：{result.Message}"));
                return info;
            }

            // 中间步骤也要等设备真的离开当前模式，否则下一步会读到旧状态。
            var expected = ParseMode(step);
            if (expected is null)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            info = await WaitForAsync(
                m => m == expected.Value || m == target, progress, cancellationToken)
                .ConfigureAwait(false);

            if (info.Mode == target)
            {
                return info;
            }
        }

        return info;
    }

    private static ConnectionMode? ParseMode(string step) => step switch
    {
        "system" => ConnectionMode.System,
        "recovery" => ConnectionMode.Recovery,
        "fastboot" => ConnectionMode.Fastboot,
        "fastbootd" => ConnectionMode.FastbootD,
        "qcedl" => ConnectionMode.QcEdl,
        _ => null,
    };

    private async Task<DeviceInfo> WaitForAsync(
        Func<ConnectionMode, bool> predicate,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        var lastReport = DateTime.MinValue;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (info.Mode != ConnectionMode.None && predicate(info.Mode))
            {
                return info;
            }

            // 每 10 秒报一次，避免进度文字刷太快。
            if ((DateTime.UtcNow - lastReport).TotalSeconds >= 10)
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new FlashProgress(
                    info.Mode == ConnectionMode.None
                        ? "等待设备重新枚举…"
                        : $"设备当前为 {info.Mode.Display()}，继续等待…"));
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
    }
}
