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

        // 首次截图：便于远程确认渲染效果，失败不影响正常使用。
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);

            // CaptureHandler 由 MainView 在可视树挂载后赋值，这里可能还没就绪，重试几次。
            for (var i = 0; i < 10 && vm.CaptureHandler is null; i++)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }

            if (vm.CaptureHandler is { } capture)
            {
                await capture().ConfigureAwait(false);
            }
        });
    }
}
