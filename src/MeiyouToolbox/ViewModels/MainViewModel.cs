using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeiyouToolbox.Services;

namespace MeiyouToolbox.ViewModels;

/// <summary>侧栏可切换的页面。</summary>
public enum AppPage
{
    Overview,
    Flash,
    Backup,
    Manage,
    Settings,
}

/// <summary>主视图模型：当前页面、侧栏状态、顶栏命令。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IScreenshotService? _screenshots;
    private readonly IAppLogger? _logger;

    public MainViewModel(
        OverviewViewModel overview,
        SettingsViewModel settings,
        IScreenshotService screenshots,
        IAppLogger logger)
    {
        Overview = overview;
        Settings = settings;
        _screenshots = screenshots;
        _logger = logger;

        _logger.Info("MainViewModel 已创建，默认页面：概览");
    }

    /// <summary>设计器/回退用无参构造。</summary>
    public MainViewModel()
    {
        Overview = null!;
        Settings = null!;
    }

    public OverviewViewModel Overview { get; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsFlash), nameof(IsBackup), nameof(IsManage), nameof(IsSettings), nameof(Title))]
    private AppPage _currentPage = AppPage.Overview;

    public bool IsOverview => CurrentPage == AppPage.Overview;
    public bool IsFlash => CurrentPage == AppPage.Flash;
    public bool IsBackup => CurrentPage == AppPage.Backup;
    public bool IsManage => CurrentPage == AppPage.Manage;
    public bool IsSettings => CurrentPage == AppPage.Settings;

    public string Title => CurrentPage switch
    {
        AppPage.Overview => "概览",
        AppPage.Flash => "刷机",
        AppPage.Backup => "备份",
        AppPage.Manage => "管理",
        AppPage.Settings => "设置",
        _ => "魅友工具箱",
    };

    /// <summary>侧栏展开状态，默认展开。</summary>
    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    /// <summary>最近一次截图路径，显示在界面上便于用户找到。</summary>
    [ObservableProperty]
    private string? _lastScreenshotPath;

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
        _logger?.Info($"侧栏切换为 {(IsSidebarExpanded ? "展开" : "收起")}");
    }

    [RelayCommand]
    private void Navigate(string? page)
    {
        if (Enum.TryParse<AppPage>(page, ignoreCase: true, out var parsed))
        {
            CurrentPage = parsed;
            _logger?.Info($"切换到页面：{parsed}");
        }
    }

    /// <summary>内置截图：由窗口代码提供当前 Window 实例。</summary>
    public Func<Task<string?>>? CaptureHandler { get; set; }

    [RelayCommand]
    private async Task CaptureScreenshot()
    {
        if (CaptureHandler is null)
        {
            _logger?.Warn("截图失败：未绑定窗口实例");
            return;
        }

        var path = await CaptureHandler().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(path))
        {
            LastScreenshotPath = path;
        }
    }
}
