using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MeiyouToolbox.Views;

/// <summary>占位页视图模型：标题、说明与图标名。</summary>
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
    public string IconKind { get; }
}

public partial class PlaceholderView : UserControl
{
    public PlaceholderView() => InitializeComponent();
}
