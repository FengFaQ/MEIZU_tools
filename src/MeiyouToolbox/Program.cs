using Avalonia;
using MeiyouToolbox;

namespace MeiyouToolbox;

internal static class Program
{
    // Avalonia 在 Main 之前不可用：BuildAvaloniaApp() 之前不要调用任何 Avalonia / Arc API。
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // 也被 IDE 的 XAML 预览器使用。UseArcApp()（AppSetup.cs）负责字体、Arc 与主题。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .UseArcApp()
                  .LogToTrace();
}
