using CommunityToolkit.Mvvm.ComponentModel;
using MeiyouToolbox.Services;

namespace MeiyouToolbox.ViewModels;

/// <summary>设置页。第一版只承载最有用的几项，后续细分。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IToolLocator _tools;

    public SettingsViewModel(IToolLocator tools)
    {
        _tools = tools;
    }

    public string ToolsDirectory => _tools.ToolsDirectory;

    public string AppDirectory => _tools.AppDirectory;

    [ObservableProperty]
    private bool _autoProbeOnStartup = true;

    [ObservableProperty]
    private bool _rememberLastPage = true;
}
