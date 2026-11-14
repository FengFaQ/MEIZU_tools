using System.Runtime.InteropServices;

namespace MeiyouToolbox.Services;

/// <summary>
/// 定位随程序分发的外部工具。原工具箱把所有 exe 放在 bin\tool\Win 下并加进 PATH；
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

    public string? AdbPath => Locate("adb" + ExecutableSuffix);

    public string? FastbootPath => Locate("fastboot" + ExecutableSuffix);

    private static string ExecutableSuffix =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;

    private string ResolveToolsDirectory()
    {
        // 依次尝试：运行目录\Tools、运行目录\tool\Win（兼容原工具箱目录结构）
        var candidates = new[]
        {
            Path.Combine(AppDirectory, "Tools"),
            Path.Combine(AppDirectory, "tool", "Win"),
            Path.Combine(AppDirectory, "..", "Tools"),
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

        return Path.Combine(AppDirectory, "Tools");
    }

    private string? Locate(string fileName)
    {
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
}
