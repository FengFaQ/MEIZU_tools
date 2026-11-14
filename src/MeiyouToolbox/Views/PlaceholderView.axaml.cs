using CommunityToolkit.Mvvm.ComponentModel;

namespace MeiyouToolbox.Views;

/// <summary>
/// 占位页视图模型。<para>
/// 注意：Avalonia 在 XAML 里用 <c>&lt;views:PlaceholderViewModel Title=".." /&gt;</c> 实例化时
/// **要求存在公共无参构造函数**（只有有参构造会报
/// AVLN3000: Unable to find public constructor）。因此这里是无参构造 + 可写属性。
/// </para>
/// </summary>
public sealed partial class PlaceholderViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlashIcon), nameof(IsBackupIcon), nameof(IsManageIcon), nameof(IsSettingsIcon))]
    private string _iconKind = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    public bool IsFlashIcon => IconKind == "Zap";
    public bool IsBackupIcon => IconKind == "HardDriveDownload";
    public bool IsManageIcon => IconKind == "Wrench";
    public bool IsSettingsIcon => IconKind == "Settings";
}
