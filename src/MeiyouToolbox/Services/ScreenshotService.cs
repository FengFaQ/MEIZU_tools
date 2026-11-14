using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MeiyouToolbox.Services;

/// <summary>
/// 内置截图：把主窗口自身渲染成 PNG，存到程序目录下的 <c>screenshots\</c>。
/// 这样不依赖外部截图工具（也不会截到被遮挡的其它窗口）。
/// </summary>
public interface IScreenshotService
{
    string Directory { get; }

    /// <summary>截取指定窗口，返回保存的文件路径；失败返回 null。</summary>
    Task<string?> CaptureWindowAsync(Window window, CancellationToken cancellationToken = default);
}

public sealed class ScreenshotService : IScreenshotService
{
    private readonly IAppLogger _logger;

    public ScreenshotService(IAppLogger logger, IToolLocator tools)
    {
        _logger = logger;
        Directory = Path.Combine(tools.AppDirectory, "screenshots");
    }

    public string Directory { get; }

    public Task<string?> CaptureWindowAsync(Window window, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 必须在 UI 线程做渲染与落盘：窗口的可视树只能在 UI 线程访问。
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                Directory.CreateDirectory(Directory);

                var size = window.Bounds.Size;
                if (size.Width < 1 || size.Height < 1)
                {
                    _logger.Warn($"截图失败：窗口尺寸无效 {size}");
                    tcs.TrySetResult(null);
                    return;
                }

                // 给 Arc 的动画/布局一个稳定的帧
                var path = Path.Combine(
                    Directory,
                    $"window-{DateTime.Now:yyyyMMdd-HHmmss}.png");

                using (var bitmap = new RenderTargetBitmap(
                           new PixelSize((int)size.Width, (int)size.Height),
                           new Vector(96, 96)))
                {
                    bitmap.Render(window);
                    bitmap.Save(path);
                }

                _logger.Info($"已截图: {path}");
                tcs.TrySetResult(path);
            }
            catch (Exception ex)
            {
                _logger.Error("截图失败", ex);
                tcs.TrySetResult(null);
            }
        });

        return tcs.Task;
    }
}
