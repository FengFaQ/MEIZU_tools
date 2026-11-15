using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>一次刷写任务的执行结果。</summary>
public sealed record FlashResult(bool Success, string Message)
{
    public static FlashResult Ok(string message) => new(true, message);
    public static FlashResult Fail(string message) => new(false, message);
}

/// <summary>刷写进度的回调载荷。</summary>
public sealed record FlashProgress(string Message, double Percent = double.NaN)
{
    public bool IsIndeterminate => double.IsNaN(Percent);
}

/// <summary>
/// 刷机业务：把参考实现里 <c>write.bat</c> / <c>reboot.bat</c> / 主脚本的解锁流程
/// 重写成强类型方法。
/// <para>
/// 关键设计：**传输方式自动选择**。参考实现最优雅的一点是
/// "模式名直接当子命令名"，<c>call write %chkdev__mode% …</c> 一行适配五种通路。
/// 这里用 <see cref="FlashTransport"/> 表达同一件事，由 <see cref="ResolveTransport"/>
/// 根据当前设备连接方式决定，用户不需要关心设备现在在哪个模式。
/// </para>
/// </summary>
public interface IFlashService
{
    /// <summary>
    /// 刷入单个分区镜像。自动选择合适的传输方式。
    /// </summary>
    Task<FlashResult> FlashPartitionAsync(
        string partition,
        string imagePath,
        ConnectionMode mode,
        string? port = null,
        StorageKind memory = StorageKind.Auto,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 9008 整包线刷：把一个目录里的 rawprogram*.xml 全部刷入。
    /// </summary>
    Task<FlashResult> FlashRawPackageAsync(
        string packageDirectory,
        string? port,
        StorageKind memory,
        string? programmerPath,
        string? authCode,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>9008 只发送引导（不刷机），常用于让设备停在可操作状态。</summary>
    Task<FlashResult> SendFirehoseAsync(
        string? port,
        string programmerPath,
        StorageKind memory,
        string? authCode,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>重启到指定模式。</summary>
    Task<FlashResult> RebootAsync(
        ConnectionMode from,
        string toMode,
        string? port = null,
        StorageKind memory = StorageKind.Auto,
        CancellationToken cancellationToken = default);

    /// <summary>按机型方案解锁 Bootloader。</summary>
    Task<FlashResult> UnlockBootloaderAsync(
        DeviceProfile profile,
        DeviceCredentials credentials,
        ConnectionMode mode,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>上锁 Bootloader。危险操作，调用方必须先确认。</summary>
    Task<FlashResult> LockBootloaderAsync(
        DeviceProfile profile,
        ConnectionMode mode,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析刷写所需的传输方式。参考实现对应 <c>chkdev all</c> 之后的
    /// <c>call write %chkdev__mode%</c>。
    /// </summary>
    FlashTransport? ResolveTransport(ConnectionMode mode);
}

public sealed class FlashService : IFlashService
{
    private readonly IProcessRunner _runner;
    private readonly IToolLocator _tools;
    private readonly IFirehoseService _firehose;
    private readonly IPartitionTableService _partitions;
    private readonly IEdlReader _reader;
    private readonly IAppLogger _logger;

    public FlashService(
        IProcessRunner runner,
        IToolLocator tools,
        IFirehoseService firehose,
        IPartitionTableService partitions,
        IEdlReader reader,
        IAppLogger logger)
    {
        _runner = runner;
        _tools = tools;
        _firehose = firehose;
        _partitions = partitions;
        _reader = reader;
        _logger = logger;
    }

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FlashTimeout = TimeSpan.FromMinutes(15);

    public FlashTransport? ResolveTransport(ConnectionMode mode) => mode switch
    {
        ConnectionMode.System => FlashTransport.System,
        ConnectionMode.Recovery => FlashTransport.Recovery,
        ConnectionMode.Fastboot => FlashTransport.Fastboot,
        ConnectionMode.FastbootD => FlashTransport.FastbootD,
        ConnectionMode.QcEdl => FlashTransport.QcEdl,
        _ => null,
    };

    // ------------------------------------------------------------------
    // 单分区刷写
    // ------------------------------------------------------------------

    public async Task<FlashResult> FlashPartitionAsync(
        string partition,
        string imagePath,
        ConnectionMode mode,
        string? port = null,
        StorageKind memory = StorageKind.Auto,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partition))
        {
            return FlashResult.Fail("未指定分区名");
        }

        if (!File.Exists(imagePath))
        {
            return FlashResult.Fail($"找不到镜像文件：{imagePath}");
        }

        var transport = ResolveTransport(mode);
        if (transport is null)
        {
            return FlashResult.Fail("当前连接方式不支持刷写，请让设备进入系统 / Recovery / Fastboot / 9008");
        }

        _logger.Info($"刷入分区 {partition}：{imagePath}（{transport}）");

        return transport switch
        {
            FlashTransport.System => await FlashViaAdbDdAsync(
                partition, imagePath, RecoveryMode: false, progress, cancellationToken).ConfigureAwait(false),
            FlashTransport.Recovery => await FlashViaAdbDdAsync(
                partition, imagePath, RecoveryMode: true, progress, cancellationToken).ConfigureAwait(false),
            FlashTransport.Fastboot or FlashTransport.FastbootD =>
                await FlashViaFastbootAsync(partition, imagePath, progress, cancellationToken).ConfigureAwait(false),
            FlashTransport.QcEdl => await FlashViaEdlAsync(
                partition, imagePath, port, memory, progress, cancellationToken).ConfigureAwait(false),
            _ => FlashResult.Fail($"未实现的传输方式：{transport}"),
        };
    }

    /// <summary>
    /// 系统 / Recovery 下用 dd 刷入。参考实现先 <c>call write adbpush</c> 推到
    /// sdcard 或 data，再 <c>dd if=… of=/dev/block/by-name/…</c>。
    /// 这里保留同样思路，但分区路径优先用 <c>/dev/block/by-name/&lt;name&gt;</c>。
    /// </summary>
    private async Task<FlashResult> FlashViaAdbDdAsync(
        string partition,
        string imagePath,
        bool RecoveryMode,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var adb = _tools.AdbPath;
        if (adb is null)
        {
            return FlashResult.Fail("找不到 adb，请把 adb.exe 放到工具的 tools 目录");
        }

        var remote = $"/data/local/tmp/{Path.GetFileName(imagePath)}";

        progress?.Report(new FlashProgress($"正在推送到设备：{remote}"));
        var push = await _runner.RunStreamingAsync(
            adb, ["push", imagePath, remote],
            line => progress?.Report(new FlashProgress(line)),
            timeout: FlashTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!push.Success)
        {
            return FlashResult.Fail($"推送失败：{FirstLine(push.Combined)}");
        }

        // 系统模式要先确认有 Root，否则 dd 写块设备会被拒绝。
        if (!RecoveryMode)
        {
            var root = await _runner.RunAsync(
                adb, ["shell", "su", "-c", "id"],
                timeout: ShortTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!root.Combined.Contains("uid=0", StringComparison.Ordinal))
            {
                return FlashResult.Fail(
                    "设备未授予 Root 权限。请在手机的 Root 管理里给 Shell 授权后重试。");
            }
        }

        var devicePath = $"/dev/block/by-name/{partition}";
        var exists = await _runner.RunAsync(
            adb, ["shell", RecoveryMode ? "ls" : "su -c", RecoveryMode
                ? devicePath
                : $"ls {devicePath}"],
            timeout: ShortTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!exists.Combined.Contains(partition, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warn($"分区路径可能不存在：{devicePath}（继续尝试）");
        }

        progress?.Report(new FlashProgress($"正在写入 {devicePath}"));
        var ddCommand = RecoveryMode
            ? $"dd if={remote} of={devicePath}"
            : $"su -c 'dd if={remote} of={devicePath}'";

        var dd = await _runner.RunStreamingAsync(
            adb, ["shell", ddCommand],
            line => progress?.Report(new FlashProgress(line)),
            timeout: FlashTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        // 清理推送过去的临时文件，参考实现也在刷完后 rm。
        await _runner.RunAsync(
            adb, ["shell", RecoveryMode ? $"rm -f {remote}" : $"su -c 'rm -f {remote}'"],
            timeout: ShortTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        return dd.Success
            ? FlashResult.Ok($"已刷入 {partition}")
            : FlashResult.Fail($"dd 写入失败：{FirstLine(dd.Combined)}");
    }

    private async Task<FlashResult> FlashViaFastbootAsync(
        string partition,
        string imagePath,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fastboot = _tools.FastbootPath;
        if (fastboot is null)
        {
            return FlashResult.Fail("找不到 fastboot，请把 fastboot.exe 放到工具的 tools 目录");
        }

        progress?.Report(new FlashProgress($"正在刷入 {partition}…"));
        var result = await _runner.RunStreamingAsync(
            fastboot, ["flash", partition, imagePath],
            line => progress?.Report(new FlashProgress(line)),
            timeout: FlashTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Success || result.Combined.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
        {
            return FlashResult.Fail($"fastboot 刷入失败：{FirstLine(result.Combined)}");
        }

        return FlashResult.Ok($"已刷入 {partition}");
    }

    /// <summary>
    /// 9008 单分区：先读回 GPT 找到物理位置，再生成 rawprogram 节点刷入。
    /// 参考实现 <c>bin__write.bat.txt:L446-L462</c> 完全相同的流程。
    /// </summary>
    private async Task<FlashResult> FlashViaEdlAsync(
        string partition,
        string imagePath,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var resolvedPort = await ResolvePortAsync(port, cancellationToken).ConfigureAwait(false);
        if (resolvedPort is null)
        {
            return FlashResult.Fail("未检测到 9008 端口，请确认设备已进入 9008 并装好驱动");
        }

        var work = CreateWorkDirectory();
        var imageDir = Path.GetDirectoryName(Path.GetFullPath(imagePath)) ?? work;

        // sparse 判定：simg_dump 能读出头部就说明是 sparse 镜像。
        var sparse = await IsSparseImageAsync(imagePath, cancellationToken).ConfigureAwait(false);

        progress?.Report(new FlashProgress("正在读取分区表…"));
        var gpt = await ReadPartitionsAsync(resolvedPort, memory, work, progress, cancellationToken)
            .ConfigureAwait(false);

        var target = _partitions.Find(gpt, partition);
        if (target is null)
        {
            return FlashResult.Fail($"分区表里找不到分区 {partition}");
        }

        var xmlPath = Path.Combine(work, "rawprogram-single.xml");
        var node = _partitions.BuildProgramNode(
            target, Path.GetFileName(imagePath), memory, sparse);
        await File.WriteAllTextAsync(
            xmlPath,
            $"<?xml version=\"1.0\" ?><data>{node}</data>",
            cancellationToken).ConfigureAwait(false);

        var options = new EdlSessionOptions
        {
            Port = resolvedPort,
            Memory = memory,
            WorkDirectory = work,
        };

        // 若端口还没进 firehose，需要先发引导 —— 但单分区刷写通常由调用方保证。
        progress?.Report(new FlashProgress($"正在刷入 {partition}（起始扇区 {target.StartSector}）…"));
        var ok = await _firehose.SendXmlAsync(
            options, [xmlPath], imageDir, convertToRead: false, progress: ToEdlProgress(progress),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ok
            ? FlashResult.Ok($"已刷入 {partition}（{target.SizeText}）")
            : FlashResult.Fail($"9008 刷入失败：{DescribeSkipped()}");
    }

    // ------------------------------------------------------------------
    // 整包线刷
    // ------------------------------------------------------------------

    public async Task<FlashResult> FlashRawPackageAsync(
        string packageDirectory,
        string? port,
        StorageKind memory,
        string? programmerPath,
        string? authCode,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(packageDirectory))
        {
            return FlashResult.Fail($"找不到刷机包目录：{packageDirectory}");
        }

        var xmlFiles = Directory
            .EnumerateFiles(packageDirectory, "rawprogram*.xml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (xmlFiles.Count == 0)
        {
            return FlashResult.Fail(
                "该目录下没有 rawprogram*.xml。9008 线刷需要高通原厂格式的刷机包。");
        }

        var resolvedPort = await ResolvePortAsync(port, cancellationToken).ConfigureAwait(false);
        if (resolvedPort is null)
        {
            return FlashResult.Fail("未检测到 9008 端口");
        }

        var work = CreateWorkDirectory();
        var options = new EdlSessionOptions
        {
            Port = resolvedPort,
            ProgrammerPath = programmerPath,
            AuthCode = authCode,
            Memory = memory,
            WorkDirectory = work,
        };

        progress?.Report(new FlashProgress("正在准备 9008 会话…"));
        if (!await _firehose.PrepareAsync(options, ToEdlProgress(progress), cancellationToken)
                .ConfigureAwait(false))
        {
            return FlashResult.Fail("9008 会话准备失败（引导 / 授权 / 端口配置之一未通过）");
        }

        var ok = await _firehose.SendXmlAsync(
            options, xmlFiles, packageDirectory, convertToRead: false,
            progress: ToEdlProgress(progress), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // 刷完把可启动存储盘设对，否则"刷成功但不开机"。
        if (ok)
        {
            progress?.Report(new FlashProgress("正在设置可启动存储盘…"));
            await SetBootableStorageDriveAsync(resolvedPort, memory, cancellationToken)
                .ConfigureAwait(false);
        }

        return ok
            ? FlashResult.Ok($"线刷完成（{xmlFiles.Count} 个 XML）{DescribeSkipped()}")
            : FlashResult.Fail("线刷失败，请查看日志");
    }

    public async Task<FlashResult> SendFirehoseAsync(
        string? port,
        string programmerPath,
        StorageKind memory,
        string? authCode,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(programmerPath))
        {
            return FlashResult.Fail($"找不到引导文件：{programmerPath}");
        }

        var resolvedPort = await ResolvePortAsync(port, cancellationToken).ConfigureAwait(false);
        if (resolvedPort is null)
        {
            return FlashResult.Fail("未检测到 9008 端口");
        }

        var options = new EdlSessionOptions
        {
            Port = resolvedPort,
            ProgrammerPath = programmerPath,
            AuthCode = authCode,
            Memory = memory,
            WorkDirectory = CreateWorkDirectory(),
        };

        var ok = await _firehose.PrepareAsync(options, ToEdlProgress(progress), cancellationToken)
            .ConfigureAwait(false);

        return ok
            ? FlashResult.Ok($"引导已发送，端口 COM{resolvedPort} 已就绪")
            : FlashResult.Fail("发送引导失败");
    }

    // ------------------------------------------------------------------
    // 重启
    // ------------------------------------------------------------------

    public async Task<FlashResult> RebootAsync(
        ConnectionMode from,
        string toMode,
        string? port = null,
        StorageKind memory = StorageKind.Auto,
        CancellationToken cancellationToken = default)
    {
        var adb = _tools.AdbPath;
        var fastboot = _tools.FastbootPath;

        switch (from)
        {
            case ConnectionMode.System or ConnectionMode.Recovery or ConnectionMode.Sideload:
            {
                if (adb is null)
                {
                    return FlashResult.Fail("找不到 adb");
                }

                var argument = toMode switch
                {
                    "system" => null,
                    "recovery" => "recovery",
                    "fastboot" => "bootloader",
                    "fastbootd" => "fastboot",
                    "qcedl" => "edl",
                    "poweroff" => "-p",
                    _ => null,
                };

                var args = argument is null ? new[] { "reboot" } : new[] { "reboot", argument };
                var result = await _runner.RunAsync(
                    adb, args, timeout: ShortTimeout, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return result.Success
                    ? FlashResult.Ok($"已重启到 {toMode}")
                    : FlashResult.Fail($"重启失败：{FirstLine(result.Combined)}");
            }

            case ConnectionMode.Fastboot or ConnectionMode.FastbootD:
            {
                if (fastboot is null)
                {
                    return FlashResult.Fail("找不到 fastboot");
                }

                var argument = toMode switch
                {
                    "system" => "reboot",
                    "fastboot" => "reboot-bootloader",
                    "fastbootd" => "reboot fastboot",
                    "recovery" => "oem reboot-recovery",
                    "qcedl" => "oem edl",
                    "poweroff" => "oem poweroff",
                    _ => "reboot",
                };

                var args = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var result = await _runner.RunAsync(
                    fastboot, args, timeout: ShortTimeout, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return result.Success
                    ? FlashResult.Ok($"已重启到 {toMode}")
                    : FlashResult.Fail($"重启失败：{FirstLine(result.Combined)}");
            }

            case ConnectionMode.QcEdl:
            {
                var resolvedPort = await ResolvePortAsync(port, cancellationToken).ConfigureAwait(false);
                if (resolvedPort is null)
                {
                    return FlashResult.Fail("未检测到 9008 端口");
                }

                var backToEdl = toMode == "qcedl";
                var ok = await _firehose.ResetAsync(
                    resolvedPort, memory, backToEdl, cancellationToken).ConfigureAwait(false);

                return ok
                    ? FlashResult.Ok(backToEdl ? "已复位并留在 9008" : "已从 9008 重启到系统")
                    : FlashResult.Fail("9008 复位失败");
            }

            default:
                return FlashResult.Fail("当前连接方式不支持重启");
        }
    }

    // ------------------------------------------------------------------
    // 解锁 / 上锁
    // ------------------------------------------------------------------

    public async Task<FlashResult> UnlockBootloaderAsync(
        DeviceProfile profile,
        DeviceCredentials credentials,
        ConnectionMode mode,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _logger.Info($"{profile.Product}: 开始解锁，方案 {profile.UnlockPlan}");

        return profile.UnlockPlan switch
        {
            UnlockPlan.AuthPrivate => await UnlockPrivateAsync(
                profile, credentials, mode, port, memory, progress, cancellationToken).ConfigureAwait(false),
            UnlockPlan.AuthMzkvSig or UnlockPlan.AuthMzkvNoSig => await UnlockMzkvAsync(
                profile, credentials, port, memory, progress, cancellationToken).ConfigureAwait(false),
            UnlockPlan.Odm1 => await UnlockOdm1Async(
                profile, mode, port, memory, progress, cancellationToken).ConfigureAwait(false),
            UnlockPlan.Odm2 => await UnlockOdm2Async(progress, cancellationToken).ConfigureAwait(false),
            UnlockPlan.OdmNote8 => await UnlockOdmNote8Async(
                profile, credentials, mode, port, memory, progress, cancellationToken).ConfigureAwait(false),
            _ => FlashResult.Fail(
                $"机型 {profile.Product} 没有可用的解锁方案，请先在机型表里补全配置"),
        };
    }

    /// <summary>
    /// auth-private：有授权码走 9008 写 private 分区，否则降级 fastboot 分段解锁码。
    /// 参考实现 <c>煤油工具箱.bat:L753-L856</c>。
    /// </summary>
    private async Task<FlashResult> UnlockPrivateAsync(
        DeviceProfile profile,
        DeviceCredentials credentials,
        ConnectionMode mode,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var hasAuthCode = credentials.HasAuthCode;

        // 20 无界版没有可用的 9008，强制走 fastboot。
        var forceFastboot = profile.Product.Equals("meizu20inf", StringComparison.OrdinalIgnoreCase);

        if (hasAuthCode && !forceFastboot)
        {
            return await UnlockPrivateViaEdlAsync(
                profile, credentials, port, memory, progress, cancellationToken).ConfigureAwait(false);
        }

        if (!credentials.HasUnlockCode)
        {
            return FlashResult.Fail(
                "尚未导入解锁码。请先在「导入授权资料」里导入 512 字符的解锁码。");
        }

        return await UnlockPrivateViaFastbootAsync(
            credentials.UnlockCode!, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 9008 路线：在 private 分区的 4 个固定偏移写入 LOCK 标记、解锁码、factory 标记与版本标记。
    /// </summary>
    private async Task<FlashResult> UnlockPrivateViaEdlAsync(
        DeviceProfile profile,
        DeviceCredentials credentials,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var resolvedPort = await ResolvePortAsync(port, cancellationToken).ConfigureAwait(false);
        if (resolvedPort is null)
        {
            return FlashResult.Fail("未检测到 9008 端口");
        }

        var unlockCode = CredentialStore.Normalize(credentials.UnlockCode!);
        var work = CreateWorkDirectory();

        progress?.Report(new FlashProgress("正在构建解锁码写入脚本…"));

        // 版本标记跟随机型：18s / 18s Pro 用 rde5.4.5.0，其余用 mTest3.10.7.0。
        var versionMark = profile.Product is "meizu18s" or "meizu18sPro"
            ? "rde5.4.5.0"
            : "mTest3.10.7.0";

        var writes = new (long Offset, string Hex)[]
        {
            (0x11000, ToHex("LOCK")),
            (0x11004, ToHex(unlockCode)),
            (0x11104, ToHex("factory")),
            (0x11114, ToHex(versionMark)),
        };

        progress?.Report(new FlashProgress("正在读取 private 分区…"));
        var readOk = await ReadPrivateAsync(resolvedPort, work, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!readOk)
        {
            return FlashResult.Fail("读取 private 分区失败");
        }

        var imagePath = Path.Combine(work, "private.img");
        if (!await PatchHexAsync(imagePath, writes, progress, cancellationToken).ConfigureAwait(false))
        {
            return FlashResult.Fail("写入解锁标记失败，请查看日志");
        }

        progress?.Report(new FlashProgress("正在写回 private 分区…"));
        var writeResult = await FlashViaEdlAsync(
            "private", imagePath, resolvedPort, memory, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!writeResult.Success)
        {
            return writeResult;
        }

        await RebootAsync(ConnectionMode.QcEdl, "system", resolvedPort, memory, cancellationToken)
            .ConfigureAwait(false);

        return FlashResult.Ok("已写入解锁标记，设备重启后 BL 应处于解锁状态");
    }

    /// <summary>
    /// Fastboot 路线：512 字符解锁码切成 16 段 × 32 字符逐条下发。
    /// 参考实现把提示文字做成了藏头诗，这里保留原意但用正常文案。
    /// </summary>
    private async Task<FlashResult> UnlockPrivateViaFastbootAsync(
        string unlockCode,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fastboot = _tools.FastbootPath;
        if (fastboot is null)
        {
            return FlashResult.Fail("找不到 fastboot");
        }

        var code = CredentialStore.Normalize(unlockCode);
        var segments = (int)Math.Ceiling(code.Length / 32.0);

        for (var i = 0; i < segments; i++)
        {
            var start = i * 32;
            var length = Math.Min(32, code.Length - start);
            var segment = code.Substring(start, length);

            progress?.Report(new FlashProgress(
                $"正在下发解锁码片段 {i + 1}/{segments}…",
                (i + 1) * 100.0 / segments));

            // 每段失败不中止：多段协议里个别段重发是正常的，全部发完再判结果。
            await _runner.RunAsync(
                fastboot,
                ["oem", $"unlock:{segment}"],
                timeout: ShortTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // 收尾：有的机型需要显式 flashing unlock 才真正生效。
        var finish = await _runner.RunAsync(
            fastboot, ["flashing", "unlock"],
            timeout: ShortTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        return finish.Combined.Contains("OKAY", StringComparison.OrdinalIgnoreCase) ||
               finish.Combined.Contains("OK", StringComparison.OrdinalIgnoreCase)
            ? FlashResult.Ok("解锁码已全部下发，设备正在解锁")
            : FlashResult.Ok("解锁码已全部下发。请重启到系统确认 BL 状态");
    }

    /// <summary>mzkv 方案：读备份 mzkv → 写解锁版 mzkv → 重启验证。</summary>
    private async Task<FlashResult> UnlockMzkvAsync(
        DeviceProfile profile,
        DeviceCredentials credentials,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!credentials.HasMzkv)
        {
            return FlashResult.Fail(
                "尚未导入解锁用的 mzkv 镜像。请先在「导入授权资料」里导入该机型的 mzkv。");
        }

        var resolvedPort = await ResolvePortAsync(port, cancellationToken).ConfigureAwait(false);
        if (resolvedPort is null)
        {
            return FlashResult.Fail("未检测到 9008 端口");
        }

        var work = CreateWorkDirectory();
        var backup = Path.Combine(work, "mzkv-orig.img");

        progress?.Report(new FlashProgress("正在备份原 mzkv 分区…"));
        var readResult = await ReadPartitionRawAsync(
            "mzkv", backup, resolvedPort, memory, work, progress, cancellationToken).ConfigureAwait(false);
        if (!readResult.Success)
        {
            // 备份失败不中止：参考实现也是"尽力备份"。
            _logger.Warn("mzkv 备份失败，继续写入解锁版");
        }

        progress?.Report(new FlashProgress("正在写入解锁 mzkv…"));
        var writeResult = await FlashViaEdlAsync(
            "mzkv", credentials.MzkvImagePath!, resolvedPort, memory, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!writeResult.Success)
        {
            return writeResult;
        }

        progress?.Report(new FlashProgress("正在写入 misc 清空 data 标记…"));
        await RebootAsync(ConnectionMode.QcEdl, "system", resolvedPort, memory, cancellationToken)
            .ConfigureAwait(false);

        return FlashResult.Ok("已写入解锁 mzkv，设备重启后 BL 应处于解锁状态");
    }

    /// <summary>
    /// ODM-1：备份 frp → 改最后一个字节为 0x01 → 写回 → fastboot flashing unlock。
    /// 参考实现 <c>煤油工具箱.bat:L857-L879</c>。
    /// </summary>
    private async Task<FlashResult> UnlockOdm1Async(
        DeviceProfile profile,
        ConnectionMode mode,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fastboot = _tools.FastbootPath;
        if (fastboot is null)
        {
            return FlashResult.Fail("找不到 fastboot");
        }

        var work = CreateWorkDirectory();
        var frpBackup = Path.Combine(work, "frp-orig.img");

        if (mode == ConnectionMode.QcEdl)
        {
            var resolvedPort = await ResolvePortAsync(port, cancellationToken).ConfigureAwait(false);
            if (resolvedPort is null)
            {
                return FlashResult.Fail("未检测到 9008 端口");
            }

            progress?.Report(new FlashProgress("正在备份 frp 分区…"));
            var read = await ReadPartitionRawAsync(
                "frp", frpBackup, resolvedPort, memory, work, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!read.Success)
            {
                return read;
            }

            // frp 的最后 1 个字节改成 0x01 表示"未完成强制恢复出厂"，从而允许解锁。
            progress?.Report(new FlashProgress("正在修改 frp 标记字节…"));
            var patched = await PatchLastByteAsync(frpBackup, 0x01, cancellationToken)
                .ConfigureAwait(false);
            if (!patched)
            {
                return FlashResult.Fail("修改 frp 分区失败");
            }

            progress?.Report(new FlashProgress("正在写回 frp 分区…"));
            var write = await FlashViaEdlAsync(
                "frp", frpBackup, resolvedPort, memory, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!write.Success)
            {
                return write;
            }

            await RebootAsync(ConnectionMode.QcEdl, "fastboot", resolvedPort, memory, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new FlashProgress("设备正在重启到 Fastboot，请稍候…"));
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        }
        else if (mode != ConnectionMode.Fastboot && mode != ConnectionMode.FastbootD)
        {
            return FlashResult.Fail("ODM-1 解锁需要设备处于 9008 或 Fastboot 模式");
        }

        progress?.Report(new FlashProgress("正在执行 fastboot flashing unlock…"));
        var unlock = await _runner.RunAsync(
            fastboot, ["flashing", "unlock"],
            timeout: ShortTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        return unlock.Combined.Contains("OKAY", StringComparison.OrdinalIgnoreCase)
            ? FlashResult.Ok("已解锁。设备屏幕可能需要确认")
            : FlashResult.Fail($"fastboot 解锁失败：{FirstLine(unlock.Combined)}");
    }

    /// <summary>ODM-2：fastboot oem unlock:&lt;SN&gt;。</summary>
    private async Task<FlashResult> UnlockOdm2Async(
        IProgress<FlashProgress>? progress, CancellationToken cancellationToken)
    {
        var fastboot = _tools.FastbootPath;
        if (fastboot is null)
        {
            return FlashResult.Fail("找不到 fastboot");
        }

        progress?.Report(new FlashProgress("正在读取设备序列号…"));
        var serial = await _runner.RunAsync(
            fastboot, ["getvar", "serialno"],
            timeout: ShortTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        var match = System.Text.RegularExpressions.Regex.Match(
            serial.Combined, @"serialno:\s*(\S+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return FlashResult.Fail("读不到设备序列号，无法执行 oem unlock");
        }

        progress?.Report(new FlashProgress($"正在执行 oem unlock:{match.Groups[1].Value}…"));
        var result = await _runner.RunAsync(
            fastboot, ["oem", $"unlock:{match.Groups[1].Value}"],
            timeout: ShortTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Combined.Contains("OKAY", StringComparison.OrdinalIgnoreCase)
            ? FlashResult.Ok("已解锁")
            : FlashResult.Fail($"oem unlock 失败：{FirstLine(result.Combined)}");
    }

    /// <summary>
    /// ODM-Note8：唯一必须 restore 的流程 —— 备份 aboot/abootbak → 都刷解锁版
    /// → 解锁 → 两个都还原。参考实现 <c>煤油工具箱.bat:L897-L925</c>。
    /// </summary>
    private async Task<FlashResult> UnlockOdmNote8Async(
        DeviceProfile profile,
        DeviceCredentials credentials,
        ConnectionMode mode,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fastboot = _tools.FastbootPath;
        if (fastboot is null)
        {
            return FlashResult.Fail("找不到 fastboot");
        }

        if (!File.Exists(credentials.MzkvImagePath))
        {
            return FlashResult.Fail("Note8 解锁需要 aboot 解锁镜像，请先导入对应资料");
        }

        var work = CreateWorkDirectory();
        var abootBackup = Path.Combine(work, "aboot-orig.img");
        var abootBakBackup = Path.Combine(work, "abootbak-orig.img");

        progress?.Report(new FlashProgress("正在备份 aboot 与 abootbak…"));
        var readResult = await ReadPartitionRawAsync(
            "aboot", abootBackup, port, memory, work, progress, cancellationToken).ConfigureAwait(false);
        if (!readResult.Success)
        {
            return FlashResult.Fail("备份 aboot 失败，为安全起见中止解锁");
        }

        await ReadPartitionRawAsync(
            "abootbak", abootBakBackup, port, memory, work, progress, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new FlashProgress("正在刷入解锁版 aboot…"));
        var flashAboot = await FlashViaEdlAsync(
            "aboot", credentials.MzkvImagePath!, port, memory, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!flashAboot.Success)
        {
            return flashAboot;
        }

        await FlashViaEdlAsync(
            "abootbak", credentials.MzkvImagePath!, port, memory, progress, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new FlashProgress("正在执行 fastboot oem unlock…"));
        await _runner.RunAsync(
            fastboot, ["oem", "unlock"],
            timeout: ShortTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        progress?.Report(new FlashProgress("正在还原 aboot 与 abootbak…"));
        await FlashViaEdlAsync(
            "aboot", abootBackup, port, memory, progress, cancellationToken).ConfigureAwait(false);
        await FlashViaEdlAsync(
            "abootbak", abootBakBackup, port, memory, progress, cancellationToken).ConfigureAwait(false);

        return FlashResult.Ok("Note8 解锁完成，aboot 已还原");
    }

    public async Task<FlashResult> LockBootloaderAsync(
        DeviceProfile profile,
        ConnectionMode mode,
        string? port,
        StorageKind memory,
        IProgress<FlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fastboot = _tools.FastbootPath;
        if (fastboot is null)
        {
            return FlashResult.Fail("找不到 fastboot");
        }

        // 顺序不能反：必须先写 misc 清空标记，再 oem lock，否则会卡在开机验证。
        progress?.Report(new FlashProgress("正在写入 misc 清数据标记…"));
        var miscImage = Path.Combine(
            _tools.ToolsDirectory, "Android", "misc_wipedata.img");

        if (File.Exists(miscImage))
        {
            await FlashPartitionAsync(
                "misc", miscImage, mode, port, memory, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            _logger.Warn($"找不到 {miscImage}，跳过 misc 写入");
        }

        progress?.Report(new FlashProgress("正在执行 fastboot oem lock…"));
        var result = await _runner.RunAsync(
            fastboot, ["oem", "lock"],
            timeout: ShortTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Combined.Contains("OKAY", StringComparison.OrdinalIgnoreCase)
            ? FlashResult.Ok("已上锁")
            : FlashResult.Fail($"上锁失败：{FirstLine(result.Combined)}");
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private async Task<string?> ResolvePortAsync(string? port, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(port))
        {
            var cleaned = port.Trim().TrimStart('C', 'O', 'M', 'c', 'o', 'm');
            return cleaned.Length > 0 ? cleaned : port.Trim();
        }

        var ports = await _firehose.ListPortsAsync(cancellationToken).ConfigureAwait(false);
        return ports.Count == 0 ? null : ports[0];
    }

    /// <summary>
    /// 读分区表。委托给 <see cref="IEdlReader"/>，本类不再自己实现 ——
    /// 原实现这里调 <c>ReadPartitionRawAsync</c>，而后者又回调本方法，
    /// 一旦真的走去读分区表就会无限递归栈溢出。
    /// </summary>
    private Task<IReadOnlyList<GptPartition>> ReadPartitionsAsync(
        string port,
        StorageKind memory,
        string work,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken) =>
        _reader.ReadPartitionsAsync(port, memory, work, progress, cancellationToken);

    /// <summary>按分区名在 9008 下回读原始数据（先用 GPT 定位，再单次下发）。</summary>
    private async Task<FlashResult> ReadPartitionRawAsync(
        string partition,
        string destination,
        string? port,
        StorageKind memory,
        string work,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var resolvedPort = await ResolvePortAsync(port, cancellationToken).ConfigureAwait(false);
        if (resolvedPort is null)
        {
            return FlashResult.Fail("未检测到 9008 端口");
        }

        var gpt = await _reader
            .ReadPartitionsAsync(resolvedPort, memory, work, progress, cancellationToken)
            .ConfigureAwait(false);
        var target = _partitions.Find(gpt, partition);
        if (target is null)
        {
            return FlashResult.Fail($"分区表里找不到 {partition}");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(destination)) ?? work;
        Directory.CreateDirectory(directory);

        return await _reader.ReadPartitionAsync(
            target, destination, resolvedPort, memory, work, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> ReadPrivateAsync(
        string port,
        string work,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        var dest = Path.Combine(work, "private.img");
        var result = await ReadPartitionRawAsync(
            "private", dest, port, StorageKind.Auto, work, progress, cancellationToken)
            .ConfigureAwait(false);
        return result.Success && File.Exists(dest);
    }

    private async Task<bool> PatchHexAsync(
        string imagePath,
        IEnumerable<(long Offset, string Hex)> writes,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);

            foreach (var (offset, hex) in writes)
            {
                var data = Convert.FromHexString(hex);
                if (offset + data.Length > bytes.Length)
                {
                    // 分区比预期小：扩容到足够长度，保持原有数据。
                    Array.Resize(ref bytes, (int)(offset + data.Length));
                }

                Array.Copy(data, 0, bytes, offset, data.Length);
            }

            await File.WriteAllBytesAsync(imagePath, bytes, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("写入解锁标记失败", ex);
            return false;
        }
    }

    private async Task<bool> PatchLastByteAsync(
        string imagePath, byte value, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return false;
            }

            bytes[^1] = value;
            await File.WriteAllBytesAsync(imagePath, bytes, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"修改 {imagePath} 最后一个字节失败", ex);
            return false;
        }
    }

    private async Task<bool> IsSparseImageAsync(string imagePath, CancellationToken cancellationToken)
    {
        var exe = _tools.FindTool("simg_dump.exe");
        if (exe is null)
        {
            // 没有工具时按文件头判断 sparse magic 0xED26FF3A。
            try
            {
                await using var stream = File.OpenRead(imagePath);
                var buffer = new byte[4];
                if (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) < 4)
                {
                    return false;
                }

                return BitConverter.ToUInt32(buffer) == 0xED26FF3A;
            }
            catch
            {
                return false;
            }
        }

        var result = await _runner.RunAsync(
            exe, ["-f", imagePath, "-m", "basicinfo"],
            timeout: ShortTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Success;
    }

    /// <summary>
    /// 设置可启动存储盘：UFS → 1，eMMC → 2。
    /// 参考实现强调"漏了会刷成功但不开机"，这里保留。
    /// </summary>
    private async Task SetBootableStorageDriveAsync(
        string port, StorageKind memory, CancellationToken cancellationToken)
    {
        var exe = _tools.FindTool("fh_loader.exe");
        if (exe is null)
        {
            return;
        }

        // 参考实现 mzbox.bat:EDLSETBOOTABLESTORAGEDRIVE 的真实映射：
        // emmc / spinor → 0；ufs → 按当前槽位 1(a) 或 2(b)。
        // 原来这里写成 "ufs→1, 其余→2" 是错的，会导致 ufs 设备刷完起不来。
        int drive;
        if (memory != StorageKind.Ufs)
        {
            drive = 0;
        }
        else
        {
            var slot = await _reader
                .ReadSlotAsync(port, memory, CreateWorkDirectory(), cancellationToken)
                .ConfigureAwait(false);
            drive = slot == "b" ? 2 : 1;
        }

        var work = CreateWorkDirectory();
        var xmlPath = Path.Combine(work, "setboot.xml");
        var xml = $"""
            <?xml version="1.0" ?>
            <data><setbootablestoragedrive value="{drive}" /></data>
            """;
        await File.WriteAllTextAsync(xmlPath, xml, cancellationToken).ConfigureAwait(false);

        await _runner.RunAsync(
            exe,
            ["--port", $@"\\.\COM{port}", "--memoryname", memory.ToArgument(),
             "--search_path", work, "--sendxml", xmlPath, "--mainoutputdir", work,
             "--skip_configure", "--noprompt"],
            workingDirectory: work, timeout: ShortTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private string DescribeSkipped()
    {
        var skipped = _firehose.LastSkippedFiles;
        return skipped.Count == 0 ? string.Empty : $"（{skipped.Count} 个文件被跳过）";
    }

    private static string CreateWorkDirectory()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "MeiyouToolbox", "edl",
            DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static IProgress<EdlProgress>? ToEdlProgress(IProgress<FlashProgress>? progress) =>
        progress is null
            ? null
            : new Progress<EdlProgress>(p => progress.Report(
                new FlashProgress(p.Message, p.Percent)));

    private static string ToHex(string text) =>
        Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(text));

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? text.Trim();
}
