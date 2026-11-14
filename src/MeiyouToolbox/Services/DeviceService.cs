using System.Text.RegularExpressions;
using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>
/// 设备探测与信息读取。对应原工具箱的 chkdev + mzbox + info 三组能力，
/// 但改成有超时、有明确返回值的异步调用。
/// </summary>
public interface IDeviceService
{
    /// <summary>探测当前连接方式并尽量读取设备信息。</summary>
    Task<DeviceInfo> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed partial class DeviceService : IDeviceService
{
    private readonly IProcessRunner _runner;
    private readonly IToolLocator _tools;

    public DeviceService(IProcessRunner runner, IToolLocator tools)
    {
        _runner = runner;
        _tools = tools;
    }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);

    public async Task<DeviceInfo> ProbeAsync(CancellationToken cancellationToken = default)
    {
        // 顺序与原实现一致：先 adb（覆盖 system/recovery/sideload），再 fastboot，最后 9008 端口。
        var adb = await ProbeAdbAsync(cancellationToken).ConfigureAwait(false);
        if (adb is not null)
        {
            return adb;
        }

        var fastboot = await ProbeFastbootAsync(cancellationToken).ConfigureAwait(false);
        if (fastboot is not null)
        {
            return fastboot;
        }

        var edl = await ProbeEdlAsync(cancellationToken).ConfigureAwait(false);
        if (edl is not null)
        {
            return edl;
        }

        return DeviceInfo.Disconnected;
    }

    // ---------- adb ----------

