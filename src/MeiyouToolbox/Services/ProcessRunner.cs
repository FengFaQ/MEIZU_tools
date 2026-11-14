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
/// 运行外部可执行文件（adb / fastboot / 系统程序）。统一处理超时、取消与编码。
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
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
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

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, $"无法启动 {fileName}");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

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
                return new ProcessResult(-1, string.Empty, $"{reason}: {fileName}");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
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
