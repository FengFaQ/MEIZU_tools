using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeiyouToolbox.ViewModels;

namespace MeiyouToolbox.Views;

/// <summary>应用的主内容：顶栏 + 侧栏 + 页面宿主。</summary>
public partial class MainView : UserControl
{
    public MainView() => InitializeComponent();

    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // 把内置截图能力接到 ViewModel：截图需要一个真实的 Window 实例。
        if (DataContext is MainViewModel vm && Host is { } window)
        {
            vm.CaptureHandler = () =>
                App.Services.GetService(typeof(MeiyouToolbox.Services.IScreenshotService))
                    is MeiyouToolbox.Services.IScreenshotService svc
                    ? svc.CaptureWindowAsync(window)
                    : Task.FromResult<string?>(null);
        }
    }

    private void OnMinimize(object? sender, RoutedEventArgs e)
    {
        if (Host is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void OnToggleMaximize(object? sender, RoutedEventArgs e)
    {
        if (Host is { } window)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Host?.Close();
}
