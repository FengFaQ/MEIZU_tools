using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeiyouToolbox.Models;
using MeiyouToolbox.Services;

namespace MeiyouToolbox.ViewModels;

/// <summary>要刷进设备的一个镜像（分区名 + 文件路径）。</summary>
public sealed partial class FastbootImageRow : ObservableObject
{
    public FastbootImageRow(string partition, string path)
    {
        Partition = partition;
        Path = path;
    }

    /// <summary>目标分区名，例如 boot、system、vendor。</summary>
    public string Partition { get; }

    /// <summary>本地镜像文件完整路径。</summary>
    public string Path { get; }

    /// <summary>文件名，列表里显示用。</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>文件体积。</summary>
    public string SizeText
    {
        get
        {
            try
            {
                return PayloadPartition.FormatSize(new FileInfo(Path).Length);
            }
            catch
            {
                return "---";
            }
        }
    }

    /// <summary>是否勾选刷入。默认全选 —— 解包出来的镜像就是要刷的。</summary>
    [ObservableProperty]
    private bool _isSelected = true;
}

/// <summary>
/// Fastboot 刷写页的视图模型。
/// <para>
/// 界面只有一个输入框：既可以选 payload.bin（选完点「解包 payload」解到某个目录），
/// 也可以直接选已解包好的文件夹。两条路最后都汇聚到同一个镜像列表，
/// 再统一走 fastboot 刷入。
/// </para>
/// </summary>
public sealed partial class FastbootFlashViewModel : ObservableObject
{
    private readonly IPayloadService _payload;
    private readonly IFlashService _flash;
    private readonly IDeviceService _devices;
    private readonly IDeviceModeService _modes;
    private readonly IToolLocator _tools;
    private readonly IAppLogger _logger;

    private CancellationTokenSource? _cts;

    public FastbootFlashViewModel(
        IPayloadService payload,
        IFlashService flash,
        IDeviceService devices,
        IDeviceModeService modes,
        IToolLocator tools,
        IAppLogger logger)
    {
        _payload = payload;
        _flash = flash;
        _devices = devices;
        _modes = modes;
        _tools = tools;
        _logger = logger;
        AutoReboot = true;
        VerifySize = true;
    }

    /// <summary>设计器/回退用无参构造。</summary>
    public FastbootFlashViewModel()
    {
        _payload = null!;
        _flash = null!;
        _devices = null!;
        _modes = null!;
        _tools = null!;
        _logger = null!;
    }

    /// <summary>页面是否可见。</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>
    /// 唯一输入框：payload.bin、OTA 压缩包，或已解包好的文件夹。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceHint), nameof(HasSource), nameof(IsPayload))]
    [NotifyCanExecuteChangedFor(nameof(InspectPayloadCommand), nameof(UnpackPayloadCommand), nameof(LoadImagesCommand))]
    private string? _sourcePath;

