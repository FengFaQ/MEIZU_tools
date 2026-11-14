using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MeiyouToolbox.Views;

/// <summary>占位页视图模型：标题、说明与图标标识。</summary>
public sealed partial class PlaceholderViewModel : ObservableObject
{
    public PlaceholderViewModel(string title, string description, string iconKind)
    {
        Title = title;
        Description = description;
        IconKind = iconKind;
    }

    public string Title { get; }
    public string Description { get; }

    /// <summary>图标标识（与 Lucide 名称对应）。界面按名分支渲染，见 PlaceholderView.axaml 注释。</summary>
    public string IconKind { get; }

    public bool IsFlashIcon => IconKind == "Zap";
    public bool IsBackupIcon => IconKind == "HardDriveDownload";
    public bool IsManageIcon => IconKind == "Wrench";
    public bool IsSettingsIcon => IconKind == "Settings";
}

public partial class PlaceholderView : UserControl
{
    public PlaceholderView() => InitializeComponent();
}
