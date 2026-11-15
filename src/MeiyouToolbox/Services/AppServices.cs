using Microsoft.Extensions.DependencyInjection;
using MeiyouToolbox.ViewModels;

namespace MeiyouToolbox.Services;

/// <summary>
/// 应用的服务注册。所有注册都写出具体类型，容器不需要反射扫描程序集，
/// 这样裁剪（trimming）与 NativeAOT 才能工作 —— 和 Arc 模板的做法一致。
/// </summary>
public static class AppServices
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        // 基础设施
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IToolLocator, ToolLocator>();
        services.AddSingleton<IShellLauncher, ShellLauncher>();

        // 日志（依赖工具目录，用于定位程序目录）
        services.AddSingleton<IAppLogger>(sp =>
        {
            var tools = sp.GetRequiredService<IToolLocator>();
            return new AppLogger(tools.AppDirectory);
        });

        // 业务服务
        services.AddSingleton<IHostInfoService, HostInfoService>();
        services.AddSingleton<IDeviceService, DeviceService>();
        services.AddSingleton<IScreenshotService, ScreenshotService>();

        // 机型表：内部要从工具目录找 devices.csv，所以延迟到容器里构造
        services.AddSingleton<IDeviceProfileCatalog>(sp =>
        {
            var tools = sp.GetRequiredService<IToolLocator>();
            var logger = sp.GetRequiredService<IAppLogger>();
            return new DeviceProfileCatalog(tools, logger);
        });

        // 刷机相关
        services.AddSingleton<ICredentialStore, CredentialStore>();
        services.AddSingleton<IFirehoseService, FirehoseService>();
        services.AddSingleton<IPartitionTableService, PartitionTableService>();
        services.AddSingleton<IEdlReader, EdlReader>();
        services.AddSingleton<IFlashService, FlashService>();

        // 备份相关
        services.AddSingleton<IBackupService, BackupService>();

        // 管理相关
        services.AddSingleton<ISlotService, SlotService>();
        services.AddSingleton<ICleanService, CleanService>();

        // 模式切换与任务收尾（解锁 / 上锁自动化，以及"完成任务提示"的两个按钮）
        services.AddSingleton<IDeviceModeService, DeviceModeService>();
        services.AddSingleton<IPostTaskService, PostTaskService>();

        // 视图模型
        services.AddSingleton<TaskCompleteViewModel>();
        services.AddSingleton<EdlFlashViewModel>();
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<FlashViewModel>();
        services.AddSingleton<BackupViewModel>();
        services.AddSingleton<ManageViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