    /// <summary>输入框下方那行小字，说明当前选的是什么。</summary>
    public string SourceHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SourcePath))
            {
                return "选择 payload.bin 或已解包的文件夹";
            }

            if (Directory.Exists(SourcePath))
            {
                var count = SafeEnumerateImages(SourcePath).Count;
                return count > 0
                    ? $"已解包文件夹 · {count} 个镜像"
                    : "文件夹里没有找到镜像（.img / .bin）";
            }

            if (!File.Exists(SourcePath))
            {
                return "路径不存在";
            }

            var name = System.IO.Path.GetFileName(SourcePath);
            return IsPayload
                ? $"{name} · 可点「解包 payload」解出镜像"
                : $"{name} · 单个镜像文件";
        }
    }

    /// <summary>当前输入框指的是不是 payload（含嵌套在 OTA 压缩包里的情况）。</summary>
    public bool IsPayload
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SourcePath))
            {
                return false;
            }

            var extension = System.IO.Path.GetExtension(SourcePath);
            return string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool HasSource => !string.IsNullOrWhiteSpace(SourcePath);

    /// <summary>解包输出的根目录；留空则用输入文件同级的 images 目录。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutputDirectory))]
    private string? _outputDirectory;

    public bool HasOutputDirectory => !string.IsNullOrWhiteSpace(OutputDirectory);

    /// <summary>解包是否校验解出字节数。</summary>
    [ObservableProperty]
    private bool _verifySize;

    /// <summary>刷完之后是否重启到系统。</summary>
    [ObservableProperty]
    private bool _autoReboot;

    /// <summary>镜像列表。</summary>
    public ObservableCollection<FastbootImageRow> Images { get; } = [];

    /// <summary>列表状态文案。</summary>
    [ObservableProperty]
    private string _imageStatus = "还没有载入镜像";

    public bool HasImages => Images.Count > 0;

    /// <summary>payload 里的分区摘要，解包前先展示给用户看。</summary>
    public ObservableCollection<PayloadPartition> PayloadPartitions { get; } = [];

    [ObservableProperty]
    private string _payloadStatus = "选择 payload 后可先查看里面的分区";

    public bool HasPayloadInfo => PayloadPartitions.Count > 0;

    /// <summary>当前设备状态，用于提示用户是否需要先切到 fastboot。</summary>
    [ObservableProperty]
    private string _deviceText = "未检测";

    /// <summary>任务状态。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    private string _taskTitle = string.Empty;

    [ObservableProperty]
    private string _taskMessage = string.Empty;

    [ObservableProperty]
    private double _taskPercent;

    [ObservableProperty]
    private bool _taskIndeterminate = true;

    [ObservableProperty]
    private bool _taskFailed;

    /// <summary>日志行。</summary>
    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>打开页面：复位状态并探测一次设备。</summary>
    public async Task OpenAsync()
    {
        IsOpen = true;
        await RefreshDeviceAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void Close()
    {
        if (IsBusy)
        {
            return;
        }

        IsOpen = false;
    }

    /// <summary>读取 payload 的分区清单（不解包）。</summary>
    [RelayCommand(CanExecute = nameof(CanInspect))]
    private async Task InspectPayloadAsync()
    {
        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            return;
        }

        await RunAsync("读取 payload", async token =>
        {
            var descriptor = await _payload.InspectAsync(SourcePath!, token).ConfigureAwait(true);

            Post(() =>
            {
                PayloadPartitions.Clear();
                foreach (var partition in descriptor.Partitions)
                {
                    PayloadPartitions.Add(partition);
                }

                PayloadStatus = descriptor.Summary;
                OnPropertyChanged(nameof(HasPayloadInfo));
            });

            Report(descriptor.Summary);
            return descriptor.IsIncremental
                ? FlashResult.Fail("这是差分包，不支持解包。请换完整包。")
                : FlashResult.Ok($"读到 {descriptor.Partitions.Count} 个分区");
        }).ConfigureAwait(true);
    }

    private bool CanInspect() => !IsBusy && IsPayload && HasSource;

    /// <summary>解包 payload 到指定目录，完成后自动载入镜像列表。</summary>
    [RelayCommand(CanExecute = nameof(CanUnpack))]
    private async Task UnpackPayloadAsync()
    {
        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            return;
        }

        var output = ResolveOutputDirectory();

        await RunAsync("解包 payload", async token =>
        {
            var progress = new Progress<ExtractProgress>(p =>
            {
                Post(() =>
                {
                    TaskMessage = p.Text;
                    TaskPercent = p.Fraction * 100;
                    TaskIndeterminate = false;
                });
            });

            var result = await _payload
                .ExtractAsync(SourcePath!, new ExtractRequest(output, null, VerifySize), progress, Report, token)
                .ConfigureAwait(true);

            Post(() => OutputDirectory = output);
            PopulateImages(output);
            Report(result.Summary);

            return result.ExtractedPartitions.Count > 0
                ? FlashResult.Ok(result.Summary)
                : FlashResult.Fail("没有解出任何分区");
        }).ConfigureAwait(true);
    }

    private bool CanUnpack() => !IsBusy && IsPayload && HasSource;

    /// <summary>载入镜像：输入框是文件夹就直接读，是单个镜像就只放一条。</summary>
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private void LoadImages()
    {
        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            return;
        }

        PopulateImages(Directory.Exists(SourcePath) ? SourcePath : null,
            File.Exists(SourcePath) ? SourcePath : null);
    }

    private bool CanLoad() => !IsBusy && HasSource;

    /// <summary>开始刷入列表里勾选的镜像。</summary>
    [RelayCommand(CanExecute = nameof(CanFlash))]
    private async Task FlashAsync()
    {
        var selected = Images.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Post(() => TaskMessage = "没有勾选任何镜像");
            return;
        }

        await RunAsync("Fastboot 刷写", async token =>
        {
            var navigation = await _modes.EnsureModeAsync(ConnectionMode.Fastboot,
                progress: CreateProgress(), cancellationToken: token).ConfigureAwait(true);

            if (!navigation.Success)
            {
                return FlashResult.Fail(navigation.Message);
            }

            Post(() => DeviceText = "Fastboot");

            var failures = new List<string>();
            for (var index = 0; index < selected.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var image = selected[index];

                Post(() =>
                {
                    TaskMessage = $"写入 {image.Partition}（{index + 1}/{selected.Count}）";
                    TaskPercent = selected.Count == 0 ? 0 : index * 100.0 / selected.Count;
                    TaskIndeterminate = false;
                });

                var result = await _flash.FlashPartitionAsync(
                    image.Partition, image.Path, ConnectionMode.Fastboot, null,
                    StorageKind.Auto, CreateProgress(), token).ConfigureAwait(true);

                Report($"{image.Partition}: {result.Message}");
                if (!result.Success)
                {
                    failures.Add(image.Partition);
                }
            }

            if (failures.Count > 0)
            {
                return FlashResult.Fail($"失败 {failures.Count} 个：{string.Join("、", failures)}");
            }

            if (AutoReboot)
            {
                Report("刷写完成，重启到系统");
                await _flash.RebootAsync(ConnectionMode.Fastboot, "system", null, StorageKind.Auto, token)
                    .ConfigureAwait(true);
            }

            return FlashResult.Ok($"已刷入 {selected.Count} 个分区");
        }).ConfigureAwait(true);
    }

    private bool CanFlash() => !IsBusy && Images.Any(i => i.IsSelected);

    [RelayCommand]
    private async Task RefreshDeviceAsync()
    {
        try
        {
            var info = await _devices.ProbeAsync().ConfigureAwait(true);
            Post(() => DeviceText = info.Mode switch
            {
                ConnectionMode.None => "未连接",
                ConnectionMode.System => "系统（ADB）",
                ConnectionMode.Recovery => "Recovery",
                ConnectionMode.Sideload => "Sideload",
                ConnectionMode.Fastboot => "Fastboot",
                ConnectionMode.FastbootD => "FastbootD",
                ConnectionMode.QcEdl => "9008（EDL）",
                ConnectionMode.QcDiag => "诊断模式",
                _ => "未知",
            });
        }
        catch (Exception ex)
        {
            _logger.Warn("探测设备失败", ex);
            Post(() => DeviceText = "未检测");
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    /// <summary>供视图在用户选完路径后调用：设路径并自动载入镜像。</summary>
    public void SetSource(string path)
    {
        SourcePath = path;
        OutputDirectory = null;
        PayloadPartitions.Clear();
        PayloadStatus = "选择 payload 后可先查看里面的分区";
        OnPropertyChanged(nameof(HasPayloadInfo));
        LoadImages();
    }

    private void PopulateImages(string? folder, string? singleFile = null)
    {
        var rows = new List<FastbootImageRow>();

        if (folder is not null)
        {
            rows.AddRange(SafeEnumerateImages(folder).Select(pair => new FastbootImageRow(pair.Partition, pair.Path)));
        }
        else if (singleFile is not null)
        {
            rows.Add(new FastbootImageRow(InferPartition(singleFile), singleFile));
        }

        Post(() =>
        {
            Images.Clear();
            foreach (var row in rows)
            {
                Images.Add(row);
            }

            ImageStatus = rows.Count == 0
                ? "没有找到镜像"
                : $"共 {rows.Count} 个镜像，已全部勾选";
            OnPropertyChanged(nameof(HasImages));
            FlashCommand.NotifyCanExecuteChanged();
        });
    }

    /// <summary>
    /// 从目录里找镜像，按文件名推断分区名。
    /// 只认 .img/.bin —— 解包产物是平铺的 &lt;分区名&gt;.img，文件名就是分区名。
    /// </summary>
    private static List<(string Partition, string Path)> SafeEnumerateImages(string folder)
    {
        var results = new List<(string, string)>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                var extension = System.IO.Path.GetExtension(path);
                if (!string.Equals(extension, ".img", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add((InferPartition(path), path));
            }
        }
        catch
        {
            // 目录读不到就当空，由界面提示用户换目录。
        }

        return results.OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 文件名转分区名：去掉扩展名，再去掉 A/B 槽位后缀（system_a → system）。
    /// fastboot 会自动按当前槽位选择实际分区，所以不需要带后缀。
    /// </summary>
    private static string InferPartition(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }

        var match = Regex.Match(name, @"^(.+?)[-_](?:a|b)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : name;
    }

    private string ResolveOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(OutputDirectory))
        {
            return OutputDirectory!;
        }

        var source = SourcePath!;
        var parent = Directory.Exists(source)
            ? Directory.GetParent(source)?.FullName ?? source
            : System.IO.Path.GetDirectoryName(source) ?? _tools.AppDirectory;

        return System.IO.Path.Combine(parent, "images");
    }

    private async Task RunAsync(string title, Func<CancellationToken, Task<FlashResult>> action)
    {
        if (IsBusy)
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Post(() =>
        {
            IsBusy = true;
            TaskTitle = title;
            TaskMessage = "正在准备…";
            TaskPercent = 0;
            TaskIndeterminate = true;
            TaskFailed = false;
            ImageStatus = title;
        });

        FlashCommand.NotifyCanExecuteChanged();
        InspectPayloadCommand.NotifyCanExecuteChanged();
        UnpackPayloadCommand.NotifyCanExecuteChanged();
        LoadImagesCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        FlashResult result;
        try
        {
            result = await action(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            result = FlashResult.Fail("已取消");
        }
        catch (Exception ex)
        {
            _logger.Error($"{title} 失败", ex);
            result = FlashResult.Fail(ex.Message);
        }

        Post(() =>
        {
            IsBusy = false;
            TaskFailed = !result.Success;
            TaskMessage = result.Message;
            TaskPercent = result.Success ? 100 : TaskPercent;
            TaskIndeterminate = false;
            ImageStatus = result.Success ? "完成" : "失败";
        });

        Report((result.Success ? "✓ " : "✗ ") + result.Message);

        FlashCommand.NotifyCanExecuteChanged();
        InspectPayloadCommand.NotifyCanExecuteChanged();
        UnpackPayloadCommand.NotifyCanExecuteChanged();
        LoadImagesCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private IProgress<FlashProgress> CreateProgress() =>
        new Progress<FlashProgress>(p => Post(() =>
        {
            TaskMessage = p.Message;
            if (p.IsIndeterminate)
            {
                TaskIndeterminate = true;
            }
            else
            {
                TaskIndeterminate = false;
                TaskPercent = p.Percent;
            }
        }));

    private void Report(string line)
    {
        _logger.Info(line.TrimStart('✓', '✗', ' '));
        Post(() =>
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            LogLines.Add($"{stamp}  {line}");
            while (LogLines.Count > 500)
            {
                LogLines.RemoveAt(0);
            }
        });
    }

    private static void Post(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
    }
}
