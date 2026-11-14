using System.Diagnostics;
using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>本机（电脑）状态：电量与软件版本。</summary>
public sealed record HostStatus(int? BatteryPercent, bool IsCharging, string AppVersion)
{
    public string BatteryText => BatteryPercent is { } p
        ? (IsCharging ? $"{p}% (充电中)" : $"{p}%")
        : "---";
}

public interface IHostInfoService
{
    HostStatus GetStatus();
}

public sealed class HostInfoService : IHostInfoService
{
    public HostStatus GetStatus() => new(
        TryGetBatteryPercent(out var percent, out var charging) ? percent : null,
        charging,
        AppVersionInfo.Current);

    private static bool TryGetBatteryPercent(out int percent, out bool charging)
    {
        percent = 0;
        charging = false;

        try
        {
            // 用 PowerShell 取一次电量，避免为了一个数字引入 System.Management 依赖。
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                "(Get-CimInstance Win32_Battery | Select-Object -First 1) | " +
                "ForEach-Object { \"$($_.EstimatedChargeRemaining)|$($_.BatteryStatus)\" }");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(true); } catch { /* ignore */ }
                return false;
            }

            var text = output.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var parts = text.Split('|', 2);
            if (!int.TryParse(parts[0].Trim(), out percent))
            {
                return false;
            }

            // Win32_Battery.BatteryStatus == 2 表示正在充电/接通外部电源。
            charging = parts.Length > 1 && parts[1].Trim() == "2";
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public static class AppVersionInfo
{
    public static string Current { get; } =
        typeof(AppVersionInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
