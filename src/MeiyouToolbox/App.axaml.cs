using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using MeiyouToolbox.Services;
using MeiyouToolbox.ViewModels;
using MeiyouToolbox.Views;

namespace MeiyouToolbox;

public partial class App : Application
{
    /// <summary>全局服务容器（Microsoft.Extensions.DependencyInjection）。</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    public override void Initialize()
    {
        // 越早挂钩越好：XAML 加载期与窗口构造期的异常都要能落盘，
        // 否则程序会"静默退出、什么都不显示"（本轮就踩过）。
        AppDiagnostics.Install();

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = AppServices.Build();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>启动期异常的兜底记录：写到 <c>%LOCALAPPDATA%\MeiyouToolbox\logs\crash-*.log</c>。</summary>
internal static class AppDiagnostics
{
    private static bool _installed;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeiyouToolbox", "logs");

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(
                LogDirectory,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            var sb = new StringBuilder();
            sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"来源: {source}");
            sb.AppendLine($"版本: {AppVersionInfo.Current}");
            sb.AppendLine($"运行时: {Environment.Version}");
            sb.AppendLine(new string('-', 60));
            sb.AppendLine(exception?.ToString() ?? "(无异常对象)");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 记录日志本身失败时不再抛出，避免二次崩溃。
        }
    }
}
