using System.Diagnostics;
using System.Text;

namespace MeiyouToolbox.Services;

/// <summary>外部命令执行结果。</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    public string Combined => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;
}

/// <summary>
/// 运行外部可执行文件（adb / fastboot / 9008 工具）。统一处理超时、取消与编码。
/// 参考实现里 26 个工具都是这样被调用的，但原版没有超时控制，这里补上。
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式执行：每产出一行就回调 <paramref name="onLine"/>。
    /// 刷写 / 备份这类几分钟的长任务必须用它，否则用户面对的是一个不动的界面。
    /// </summary>
    Task<ProcessResult> RunStreamingAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onLine,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default) =>
        RunStreamingAsync(fileName, arguments, null, workingDirectory, timeout, cancellationToken);

    public async Task<ProcessResult> RunStreamingAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onLine,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = psi };

            if (onLine is not null)
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null)
                    {
                        return;
                    }

                    stdout.AppendLine(e.Data);
                    onLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null)
                    {
                        return;
                    }

                    stderr.AppendLine(e.Data);
                    onLine(e.Data);
                };
            }

            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, $"无法启动 {fileName}");
            }

            if (onLine is not null)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            Task<string> stdoutTask = onLine is null
                ? process.StandardOutput.ReadToEndAsync(cancellationToken)
                : Task.FromResult(string.Empty);
            Task<string> stderrTask = onLine is null
                ? process.StandardError.ReadToEndAsync(cancellationToken)
                : Task.FromResult(string.Empty);

            using var timeoutCts = timeout is { } t
                ? new CancellationTokenSource(t)
                : new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                var reason = timeoutCts.IsCancellationRequested ? "命令超时" : "命令已取消";
                return new ProcessResult(-1, stdout.ToString(), $"{reason}: {fileName}");
            }

            if (onLine is null)
            {
                return new ProcessResult(
                    process.ExitCode,
                    await stdoutTask.ConfigureAwait(false),
                    await stderrTask.ConfigureAwait(false));
            }

            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, stdout.ToString(), ex.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能已经退出，忽略。
        }
    }
}
