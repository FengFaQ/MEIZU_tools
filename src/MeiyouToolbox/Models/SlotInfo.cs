namespace MeiyouToolbox.Models;

/// <summary>
/// A/B 槽位状态。参考实现只往外抛 <c>slot__cur</c> / <c>slot__cur_oth</c> 两个字符串，
/// 这里把"当前槽位 + 是否 A/B 设备 + 各槽位是否可启动"一起带回来，
/// 因为 fastboot 切槽位前必须知道目标槽位是不是 unbootable。
/// </summary>
public sealed class SlotInfo
{
    /// <summary>设备是否使用 A/B 分区。</summary>
    public bool IsAbDevice { get; init; }

    /// <summary>当前活动槽位：<c>a</c> / <c>b</c> / <c>aonly</c> / <c>unknown</c>。</summary>
    public string Current { get; init; } = "unknown";

    /// <summary>另一个槽位。非 A/B 设备为空。</summary>
    public string? Other => Current switch
    {
        "a" => "b",
        "b" => "a",
        _ => null,
    };

    /// <summary>槽位 a 是否不可启动（fastboot <c>getvar all</c> 的 <c>slot_a_unbootable</c>）。</summary>
    public bool SlotAUnbootable { get; init; }

    /// <summary>槽位 b 是否不可启动。</summary>
    public bool SlotBUnbootable { get; init; }

    /// <summary>读槽位时用的连接方式。</summary>
    public ConnectionMode Source { get; init; } = ConnectionMode.None;

    public bool HasSlot => Current is "a" or "b";

    public string Display => Current switch
    {
        "a" => "A",
        "b" => "B",
        "aonly" => "单分区（无 A/B）",
        "unknown" => "读取失败",
        _ => Current,
    };

    /// <summary>目标槽位是否已知不可启动 —— 界面据此给红色警告。</summary>
    public bool IsUnbootable(string slot) => slot switch
    {
        "a" => SlotAUnbootable,
        "b" => SlotBUnbootable,
        _ => false,
    };

    public static SlotInfo Unknown(ConnectionMode source) =>
        new() { Current = "unknown", Source = source };
}
