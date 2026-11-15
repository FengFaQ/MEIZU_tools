using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>
/// 解锁计划里的一步：切到某个连接状态，做一件事。
/// </summary>
/// <param name="Mode">这一步需要的设备状态。</param>
/// <param name="Label">给用户看的一句话（会显示在进度里）。</param>
public sealed record UnlockStep(ConnectionMode Mode, string Label);

/// <summary>
/// 解锁计划：按机型与已导入的资料，决定要依次经过哪些状态。
/// </summary>
public sealed record UnlockRoute(
    IReadOnlyList<UnlockStep> Steps,
    string Summary,
    string? BlockedReason = null)
{
    public bool CanRun => BlockedReason is null && Steps.Count > 0;

    /// <summary>第一步需要的状态，界面用它显示"会先切到哪里"。</summary>
    public ConnectionMode EntryMode => Steps.Count > 0 ? Steps[0].Mode : ConnectionMode.None;

    public static UnlockRoute Blocked(string reason) => new([], reason, reason);
}

/// <summary>
/// 按参考实现 <c>煤油工具箱.bat:L753-L856</c> 的 <c>:UNLOCKBL-AUTH-PRIVATE</c> 分支，
/// 把"该机型解锁需要经过哪些设备状态"算出来。
/// <para>
/// 关键事实（原实现的分支顺序）：
/// 1. <c>meizu20inf</c> 不支持 9008，<c>L757</c> 强制 <c>forcefb=y</c> → 只走 Fastboot；
/// 2. 有有效授权码 → <c>L774</c> 直接跳 <c>-EDL</c>，纯 9008 路线，跳过 Fastboot 阶段；
/// 3. 没有授权码 → <c>L769-L773</c> 只警告不中止，走 Fastboot 写解锁码（<c>L785-L800</c>
///    把 512 字符切成 16 段 × 32 字符逐条 <c>oem unlock:</c>）；
/// 4. 走完 Fastboot 后，非 forcefb 机型**继续进 9008**（<c>L811-L817</c>）写 private 分区
///    （<c>L819-L856</c>：0x11000 写 LOCK、0x11004 写解锁码、0x11104 写 factory、
///    0x11114 写版本标记）。
/// </para>
/// 所以 auth-private 是**两阶段**的：Fastboot → 9008。只有 meizu20inf 是单阶段 Fastboot。
/// </summary>
public interface IUnlockPlanner
{
    UnlockRoute Plan(DeviceProfile profile, DeviceCredentials credentials);
}

public sealed class UnlockPlanner : IUnlockPlanner
{
    /// <summary>强制只走 Fastboot 的机型（原实现 L756-L757）。</summary>
    private static bool IsForceFastboot(DeviceProfile profile) =>
        string.Equals(profile.Product, "meizu20inf", StringComparison.OrdinalIgnoreCase);

    public UnlockRoute Plan(DeviceProfile profile, DeviceCredentials credentials)
    {
        return profile.UnlockPlan switch
        {
            Models.UnlockPlan.AuthPrivate => PlanAuthPrivate(profile, credentials),
            Models.UnlockPlan.AuthMzkvSig or Models.UnlockPlan.AuthMzkvNoSig => PlanMzkv(profile, credentials),
            Models.UnlockPlan.Odm1 => new UnlockRoute(
                [
                    new UnlockStep(ConnectionMode.QcEdl, "进 9008，改 frp 分区最后 1 字节为 01"),
                    new UnlockStep(ConnectionMode.Fastboot, "切到 Fastboot，执行 flashing unlock"),
                ],
                "9008（改 frp）→ Fastboot（flashing unlock）"),
            Models.UnlockPlan.Odm2 => new UnlockRoute(
                [new UnlockStep(ConnectionMode.Fastboot, "切到 Fastboot，用 oem unlock:<SN> 解锁")],
                "Fastboot（oem unlock:<SN>）"),
            Models.UnlockPlan.OdmNote8 => new UnlockRoute(
                [new UnlockStep(ConnectionMode.Fastboot, "切到 Fastboot，刷 aboot_unlock 后 oem unlock，再还原")],
                "Fastboot（刷 aboot 解锁版 → 解锁 → 还原）"),
            _ => UnlockRoute.Blocked(
                $"机型 {profile.Product} 的解锁方案未配置（{profile.UnlockPlanText}），请先在机型表里补全"),
        };
    }

    private static UnlockRoute PlanAuthPrivate(DeviceProfile profile, DeviceCredentials credentials)
    {
        // ---- 1. meizu20inf：没有 9008，只有 Fastboot 一阶段 ----
        if (IsForceFastboot(profile))
        {
            if (!credentials.HasUnlockCode)
            {
                return UnlockRoute.Blocked(
                    "该机型不支持 9008，只能走 Fastboot，需要 512 字符解锁码。请先导入解锁码。");
            }

            return new UnlockRoute(
                [new UnlockStep(ConnectionMode.Fastboot, "切到 Fastboot，分 16 段写入解锁码")],
                "Fastboot（写解锁码，该机型无 9008）");
        }

        // ---- 2. 有授权码：直接走 9008，跳过 Fastboot 写码阶段（原实现 L774）----
        if (credentials.HasAuthCode)
        {
            return new UnlockRoute(
                [
                    new UnlockStep(
                        ConnectionMode.QcEdl,
                        "进 9008，备份 private 后写入解锁标记与解锁码"),
                ],
                "9008（写 private 分区）");
        }

        // ---- 3. 没有授权码：先 Fastboot 写码，再进 9008 ----
        if (!credentials.HasUnlockCode)
        {
            return UnlockRoute.Blocked(
                "还没有导入解锁码。auth-private 机型解锁必须先有 512 字符解锁码"
                + "（有授权码时可直接走 9008，但仍建议两个都导入）。");
        }

        return new UnlockRoute(
            [
                new UnlockStep(ConnectionMode.Fastboot, "切到 Fastboot，分 16 段写入解锁码"),
                new UnlockStep(ConnectionMode.QcEdl, "进 9008，备份 private 后写入解锁标记与解锁码"),
            ],
            "Fastboot（写解锁码）→ 9008（写 private 分区）");
    }

    private static UnlockRoute PlanMzkv(DeviceProfile profile, DeviceCredentials credentials)
    {
        var needsSig = profile.UnlockPlan == Models.UnlockPlan.AuthMzkvSig;

        // mzkv 路线必须有解锁版 mzkv 镜像，没有就写不进去。
        if (!credentials.HasMzkv)
        {
            return UnlockRoute.Blocked(
                "还没有导入解锁版 mzkv 镜像。mzkv 解锁方案必须先把该机型的 mzkv 解锁文件放进来。");
        }

        // 带签名的方案还需要授权码（Firehose 认证）。
        if (needsSig && !credentials.HasAuthCode)
        {
            return UnlockRoute.Blocked(
                "该机型的 9008 需要授权码认证。请先导入 512 字符授权码。");
        }

        var label = needsSig
            ? "进 9008，回读 mzkv 备份，写入解锁版 mzkv"
            : "进 9008，回读 mzkv 备份，写入解锁版 mzkv（该机型无需授权码）";

        return new UnlockRoute(
            [new UnlockStep(ConnectionMode.QcEdl, label)],
            needsSig ? "9008（mzkv + 授权码）" : "9008（mzkv）");
    }
}
