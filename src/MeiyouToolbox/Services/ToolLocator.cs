using System.Runtime.InteropServices;

namespace MeiyouToolbox.Services;

/// <summary>
/// 定位随程序分发的外部工具。参考实现把所有 exe 放在 <c>bin\tool\Win</c> 下并加进 PATH；
/// 这里改为显式定位，找不到时返回 null，由调用方降级处理（而不是像原版那样直接 FATAL）。
/// </summary>
public interface IToolLocator
{
    string? AdbPath { get; }
    string? FastbootPath { get; }

    /// <summary>工具目录绝对路径（不存在时返回预期路径，便于"终端"按钮直接定位）。</summary>
    string ToolsDirectory { get; }

    /// <summary>程序的运行目录（exe 所在目录，或开发时的输出目录）。</summary>
    string AppDirectory { get; }

    /// <summary>
    /// 在工具目录 / 系统 PATH 中查找任意工具，返回值不存在时返回 null。
    /// 9008 那一串工具（QSaharaServer、fh_loader、qcedlcmdhelper…）都走这里。
    /// </summary>
    string? FindTool(string fileName);

    /// <summary>该工具是否可用（界面据此禁用按钮并给出"缺少工具"的提示）。</summary>
    bool HasTool(string fileName);
}

public sealed class ToolLocator : IToolLocator
{
    private readonly Lazy<string> _toolsDirectory;

    public ToolLocator()
    {
        AppDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        _toolsDirectory = new Lazy<string>(ResolveToolsDirectory);
    }

    /// <inheritdoc />
    public string AppDirectory { get; }

    public string ToolsDirectory => _toolsDirectory.Value;

    public string? AdbPath => FindTool("adb" + ExecutableSuffix);

    public string? FastbootPath => FindTool("fastboot" + ExecutableSuffix);

    private static string ExecutableSuffix =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;

    private string ResolveToolsDirectory()
    {
        // 依次尝试：运行目录\tools、运行目录\Tools、运行目录\tool\Win（兼容原工具箱目录结构）
        var candidates = new[]
        {
            Path.Combine(AppDirectory, "tools"),
            Path.Combine(AppDirectory, "Tools"),
            Path.Combine(AppDirectory, "tool", "Win"),
            Path.Combine(AppDirectory, "..", "tools"),
            Path.Combine(AppDirectory, "..", "tool", "Win"),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (Directory.Exists(full))
            {
                return full;
            }
        }

        return Path.Combine(AppDirectory, "tools");
    }

    public string? FindTool(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var local = Path.Combine(ToolsDirectory, fileName);
        if (File.Exists(local))
        {
            return local;
        }

        // 退化到系统 PATH（用户可能自己装了 platform-tools）
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 非法路径段，忽略
            }
        }

        return null;
    }

    public bool HasTool(string fileName) => FindTool(fileName) is not null;
}
