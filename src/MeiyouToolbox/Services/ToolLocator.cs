using System.Runtime.InteropServices;

namespace MeiyouToolbox.Services;

/// <summary>
/// 定位随程序分发的外部工具。
/// 目录结构对齐原魅族工具箱（<c>bin\tool\Win</c> 等），迁移后的布局是：
/// <code>
/// Tools\Win\      Windows 端 exe（adb / fastboot / QSaharaServer / fh_loader / gpttool …）
/// Tools\Android\  推送到手机的二进制（busybox / mke2fs / mkntfs / misc_*.img …）
/// Tools\Other\    杂项脚本
/// Tools\res\      各机型的 9008 programmer（res\&lt;product&gt;\devprg）
/// </code>
/// 找不到时返回 null，由调用方降级处理（原版是直接 FATAL）。
/// </summary>
public interface IToolLocator
{
    string? AdbPath { get; }
    string? FastbootPath { get; }

    /// <summary>Windows 工具目录绝对路径（不存在时返回预期路径，便于"终端"按钮直接定位）。</summary>
    string ToolsDirectory { get; }

    /// <summary>随包目录根（<c>Tools</c>），下面有 Win / Android / Other / res 四个子目录。</summary>
    string ToolsRoot { get; }

    /// <summary>推送到手机的二进制所在目录。</summary>
    string AndroidToolsDirectory { get; }

    /// <summary>机型资源根目录（<c>res\&lt;product&gt;</c>）。</summary>
    string ResourcesDirectory { get; }

    /// <summary>程序的运行目录（exe 所在目录，或开发时的输出目录）。</summary>
    string AppDirectory { get; }

    /// <summary>
    /// 在工具目录 / 系统 PATH 中查找任意工具，不存在时返回 null。
    /// 9008 那一串工具（QSaharaServer、fh_loader、qcedlcmdhelper…）都走这里。
    /// </summary>
    string? FindTool(string fileName);

    /// <summary>该工具是否可用（界面据此禁用按钮并给出"缺少工具"的提示）。</summary>
    bool HasTool(string fileName);

    /// <summary>取某机型的 9008 引导文件（<c>res\&lt;product&gt;\devprg</c>），没有则返回 null。</summary>
    string? FindProgrammer(string? product);

    /// <summary>取某机型的资源目录（<c>res\&lt;product&gt;</c>），没有则返回 null。</summary>
    string? FindResourceDirectory(string? product);
}

public sealed class ToolLocator : IToolLocator
{
    private readonly Lazy<string> _toolsRoot;
    private readonly Lazy<string> _toolsDirectory;

    public ToolLocator()
    {
        AppDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        _toolsRoot = new Lazy<string>(ResolveToolsRoot);
        _toolsDirectory = new Lazy<string>(ResolveToolsDirectory);
    }

    /// <inheritdoc />
    public string AppDirectory { get; }

    public string ToolsRoot => _toolsRoot.Value;

    public string ToolsDirectory => _toolsDirectory.Value;

    public string AndroidToolsDirectory => Path.Combine(ToolsRoot, "Android");

    public string ResourcesDirectory => Path.Combine(ToolsRoot, "res");

    public string? AdbPath => FindTool("adb" + ExecutableSuffix);

    public string? FastbootPath => FindTool("fastboot" + ExecutableSuffix);

    private static string ExecutableSuffix =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;

    /// <summary>
    /// 找 <c>Tools</c> 根目录。优先运行目录下的 <c>Tools</c>，
    /// 同时兼容原工具箱的 <c>tool</c>（无 s）与上一层目录，方便直接拿原工具箱试跑。
    /// </summary>
    private string ResolveToolsRoot()
    {
        var candidates = new[]
        {
            Path.Combine(AppDirectory, "Tools"),
            Path.Combine(AppDirectory, "tools"),
            Path.Combine(AppDirectory, "tool"),
            Path.Combine(AppDirectory, "..", "Tools"),
            Path.Combine(AppDirectory, "..", "tool"),
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

    /// <summary>
    /// exe 实际落在 <c>Tools\Win</c>；但如果用户把 adb 直接丢在 <c>Tools</c> 根下也认，
    /// 所以这里返回最可能存在 exe 的那个目录。
    /// </summary>
    private string ResolveToolsDirectory()
    {
        var root = ToolsRoot;

        var win = Path.Combine(root, "Win");
        if (Directory.Exists(win))
        {
            return win;
        }

        // 旧布局：Tools 根下直接放 exe
        if (Directory.Exists(root))
        {
            return root;
        }

        return win;
    }

    public string? FindTool(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // 工具目录（Tools\Win），其次 Tools 根、Tools\Android
        foreach (var dir in new[] { ToolsDirectory, ToolsRoot, AndroidToolsDirectory })
        {
            var local = Path.Combine(dir, fileName);
            if (File.Exists(local))
            {
                return local;
            }
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

    public string? FindResourceDirectory(string? product)
    {
        if (string.IsNullOrWhiteSpace(product))
        {
            return null;
        }

        var dir = Path.Combine(ResourcesDirectory, product);
        return Directory.Exists(dir) ? dir : null;
    }

    public string? FindProgrammer(string? product)
    {
        var dir = FindResourceDirectory(product);
        if (dir is null)
        {
            return null;
        }

        // 原工具箱固定文件名是 devprg（无扩展名），另兼容带扩展名的变体。
        foreach (var name in new[] { "devprg", "devprg.bin", "devprg.mbn", "programmer.elf" })
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
