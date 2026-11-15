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
        FlashViewModel flash,
        BackupViewModel backup,
        ManageViewModel manage,
        SettingsViewModel settings,
        TaskCompleteViewModel taskComplete,
        EdlFlashViewModel edl,
        IScreenshotService screenshots,
        IAppLogger logger)
    {
        Overview = overview;
        Flash = flash;
        Backup = backup;
        Manage = manage;
        Settings = settings;
        TaskComplete = taskComplete;
        Edl = edl;
        _screenshots = screenshots;
        _logger = logger;

        // 诊断入口：设置环境变量 MEIYOU_START_PAGE=flash 可直接以某页启动，
        // 便于自动化截图验证各页布局，正常使用时不设此变量。
        var startPage = Environment.GetEnvironmentVariable("MEIYOU_START_PAGE");
        if (!string.IsNullOrWhiteSpace(startPage) &&
            Enum.TryParse<AppPage>(startPage, ignoreCase: true, out var initial))
        {
            _currentPage = initial;
            _logger.Info($"按环境变量以 {initial} 页启动");
        }

        _logger.Info("MainViewModel 已创建，默认页面：概览");
    }

    /// <summary>设计器/回退用无参构造。</summary>
    public MainViewModel()
    {
        Overview = null!;
        Flash = null!;
        Backup = null!;
        Manage = null!;
        Settings = null!;
        TaskComplete = null!;
        Edl = null!;
    }

    public OverviewViewModel Overview { get; }

    public FlashViewModel Flash { get; }

    public BackupViewModel Backup { get; }

    public ManageViewModel Manage { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>「完成任务提示」—— 全窗口共用的收尾提示层。</summary>
    public TaskCompleteViewModel TaskComplete { get; }

    /// <summary>9008 刷写页 —— 从刷机页的「选择刷写通路」打开。</summary>
    public EdlFlashViewModel Edl { get; }

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
