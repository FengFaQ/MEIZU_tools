using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MeiyouToolbox.Views;

/// <summary>应用的主内容：顶栏 + 侧栏 + 页面宿主。</summary>
public partial class MainView : UserControl
{
    public MainView() => InitializeComponent();

    private Window? Host => TopLevel.GetTopLevel(this) as Window;

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
