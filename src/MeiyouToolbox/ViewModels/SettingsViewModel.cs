using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeiyouToolbox.Services;

namespace MeiyouToolbox.ViewModels;

/// <summary>设置页：展示运行目录结构，并提供打开日志/截图目录的入口。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IToolLocator _tools;
    private readonly IScreenshotService _screenshots;
    private readonly IShellLauncher _shell;
    private readonly IAppLogger _logger;

    /// <summary>XAML 预览器需要的无参构造。</summary>
    public SettingsViewModel()
    {
        _tools = null!;
        _screenshots = null!;
        _shell = null!;
        _logger = null!;
    }

    public SettingsViewModel(
        IToolLocator tools,
        IScreenshotService screenshots,
        IShellLauncher shell,
        IAppLogger logger)
    {
        _tools = tools;
        _screenshots = screenshots;
        _shell = shell;
        _logger = logger;
    }

    public string ToolsDirectory => _tools?.ToolsDirectory ?? "(未初始化)";

    public string AppDirectory => _tools?.AppDirectory ?? "(未初始化)";

    public string LogDirectory => _logger?.LogDirectory ?? "(未初始化)";

    public string ScreenshotDirectory => _screenshots?.Directory ?? "(未初始化)";

    [ObservableProperty]
    private bool _autoProbeOnStartup = true;

    [RelayCommand]
    private void OpenLogDirectory() => _shell?.OpenFolder(LogDirectory);

    [RelayCommand]
    private void OpenScreenshotDirectory() => _shell?.OpenFolder(ScreenshotDirectory);

    [RelayCommand]
    private void OpenAppDirectory() => _shell?.OpenAppDirectory();
}
