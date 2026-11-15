using Avalonia;
using Avalonia.Controls;
using MeiyouToolbox.Services;
using MeiyouToolbox.ViewModels;

namespace MeiyouToolbox.Views;

/// <summary>应用的主内容：侧栏 + 页面宿主（窗口按钮交给系统标题栏）。</summary>
public partial class MainView : UserControl
{
    private bool _captureBound;

    public MainView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
    }

    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        TryBindCapture();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        TryBindCapture();
    }

    /// <summary>
    /// 把内置截图能力接到 ViewModel。
    /// 必须等到控件挂上可视树、拿到真实 Window 之后才能绑定，
    /// 否则 CaptureHandler 为空、点"截图"按钮没反应。
    /// </summary>
    private void TryBindCapture()
    {
        if (_captureBound)
        {
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var window = Host;
        if (window is null)
        {
            return;
        }

        vm.CaptureHandler = () =>
        {
            var svc = App.Services.GetService(typeof(IScreenshotService)) as IScreenshotService;
            return svc is null
                ? Task.FromResult<string?>(null)
                : svc.CaptureWindowAsync(window);
        };

        _captureBound = true;
    }
}
