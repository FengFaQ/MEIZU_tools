namespace MeiyouToolbox.Models;

/// <summary>
/// 解锁方案。参考实现里直接以字符串（<c>auth-private</c> 等）作为 goto 标签名，
/// 这里改成枚举，避免拼错标签导致流程静默失败（参考实现就有一个少连字符的 goto）。
/// </summary>
public enum UnlockPlan
{
    /// <summary>未知：机型表没给出可用方案，界面上所有刷写入口应禁用。</summary>
    Unknown,
    /// <summary>需要授权码走 9008 写 private 分区；无授权码时降级走 Fastboot 分段解锁码。</summary>
    AuthPrivate,
    /// <summary>需要授权码 + 解锁 mzkv 镜像。</summary>
    AuthMzkvSig,
    /// <summary>需要解锁 mzkv 镜像，但不需要授权码。</summary>
    AuthMzkvNoSig,
    /// <summary>改 frp 分区最后 1 字节后 fastboot flashing unlock。</summary>
    Odm1,
    /// <summary>fastboot oem unlock:&lt;SN&gt;。</summary>
    Odm2,
    /// <summary>临时刷 aboot/abootbak 后解锁，再还原。</summary>
    OdmNote8,
}

/// <summary>分区布局。</summary>
public enum PartitionLayout
{
    /// <summary>只有 A 槽（不支持 A/B）。</summary>
    AOnly,
    /// <summary>A/B 双槽，无独立 recovery。</summary>
    Ab,
    /// <summary>A/B 双槽 + 独立 recovery 分区。</summary>
    AbRec,
}

/// <summary>进入 9008 的方案。</summary>
public enum EdlPlan
{
    /// <summary>Fastboot 下 <c>oem edl</c>。</summary>
    Cmd,
    /// <summary>手动按键进入。</summary>
    Key,
    /// <summary>用 9008 线插上后自动进入。</summary>
    EdlCable,
}

/// <summary>
/// 机型档案：驱动整套刷机流程的静态数据。
/// 对应参考实现的 <c>bin\conf\dev.csv</c> 一行，但做了三项修正：
/// <list type="number">
/// <item>每行必须有 product，否则整行丢弃（参考实现里有缺方括号的行，导致机型在选择列表里消失）。</item>
/// <item>字段缺失不再让整条流程崩掉，而是标记为 <see cref="UnlockPlan.Unknown"/> 并在界面提示。</item>
/// <item>Lucky08 的代号不再与资源目录名不一致（参考实现用 MeizuVenus 却找 meizulucky08 目录）。</item>
/// </list>
/// </summary>
public sealed class DeviceProfile
{
    public required string Product { get; init; }

    public string Model { get; init; } = string.Empty;

    public string Cpu { get; init; } = string.Empty;

    /// <summary>emmc / ufs / 空（未知）。</summary>
    public string StorageType { get; init; } = string.Empty;

    public UnlockPlan UnlockPlan { get; init; } = UnlockPlan.Unknown;

    public PartitionLayout Layout { get; init; } = PartitionLayout.AOnly;

    /// <summary>boot 或 init_boot（Android 13+ 用 init_boot）。</summary>
    public string BootPartition { get; init; } = "boot";

    public EdlPlan EdlPlan { get; init; } = EdlPlan.Key;

    /// <summary>下拉框显示文本。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Model) ? Product : $"{Model}（{Product}）";

    /// <summary>解锁码只在 auth-private 方案下用得到。</summary>
    public bool NeedsUnlockCode => UnlockPlan == UnlockPlan.AuthPrivate;

    /// <summary>授权码给 Firehose 签名用，auth-mzkv-nosig 不需要。</summary>
    public bool NeedsAuthCode => UnlockPlan is UnlockPlan.AuthPrivate or UnlockPlan.AuthMzkvSig;

    /// <summary>mzkv 方案需要导入解锁用的 mzkv 镜像。</summary>
    public bool NeedsMzkv => UnlockPlan is UnlockPlan.AuthMzkvSig or UnlockPlan.AuthMzkvNoSig;

    /// <summary>ODM 系列机型本身不限制解锁与 9008，无需任何码。</summary>
    public bool IsOdm => UnlockPlan
        is UnlockPlan.Odm1 or UnlockPlan.Odm2 or UnlockPlan.OdmNote8;

    /// <summary>是否支持进入 9008（20 无界版明确不支持）。</summary>
    public bool SupportsEdl => EdlPlan != EdlPlan.Key || !Product.Equals(
        "meizu20inf", StringComparison.OrdinalIgnoreCase);

    public string UnlockPlanText => UnlockPlan switch
    {
        UnlockPlan.AuthPrivate => "授权码 + 解锁码",
        UnlockPlan.AuthMzkvSig => "授权码 + 解锁 mzkv",
        UnlockPlan.AuthMzkvNoSig => "解锁 mzkv",
        UnlockPlan.Odm1 => "ODM-1（免码）",
        UnlockPlan.Odm2 => "ODM-2（免码）",
        UnlockPlan.OdmNote8 => "ODM-Note8（免码）",
        _ => "未知（不可操作）",
    };

    public string LayoutText => Layout switch
    {
        PartitionLayout.Ab => "A/B 双槽",
        PartitionLayout.AbRec => "A/B 双槽 + Recovery",
        _ => "单槽 A",
    };

    public string EdlPlanText => EdlPlan switch
    {
        EdlPlan.Cmd => "Fastboot 命令进入",
        EdlPlan.EdlCable => "9008 线插入自动进入",
        _ => "手动按键进入",
    };
}
