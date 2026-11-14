using System.Collections.ObjectModel;
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

    public OverviewViewModel(IDeviceService devices, IHostInfoService host, IShellLauncher shell)
    {
        _devices = devices;
        _host = host;
        _shell = shell;

        RefreshHost();

        // 构造时不阻塞 UI：首帧渲染后再探测设备。
        _ = Task.Run(async () =>
        {
            try
            {
                var info = await _devices.ProbeAsync().ConfigureAwait(false);
                ApplyDevice(info);
            }
            catch
            {
                // 探测失败保持"未连接"，不影响启动。
            }
        });
    }

    // ---------------- 手机状态 ----------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsConnected), nameof(HasConnectionPath))]
    private DeviceInfo _device = DeviceInfo.Disconnected;

    public bool IsConnected => Device.IsConnected;

    public bool HasConnectionPath => Device.IsConnected;

    public string StatusText => Device.IsConnected ? "已连接" : "未连接";

    public string ConnectionPathText => Device.Mode.Display();

    /// <summary>设备具体信息（14 项，按用户指定顺序）。</summary>
    public ObservableCollection<DeviceFieldRow> DeviceFields { get; } = [];

    [ObservableProperty]
    private bool _isProbing;

    public async Task RefreshDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (IsProbing)
        {
            return;
        }

        IsProbing = true;
        try
        {
            var info = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
            ApplyDevice(info);
        }
        finally
        {
            IsProbing = false;
        }
    }

    private void ApplyDevice(DeviceInfo info)
    {
        Device = info;

        DeviceFields.Clear();
        foreach (var field in Enum.GetValues<DeviceField>())
        {
            DeviceFields.Add(new DeviceFieldRow(field, info.Value(field)));
        }
    }

    [RelayCommand]
    private async Task RefreshDevice(CancellationToken cancellationToken) =>
        await RefreshDeviceAsync(cancellationToken).ConfigureAwait(false);

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
    private BackgroundTaskProgress _task = BackgroundTaskProgress.Idle;

    public bool IsTaskRunning => Task.IsRunning;

    public bool HasTask => Task.State != TaskState.Idle;

    public string TaskTitle => Task.IsRunning
        ? (string.IsNullOrWhiteSpace(Task.Title) ? "空闲" : Task.Title)
        : (Task.State == TaskState.Idle ? "空闲" : (Task.Title ?? "空闲"));

    public string TaskPercentText => Task.IsIndeterminate
        ? "---"
        : $"{Math.Clamp((int)Math.Round(Task.Percent), 0, 100)}%";

    public void ReportTask(BackgroundTaskProgress progress) => Task = progress;

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
