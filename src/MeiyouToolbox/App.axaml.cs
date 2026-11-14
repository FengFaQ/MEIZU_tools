using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MeiyouToolbox.Services;
using MeiyouToolbox.ViewModels;
using MeiyouToolbox.Views;

namespace MeiyouToolbox;

public partial class App : Application
{
    /// <summary>全局服务容器（Microsoft.Extensions.DependencyInjection）。</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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
