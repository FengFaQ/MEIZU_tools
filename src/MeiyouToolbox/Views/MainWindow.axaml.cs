using Avalonia;
using Avalonia.Controls;
using MeiyouToolbox.ViewModels;

namespace MeiyouToolbox.Views;

/// <summary>桌面窗口。</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // 首次截图（便于远程确认渲染效果），失败不影响使用。
        if (DataContext is MainViewModel vm && vm.CaptureHandler is { } capture)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1200).ConfigureAwait(false);
                await capture().ConfigureAwait(false);
            });
        }
    }
}
