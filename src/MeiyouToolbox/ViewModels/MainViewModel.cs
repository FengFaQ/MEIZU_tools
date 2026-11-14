using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    public MainViewModel(OverviewViewModel overview, SettingsViewModel settings)
    {
        Overview = overview;
        Settings = settings;
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

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    [RelayCommand]
    private void Navigate(string? page)
    {
        if (Enum.TryParse<AppPage>(page, ignoreCase: true, out var parsed))
        {
            CurrentPage = parsed;
        }
    }
}
