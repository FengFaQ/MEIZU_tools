namespace MeiyouToolbox.Models;

/// <summary>设备连接方式。对应原工具箱的 chkdev 模式探测。</summary>
public enum ConnectionMode
{
    /// <summary>未连接。</summary>
    None,
    /// <summary>开机状态（adb）。</summary>
    System,
    /// <summary>Recovery（adb）。</summary>
    Recovery,
    /// <summary>Sideload（adb）。</summary>
    Sideload,
    /// <summary>Fastboot。</summary>
    Fastboot,
    /// <summary>FastbootD（用户空间 fastboot）。</summary>
    FastbootD,
    /// <summary>9008 / EDL。</summary>
    QcEdl,
    /// <summary>高通基带调试口（QCN 用）。</summary>
    QcDiag,
}

public static class ConnectionModeNames
{
    public static string Display(this ConnectionMode mode) => mode switch
    {
        ConnectionMode.System => "系统 (ADB)",
        ConnectionMode.Recovery => "Recovery",
        ConnectionMode.Sideload => "Sideload",
        ConnectionMode.Fastboot => "Fastboot",
        ConnectionMode.FastbootD => "FastbootD",
        ConnectionMode.QcEdl => "9008 (EDL)",
        ConnectionMode.QcDiag => "基带调试 (DIAG)",
        _ => "未连接",
    };
}

/// <summary>
/// 设备信息快照。不同连接方式能读到的字段不同，读不到的保持 null，
/// 由界面统一渲染成 "---"。
/// </summary>
public sealed class DeviceInfo
{
    public ConnectionMode Mode { get; init; } = ConnectionMode.None;

    public bool IsConnected => Mode != ConnectionMode.None;

    public string? Brand { get; init; }
    public string? Model { get; init; }
    public string? Codename { get; init; }
    public string? SystemVersion { get; init; }
    public string? BootloaderState { get; init; }
    public string? MainboardId { get; init; }
    public string? CpuArchitecture { get; init; }
    public string? StorageType { get; init; }
    public string? DisplayInfo { get; init; }
    public string? BootState { get; init; }
    public string? BatteryInfo { get; init; }
    public string? SystemArchitecture { get; init; }
    public string? BuildVersion { get; init; }
    public string? KernelVersion { get; init; }

    /// <summary>adb 串号 / 端口号，便于诊断。</summary>
    public string? SerialOrPort { get; init; }

    public static DeviceInfo Disconnected { get; } = new();
}

/// <summary>可显示的设备信息字段（顺序即界面顺序）。</summary>
public enum DeviceField
{
    Brand,
    Model,
    Codename,
    SystemVersion,
    BootloaderState,
    MainboardId,
    CpuArchitecture,
    StorageType,
    DisplayInfo,
    BootState,
    BatteryInfo,
    SystemArchitecture,
    BuildVersion,
    KernelVersion,
}

public static class DeviceFieldNames
{
    public static string Label(this DeviceField field) => field switch
    {
        DeviceField.Brand => "品牌",
        DeviceField.Model => "型号",
        DeviceField.Codename => "代号",
        DeviceField.SystemVersion => "系统",
        DeviceField.BootloaderState => "BL 状态",
        DeviceField.MainboardId => "主板 ID",
        DeviceField.CpuArchitecture => "CPU 架构",
        DeviceField.StorageType => "闪存类型",
        DeviceField.DisplayInfo => "显示信息",
        DeviceField.BootState => "开机状态",
        DeviceField.BatteryInfo => "电池信息",
        DeviceField.SystemArchitecture => "系统架构",
        DeviceField.BuildVersion => "编译版本",
        DeviceField.KernelVersion => "内核版本",
        _ => field.ToString(),
    };

    public static string? Value(this DeviceInfo info, DeviceField field) => field switch
    {
        DeviceField.Brand => info.Brand,
        DeviceField.Model => info.Model,
        DeviceField.Codename => info.Codename,
        DeviceField.SystemVersion => info.SystemVersion,
        DeviceField.BootloaderState => info.BootloaderState,
        DeviceField.MainboardId => info.MainboardId,
        DeviceField.CpuArchitecture => info.CpuArchitecture,
        DeviceField.StorageType => info.StorageType,
        DeviceField.DisplayInfo => info.DisplayInfo,
        DeviceField.BootState => info.BootState,
        DeviceField.BatteryInfo => info.BatteryInfo,
        DeviceField.SystemArchitecture => info.SystemArchitecture,
        DeviceField.BuildVersion => info.BuildVersion,
        DeviceField.KernelVersion => info.KernelVersion,
        _ => null,
    };
}
