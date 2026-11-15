using Avalonia.Controls;
using Avalonia.Threading;
using MeiyouToolbox.ViewModels;

namespace MeiyouToolbox.Views;

/// <summary>概览页：手机状态 / 电脑状态 / 快捷打开 / 运行日志。</summary>
public partial class OverviewView : UserControl
{
    private OverviewViewModel? _vm;

    public OverviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel();
    }

    private void BindViewModel()
    {
        if (_vm is not null)
        {
            _vm.LogScrollRequested -= OnLogScrollRequested;
        }

        _vm = DataContext as OverviewViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.LogScrollRequested += OnLogScrollRequested;
        ScrollLogToEnd();
    }

    /// <summary>
    /// 新日志追加后把滚动条推到底部。<c>LayoutUpdated</c> 之后才拿得到新的
    /// <c>Extent</c>，所以用 <see cref="DispatcherPriority.Background"/> 延后一帧。
    /// </summary>
    private void OnLogScrollRequested(object? sender, EventArgs e) => ScrollLogToEnd();

    private void ScrollLogToEnd()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_vm is { IsLogAutoScroll: false })
                {
                    return;
                }

                LogScroll.ScrollToEnd();
            },
            DispatcherPriority.Background);
    }
}