    private async Task<DeviceInfo?> ProbeAdbAsync(CancellationToken cancellationToken)
    {
        var adb = _tools.AdbPath;
        if (adb is null)
        {
            return null;
        }

        var result = await _runner.RunAsync(
            adb, ["devices", "-l"], timeout: ProbeTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return null;
        }

        // 参考实现要求"有且只有一个设备"：多设备时选错线索会导致刷错机。
        var devices = new List<string>();
        foreach (var raw in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 ||
                line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith('*'))
            {
                continue;
            }

            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && !parts[1].Contains("offline", StringComparison.OrdinalIgnoreCase))
            {
                devices.Add(parts[0].Trim());
            }
        }

        if (devices.Count == 0)
        {
            return null;
        }

        var serial = devices[0];

        // 区分 system / recovery / sideload
        var mode = ConnectionMode.System;
        var bootState = await GetPropAsync(adb, serial, "ro.bootmode", cancellationToken).ConfigureAwait(false);
        var recovery = await GetPropAsync(adb, serial, "ro.boot.verifiedbootstate", cancellationToken)
            .ConfigureAwait(false);
        _ = recovery;

        // adb 在 recovery 下 getprop service.bootanim.exit 等不可靠，用 /sbin 判断更稳
        var isRecovery = await RunAdbShellAsync(adb, serial, "test -d /sbin && echo REC", cancellationToken)
            .ConfigureAwait(false);
        if (isRecovery.Contains("REC", StringComparison.Ordinal))
        {
            mode = ConnectionMode.Recovery;
        }
        else if (bootState.Contains("sideload", StringComparison.OrdinalIgnoreCase))
        {
            mode = ConnectionMode.Sideload;
        }

        return mode == ConnectionMode.Recovery
            ? await ReadAdbAsync(adb, serial, ConnectionMode.Recovery, cancellationToken).ConfigureAwait(false)
            : await ReadAdbAsync(adb, serial, mode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeviceInfo> ReadAdbAsync(
        string adb, string serial, ConnectionMode mode, CancellationToken cancellationToken)
    {
        async Task<string?> Prop(string key) =>
            NullIfEmpty(await GetPropAsync(adb, serial, key, cancellationToken).ConfigureAwait(false));

        var brand = await Prop("ro.product.brand").ConfigureAwait(false);
        var manufacturer = await Prop("ro.product.manufacturer").ConfigureAwait(false);
        var model = await Prop("ro.product.model").ConfigureAwait(false);
        var device = await Prop("ro.product.device").ConfigureAwait(false);
        var systemVersion = NullIfEmpty(
            await RunAdbShellAsync(adb, serial, "getprop ro.build.display.id", cancellationToken)
                .ConfigureAwait(false));
        var androidVersion = await Prop("ro.build.version.release").ConfigureAwait(false);
        var sdk = await Prop("ro.build.version.sdk").ConfigureAwait(false);
        var abi = await Prop("ro.product.cpu.abi").ConfigureAwait(false);
        var platform = await Prop("ro.board.platform").ConfigureAwait(false);
        var bootloader = await Prop("ro.bootloader").ConfigureAwait(false);
        var buildVersion = NullIfEmpty(
            await RunAdbShellAsync(adb, serial, "getprop ro.build.fingerprint", cancellationToken)
                .ConfigureAwait(false));
        var kernel = NullIfEmpty(
            await RunAdbShellAsync(adb, serial, "uname -r", cancellationToken).ConfigureAwait(false));
        var serialNo = NullIfEmpty(
            await RunAdbShellAsync(adb, serial, "getprop ro.serialno", cancellationToken).ConfigureAwait(false));

        // 电池
        var battery = NullIfEmpty(
            await RunAdbShellAsync(adb, serial, "dumpsys battery | grep -E 'level|status'", cancellationToken)
                .ConfigureAwait(false));

        // 屏幕分辨率
        var display = NullIfEmpty(
            await RunAdbShellAsync(adb, serial, "wm size", cancellationToken).ConfigureAwait(false));

        // BL 状态：ro.boot.verifiedbootstate + ro.boot.flash.locked
        var vbState = await Prop("ro.boot.verifiedbootstate").ConfigureAwait(false);
        var flashLocked = await Prop("ro.boot.flash.locked").ConfigureAwait(false);
        var bootloaderState = flashLocked switch
        {
            "1" => "已上锁",
            "0" => "已解锁",
            _ => vbState is null ? null : (vbState.Equals("green", StringComparison.OrdinalIgnoreCase)
                ? "已上锁"
                : "已解锁"),
        };

        return new DeviceInfo
        {
            Mode = mode,
            Brand = brand ?? manufacturer,
            Model = model,
            Codename = device,
            SystemVersion = systemVersion ?? androidVersion,
            BootloaderState = bootloaderState,
            MainboardId = platform,
            CpuArchitecture = abi,
            StorageType = null,
            DisplayInfo = display is null ? null : NormalizeDisplay(display),
            BootState = mode == ConnectionMode.Recovery ? "Recovery" : "已开机",
            BatteryInfo = battery is null ? null : NormalizeBattery(battery),
            SystemArchitecture = sdk is null ? null : $"{abi} (API {sdk})",
            BuildVersion = buildVersion,
            KernelVersion = kernel,
            SerialOrPort = serialNo ?? serial,
        };
    }

    private static string NormalizeDisplay(string raw)
    {
        var match = Regex.Match(raw, @"(\d+)\s*x\s*(\d+)");
        return match.Success ? $"{match.Groups[1].Value} x {match.Groups[2].Value}" : raw.Trim();
    }

    private static string NormalizeBattery(string raw)
    {
        var level = Regex.Match(raw, @"level:\s*(\d+)");
        var status = Regex.Match(raw, @"status:\s*(\d+)");
        if (!level.Success)
        {
            return raw.Trim();
        }

        var percent = level.Groups[1].Value;
        // dumpsys battery: status 2 = charging, 5 = full
        var charging = status.Success && status.Groups[1].Value is "2" or "5";
        return charging ? $"{percent}% (充电中)" : $"{percent}%";
    }

    // ---------- fastboot ----------

    private async Task<DeviceInfo?> ProbeFastbootAsync(CancellationToken cancellationToken)
    {
        var fastboot = _tools.FastbootPath;
        if (fastboot is null)
        {
            return null;
        }

        var result = await _runner.RunAsync(
            fastboot, ["devices"], timeout: ProbeTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return null;
        }

        var lines = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.EndsWith("fastboot", StringComparison.OrdinalIgnoreCase) ||
                        l.Contains(" fastboot", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (lines.Count == 0)
        {
            return null;
        }

        var serial = lines[0].Split('\t', ' ')[0].Trim();

        // 区分 fastboot 与 fastbootd：is-userspace=yes 即 fastbootd。
        // 参考实现 core 缺陷之一就是漏了这一步，这里补上。
        var userspace = await _runner.RunAsync(
            fastboot, ["getvar", "is-userspace"], timeout: ProbeTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var isUserspace = userspace.Combined.Contains("is-userspace: yes", StringComparison.OrdinalIgnoreCase);
        var mode = isUserspace ? ConnectionMode.FastbootD : ConnectionMode.Fastboot;

        var product = NullIfEmpty(await GetVarAsync(fastboot, "product", cancellationToken).ConfigureAwait(false));
        var unlocked = NullIfEmpty(await GetVarAsync(fastboot, "unlocked", cancellationToken).ConfigureAwait(false));
        var variant = NullIfEmpty(await GetVarAsync(fastboot, "variant", cancellationToken).ConfigureAwait(false));
        var secure = NullIfEmpty(await GetVarAsync(fastboot, "secure", cancellationToken).ConfigureAwait(false));
        var battery = NullIfEmpty(await GetVarAsync(fastboot, "battery-voltage", cancellationToken)
            .ConfigureAwait(false));

        return new DeviceInfo
        {
            Mode = mode,
            Brand = "Meizu",
            Model = variant,
            Codename = product,
            SystemVersion = null,
            BootloaderState = unlocked switch
            {
                "yes" or "true" => "已解锁",
                "no" or "false" => "已上锁",
                _ => null,
            },
            MainboardId = null,
            CpuArchitecture = null,
            StorageType = null,
            DisplayInfo = null,
            BootState = mode == ConnectionMode.FastbootD ? "FastbootD" : "Fastboot",
            BatteryInfo = battery,
            SystemArchitecture = null,
            BuildVersion = null,
            KernelVersion = null,
            SerialOrPort = serial + (secure is null ? string.Empty : $" (secure={secure})"),
        };
    }

    private async Task<string> GetVarAsync(string fastboot, string var, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            fastboot, ["getvar", var], timeout: ProbeTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var match = Regex.Match(result.Combined, $@"^{Regex.Escape(var)}:\s*(.+)$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    // ---------- 9008 / EDL ----------

    private async Task<DeviceInfo?> ProbeEdlAsync(CancellationToken cancellationToken)
    {
        // 9008 是串口设备，Windows 上用 WMI 查一次端口名即可，不必依赖 devcon。
        const string script =
            "Get-CimInstance Win32_PnPEntity | " +
            "Where-Object { $_.Name -match 'QDLoader 9008|QDLoader 900E|9008' } | " +
            "Select-Object -First 1 -ExpandProperty Name";

        var result = await _runner.RunAsync(
            "powershell",
            ["-NoProfile", "-NonInteractive", "-Command", script],
            timeout: ProbeTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        var name = result.StandardOutput.Trim();
        var portMatch = Regex.Match(name, @"\(COM(\d+)\)");

        return new DeviceInfo
        {
            Mode = ConnectionMode.QcEdl,
            Brand = "Meizu",
            BootState = "9008 (EDL)",
            SerialOrPort = portMatch.Success ? $"COM{portMatch.Groups[1].Value}" : name,
        };
    }

    // ---------- helpers ----------

    private async Task<string> GetPropAsync(
        string adb, string serial, string key, CancellationToken cancellationToken)
    {
        return await RunAdbShellAsync(adb, serial, $"getprop {key}", cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> RunAdbShellAsync(
        string adb, string serial, string shellCommand, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            adb, ["-s", serial, "shell", shellCommand], timeout: ProbeTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.StandardOutput.Replace("\r", string.Empty).Trim();
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
