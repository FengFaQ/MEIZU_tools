using System.Diagnostics;

namespace MeiyouToolbox.Services;

/// <summary>概览页"快捷打开"里的四个动作。</summary>
public interface IShellLauncher
{
    /// <summary>打开计算机管理（mmc.exe）。</summary>
    void OpenComputerManagement();

    /// <summary>在工具目录打开一个 CMD，可直接使用 adb / fastboot。</summary>
    void OpenTerminalAtTools();

    /// <summary>打开任务管理器。</summary>
    void OpenTaskManager();

    /// <summary>在资源管理器中打开本程序运行目录。</summary>
    void OpenAppDirectory();

    /// <summary>用资源管理器打开任意目录。</summary>
    void OpenFolder(string path);
}

public sealed class ShellLauncher : IShellLauncher
{
    private readonly IToolLocator _tools;

    public ShellLauncher(IToolLocator tools) => _tools = tools;

    public void OpenComputerManagement() =>
        Start("mmc.exe", null, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "compmgmt.msc"));

    public void OpenTerminalAtTools()
    {
        var directory = _tools.ToolsDirectory;
        if (!Directory.Exists(directory))
        {
            // 目录不存在时退到程序目录，至少能用相对路径
            directory = _tools.AppDirectory;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            WorkingDirectory = directory,
            UseShellExecute = true,
        };

        // 让这个 CMD 里 adb / fastboot 可直接调用，等价于原工具箱"命令行.bat"的效果。
        psi.Arguments = $"/k set \"PATH={directory};%PATH%\" & echo 魅友工具箱命令行 - 当前目录: {directory}";
        TryStart(psi);
    }

    public void OpenTaskManager() =>
        Start(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe"), null, null);

    public void OpenAppDirectory() => OpenFolder(_tools.AppDirectory);

    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // 无法创建就交给 shell 报错
        }

        Start("explorer.exe", path, null);
    }

    private static void Start(string fileName, string? arguments, string? fallbackFileName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = true,
        };
        if (!string.IsNullOrEmpty(arguments))
        {
            psi.Arguments = arguments;
        }

        try
        {
            Process.Start(psi);
        }
        catch
        {
            if (fallbackFileName is not null && File.Exists(fallbackFileName))
            {
                TryStart(new ProcessStartInfo { FileName = fallbackFileName, UseShellExecute = true });
            }
        }
    }

    private static void TryStart(ProcessStartInfo psi)
    {
        try
        {
            Process.Start(psi);
        }
        catch
        {
            // 启动失败不抛出，避免让 UI 崩溃
        }
    }
}
