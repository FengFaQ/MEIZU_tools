using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeiyouToolbox.Models;
using MeiyouToolbox.Services;

namespace MeiyouToolbox.ViewModels;

/// <summary>设备信息的一行（标签 + 值，值为空时显示 "---"）。</summary>
public sealed class DeviceFieldRow
{
    public DeviceFieldRow(DeviceField field, string? value)
    {
        Label = field.Label();
        Value = string.IsNullOrWhiteSpace(value) ? "---" : value;
        HasValue = !string.IsNullOrWhiteSpace(value);
    }

    public string Label { get; }
    public string Value { get; }
    public bool HasValue { get; }
}

/// <summary>
/// 概览页：手机状态（第一、四象限）、电脑与任务状态（第二象限）、快捷打开（第三象限）。
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly IDeviceService _devices;
    private readonly IHostInfoService _host;
    private readonly IShellLauncher _shell;
    private readonly IAppLogger _logger;

    public OverviewViewModel(
        IDeviceService devices, IHostInfoService host, IShellLauncher shell, IAppLogger logger)
    {
        _devices = devices;
        _host = host;
        _shell = shell;
        _logger = logger;

        // 先把 14 行占位（全部 "---"）建好，界面首帧就有完整结构。
        RebuildFieldRows(DeviceInfo.Disconnected);
        RefreshHost();

        // 把启动以来已有的日志补进列表，之后靠事件实时追加。
        foreach (var line in _logger.Snapshot())
        {
            LogLines.Add(line);
        }

        TrimLogLines();
        _logger.LineWritten += OnLogLineWritten;
    }

    // ---------------- 运行日志 ----------------

    /// <summary>右侧日志栏：软件运行日志 + 刷写 / 备份等操作记录。</summary>
    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>日志保留行数。概览页只是"看个大概"，完整日志在 logs\ 目录里。</summary>
    private const int MaxLogLines = 400;

    [ObservableProperty]
    private bool _isLogAutoScroll = true;

    public bool HasLog => LogLines.Count > 0;

    /// <summary>
    /// <see cref="IAppLogger.LineWritten"/> 回调。<b>可能在任意线程</b>，必须切回 UI 线程再改集合。
    /// </summary>
    private void OnLogLineWritten(string line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnLogLineWritten(line));
            return;
        }

        LogLines.Add(line);
        TrimLogLines();
    }

    private void TrimLogLines()
    {
        while (LogLines.Count > MaxLogLines)
        {
            LogLines.RemoveAt(0);
        }

        OnPropertyChanged(nameof(HasLog));
        LogScrollRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>视图订阅它把日志列表滚到底部（VM 不引用控件）。</summary>
    public event EventHandler? LogScrollRequested;

    [RelayCommand]
    private void ClearLog()
    {
        LogLines.Clear();
        OnPropertyChanged(nameof(HasLog));
    }

    // ---------------- 手机状态 ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsConnected), nameof(HasConnectionPath), nameof(ConnectionPathText))]
    private DeviceInfo _device = DeviceInfo.Disconnected;

    public bool IsConnected => Device.IsConnected;

    public bool HasConnectionPath => Device.IsConnected;

    public string StatusText => Device.IsConnected ? "已连接" : "未连接";

    public string ConnectionPathText => Device.Mode.Display();

    /// <summary>设备具体信息（14 项，按用户指定顺序）。</summary>
    public ObservableCollection<DeviceFieldRow> DeviceFields { get; } = [];

    [ObservableProperty]
    private bool _isProbing;

    /// <summary>
    /// 在 UI 线程上刷新设备信息。
    /// <para>
    /// 注意：<see cref="DeviceFields"/> 绑定到界面，<b>只能在 UI 线程修改</b>。
    /// 早期实现在构造里用 <c>Task.Run</c> 后台线程直接改集合，触发跨线程访问，
    /// 表现为启动后弹出原生崩溃 0xe0434352、界面停在半截。
    /// </para>
    /// </summary>
    public async Task RefreshDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (IsProbing)
        {
            return;
        }

        IsProbing = true;
        try
        {
            // ConfigureAwait(true)：回到调用方（UI）线程上下文再更新绑定数据。
            var info = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(true);
            ApplyDevice(info);
        }
        catch
        {
            // 探测失败保持"未连接"，不影响界面。
        }
        finally
        {
            IsProbing = false;
        }
    }

    private void ApplyDevice(DeviceInfo info)
    {
        Device = info;
        RebuildFieldRows(info);
    }

    private void RebuildFieldRows(DeviceInfo info)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RebuildFieldRows(info));
            return;
        }

        DeviceFields.Clear();
        foreach (var field in Enum.GetValues<DeviceField>())
        {
            DeviceFields.Add(new DeviceFieldRow(field, info.Value(field)));
        }
    }

    [RelayCommand]
    private async Task RefreshDevice(CancellationToken cancellationToken) =>
        await RefreshDeviceAsync(cancellationToken).ConfigureAwait(true);

    // ---------------- 电脑状态 ----------------

    [ObservableProperty]
    private string _batteryText = "---";

    [ObservableProperty]
    private string _appVersion = AppVersionInfo.Current;

    public void RefreshHost()
    {
        var status = _host.GetStatus();
        BatteryText = status.BatteryText;
        AppVersion = status.AppVersion;
    }

    // ---------------- 任务状态 ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskTitle), nameof(TaskPercentText), nameof(IsTaskRunning), nameof(HasTask))]
    private BackgroundTaskProgress _currentTask = BackgroundTaskProgress.Idle;

    public bool IsTaskRunning => CurrentTask.IsRunning;

    public bool HasTask => CurrentTask.State != TaskState.Idle;

    public string TaskTitle => CurrentTask.IsRunning
        ? (string.IsNullOrWhiteSpace(CurrentTask.Title) ? "空闲" : CurrentTask.Title)
        : (CurrentTask.State == TaskState.Idle ? "空闲" : (CurrentTask.Title ?? "空闲"));

    public string TaskPercentText => CurrentTask.IsIndeterminate
        ? "---"
        : $"{Math.Clamp((int)Math.Round(CurrentTask.Percent), 0, 100)}%";

    public void ReportTask(BackgroundTaskProgress progress)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ReportTask(progress));
            return;
        }

        CurrentTask = progress;
    }

    // ---------------- 快捷打开 ----------------

    [RelayCommand]
    private void OpenComputerManagement() => _shell.OpenComputerManagement();

    [RelayCommand]
    private void OpenTerminal() => _shell.OpenTerminalAtTools();

    [RelayCommand]
    private void OpenTaskManager() => _shell.OpenTaskManager();

    [RelayCommand]
    private void OpenAppDirectory() => _shell.OpenAppDirectory();
}
