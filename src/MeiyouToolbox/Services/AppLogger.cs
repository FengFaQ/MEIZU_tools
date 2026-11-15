using System.Text;

namespace MeiyouToolbox.Services;

/// <summary>
/// 运行期日志：实时写入程序目录下的 <c>logs\app-yyyyMMdd.log</c>。
/// 同时保留在内存里，便于内置"导出日志"功能一次性打包。
/// </summary>
public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
    string LogDirectory { get; }
    string? CurrentLogFile { get; }

    /// <summary>每写入一行就触发一次（可能在任意线程），概览页用它做实时日志。</summary>
    event Action<string>? LineWritten;

    /// <summary>取内存里保留的近期日志（最多 5000 行）。</summary>
    IReadOnlyList<string> Snapshot();
}

public sealed class AppLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly List<string> _buffer = [];

    public AppLogger(string runtimeDirectory)
    {
        LogDirectory = Path.Combine(runtimeDirectory, "logs");
        try
        {
            Directory.CreateDirectory(LogDirectory);
            CurrentLogFile = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            CurrentLogFile = null;
        }

        Info($"===== 魅友工具箱启动 (v{AppVersionInfo.Current}) =====");
        Info($"运行时: {Environment.Version} / OS: {Environment.OSVersion}");
        Info($"程序目录: {runtimeDirectory}");
    }

    public string LogDirectory { get; }

    public string? CurrentLogFile { get; }

    /// <summary>每写入一行就触发一次。可能在任意线程，订阅方自行切回 UI 线程。</summary>
    public event Action<string>? LineWritten;

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";

        lock (_gate)
        {
            _buffer.Add(line);
            if (_buffer.Count > 5000)
            {
                _buffer.RemoveRange(0, 1000);
            }

            if (CurrentLogFile is not null)
            {
                try
                {
                    File.AppendAllText(CurrentLogFile, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // 日志写失败不能影响主流程
                }
            }
        }

        // 在锁外通知订阅方：回调里可能又调用 Snapshot()，避免重入复杂化。
        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // 订阅方异常不能影响日志本身
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _buffer.ToArray();
        }
    }
}
