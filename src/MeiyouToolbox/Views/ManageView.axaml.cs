using Avalonia;
using Avalonia.Controls;
using MeiyouToolbox.ViewModels;

namespace MeiyouToolbox.Views;

/// <summary>
/// 管理页。本页不需要文件选择器，只需在首次显示时灌入机型表并刷新连接。
/// </summary>
public partial class ManageView : UserControl
{
    private bool _initialized;

    public ManageView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
    }

    private ManageViewModel? Vm => DataContext as ManageViewModel;

    private async void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        try
        {
            if (!_initialized)
            {
                _initialized = true;
                await vm.InitializeAsync().ConfigureAwait(true);
            }
            else
            {
                await vm.RefreshConnectionAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Write("ManageView 初始化", ex);
        }
    }
}
