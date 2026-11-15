using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeiyouToolbox.Models;
using MeiyouToolbox.Services;

namespace MeiyouToolbox.ViewModels;

/// <summary>分区表里的一行。比备份页的行多两样：勾选状态与待写镜像路径。</summary>
public sealed partial class EdlPartitionRow(GptPartition partition) : ObservableObject
{
    public GptPartition Source { get; } = partition;

    public int Lun => Source.Lun;

    public string Name => Source.Name;

    public long StartSector => Source.StartSector;

    public long SectorCount => Source.SectorCount;

    public string SizeText => Source.SizeText;

    /// <summary>要与本分区一起写入的镜像。为空表示这次不写它。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageName), nameof(HasImage))]
    private string? _imagePath;

    public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath);

    public string ImageName =>
        string.IsNullOrWhiteSpace(ImagePath) ? string.Empty : Path.GetFileName(ImagePath);

    /// <summary>是否勾选（用于批量写入）。</summary>
    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 9008 刷写页。
/// <para>
/// 依据紫罗兰工具箱的 EDL 页面结构（文件选择 / 分区表 / 操作按钮 / 扩展功能 / 日志 / 进度），
/// 但按魅族工具箱的做法把「文件选择」压成一个「救砖包位置」：
/// 引导文件、存储类型、串口全部从它推导，用户不必逐个填。
/// </para>
/// <para>
/// 有意没有「BUG 反馈」按钮 —— 用户明确要求去掉。
/// </para>
/// </summary>
public sealed partial class EdlFlashViewModel : ObservableObject
{
    private readonly IFlashService _flash;
    private readonly IEdlReader _reader;
    private readonly ICleanService _clean;
    private readonly IFirehoseService _firehose;
    private readonly IToolLocator _tools;
    private readonly IDeviceProfileCatalog _catalog;
    private readonly ICredentialStore _credentials;
    private readonly IAppLogger _logger;

    private CancellationTokenSource? _cts;

    public EdlFlashViewModel(
        IFlashService flash,
        IEdlReader reader,
        ICleanService clean,
        IFirehoseService firehose,
        IToolLocator tools,
        IDeviceProfileCatalog catalog,
        ICredentialStore credentials,
        IAppLogger logger)
    {
        _flash = flash;
        _reader = reader;
        _clean = clean;
        _firehose = firehose;
        _tools = tools;
        _catalog = catalog;
        _credentials = credentials;
        _logger = logger;

        Partitions = [];
        Ports = [];
        LogLines = [];

        AutoReboot = true;
        SendGuidance = true;
        ProtectBaseband = true;
    }

    /// <summary>设计器 / XAML 内实例化用。</summary>
    public EdlFlashViewModel()
    {
        _flash = null!;
        _reader = null!;
        _clean = null!;
        _firehose = null!;
        _tools = null!;
        _catalog = null!;
        _credentials = null!;
        _logger = null!;

        Partitions = [];
        Ports = [];
        LogLines = [];
    }

    /// <summary>关闭本页时由宿主置为 false。</summary>
    [ObservableProperty]
    private bool _isOpen;

    // ------------------------------------------------------------------
    // 刷写目标
    // ------------------------------------------------------------------

    /// <summary>救砖包位置：目录（含 rawprogram*.xml）或单个镜像。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackageKindText), nameof(HasPackagePath))]
    [NotifyCanExecuteChangedFor(nameof(WriteSelectedCommand), nameof(FlashFullPackageCommand))]
    private string? _packagePath;

    public bool HasPackagePath => !string.IsNullOrWhiteSpace(PackagePath);

    public string PackageKindText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PackagePath))
            {
                return "选择线刷包目录会自动找出引导文件与 rawprogram XML。";
            }

            if (Directory.Exists(PackagePath))
            {
                var xmls = Directory.GetFiles(PackagePath, "rawprogram*.xml", SearchOption.TopDirectoryOnly);
                return xmls.Length > 0
                    ? $"线刷包目录 · {xmls.Length} 个 rawprogram XML"
                    : "目录里没有 rawprogram*.xml，只能按分区表单独写。";
            }

            if (File.Exists(PackagePath))
            {
                var ext = Path.GetExtension(PackagePath).ToLowerInvariant();
                return ext switch
                {
                    ".img" or ".bin" => "单个镜像文件",
                    ".xml" => "XML 文件（需要与镜像同目录）",
                    ".zip" or ".7z" or ".rar" => "压缩包：本工具不解压，请先自行解压。",
                    _ => "未知文件类型",
                };
            }

            return "路径不存在。";
        }
    }

    /// <summary>当前机型（由刷机页带过来，用于推导引导文件与存储类型）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileHint))]
    private DeviceProfile? _profile;

    public string ProfileHint => Profile is { } p
        ? $"{p.Model} · {p.StorageType.ToUpperInvariant()} · 引导文件将自动取自 Tools\\res\\{p.Product}\\devprg"
        : "未选择机型：引导文件需要手动指定。";

    /// <summary>引导文件。留空则按机型自动找。</summary>
    [ObservableProperty]
    private string? _programmerPath;

    // ------------------------------------------------------------------
    // 串口与存储
    // ------------------------------------------------------------------

    public ObservableCollection<string> Ports { get; }

    [ObservableProperty]
    private string? _selectedPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StorageText))]
    private StorageKind _storage = StorageKind.Auto;

    public string StorageText => Storage == StorageKind.Auto
        ? "自动识别（UFS → eMMC → SPI）"
        : Storage.Display();

    // ------------------------------------------------------------------
    // 选项
    // ------------------------------------------------------------------

    [ObservableProperty] private bool _autoReboot;
    [ObservableProperty] private bool _autoFormat;
    [ObservableProperty] private bool _sendGuidance;
    [ObservableProperty] private bool _protectBaseband;
    [ObservableProperty] private bool _protectData;

    // ------------------------------------------------------------------
    // 分区表
    // ------------------------------------------------------------------

    public ObservableCollection<EdlPartitionRow> Partitions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPartitionText), nameof(HasSelectedPartition))]
    [NotifyCanExecuteChangedFor(nameof(ReadSelectedCommand), nameof(EraseSelectedCommand))]
    private EdlPartitionRow? _selectedPartition;

    public bool HasSelectedPartition => SelectedPartition is not null;

    public string SelectedPartitionText => SelectedPartition is { } row
        ? $"{row.Name} · LUN{row.Lun} · {row.SizeText}"
        : "未选中分区";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartitions))]
    private string _partitionStatus = "尚未读取分区表。9008 模式下点「读分区表」。";

    public bool HasPartitions => Partitions.Count > 0;

    // ------------------------------------------------------------------
    // 任务状态
    // ------------------------------------------------------------------

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _taskTitle = "空闲";
    [ObservableProperty] private string _taskMessage = "等待操作。";
    [ObservableProperty] private double _taskPercent;
    [ObservableProperty] private bool _taskIndeterminate = true;
    [ObservableProperty] private bool _taskFailed;

    public ObservableCollection<string> LogLines { get; }

    // ------------------------------------------------------------------
    // 命令
    // ------------------------------------------------------------------

    /// <summary>打开页面时刷新端口。</summary>
    public async Task OpenAsync(DeviceProfile? profile)
    {
        Profile = profile;
        IsOpen = true;
        await RefreshPortsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void Close()
    {
        if (IsBusy)
        {
            Append("任务进行中，先取消再关闭。");
            return;
        }

        IsOpen = false;
    }

    [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        try
        {
            var ports = await _firehose.ListPortsAsync().ConfigureAwait(true);

            await UiAsync(() =>
            {
                Ports.Clear();
                foreach (var port in ports)
                {
                    Ports.Add(port);
                }

                if (SelectedPort is null && Ports.Count > 0)
                {
                    SelectedPort = Ports[0];
                }

                Append(Ports.Count == 0
                    ? "未检测到 9008 设备。请确认手机已进入 9008（音量上下同时按住再插线）。"
                    : $"检测到 {Ports.Count} 个 9008 端口：{string.Join(", ", Ports)}");
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Warn($"枚举 9008 端口失败：{ex.Message}");
            Append("枚举端口失败：" + ex.Message);
        }
    }

    private bool CanReadPartitions() => !IsBusy;

    /// <summary>读分区表。需要设备已在 9008 且引导已发送。</summary>
    [RelayCommand(CanExecute = nameof(CanReadPartitions))]
    private async Task ReadPartitionsAsync()
    {
        var programmer = ResolveProgrammer();
        if (programmer is null)
        {
            Append("找不到 9008 引导文件，无法建立会话。");
            return;
        }

        await RunAsync("读取分区表", async token =>
        {
            var port = await ResolvePortAsync(token).ConfigureAwait(true);
            if (port is null)
            {
                return FlashResult.Fail("未检测到 9008 端口");
            }

            var sent = await _flash.SendFirehoseAsync(
                port, programmer, Storage, AuthCodeForProfile(), CreateProgress(), token)
                .ConfigureAwait(true);

            if (!sent.Success)
            {
                return sent;
            }

            var work = CreateWorkDirectory();
            var partitions = await _reader
                .ReadPartitionsAsync(port, Storage, work, CreateProgress(), token)
                .ConfigureAwait(true);

            await UiAsync(() =>
            {
                Partitions.Clear();
                foreach (var p in partitions)
                {
                    Partitions.Add(new EdlPartitionRow(p));
                }

                PartitionStatus = Partitions.Count == 0
                    ? "没有读到分区。设备可能不在 9008，或引导文件与机型不匹配。"
                    : $"共 {Partitions.Count} 个分区。勾选后点「写入选中分区」，或双击行选镜像。";

                OnPropertyChanged(nameof(HasPartitions));
            }).ConfigureAwait(true);

            return partitions.Count == 0
                ? FlashResult.Fail("没读到分区")
                : FlashResult.Ok($"已读取 {partitions.Count} 个分区");
        }).ConfigureAwait(true);
    }

    private bool CanWriteSelected() => !IsBusy && HasPackagePath;

    /// <summary>把工程里的整包 rawprogram*.xml 全部刷入。</summary>
    [RelayCommand(CanExecute = nameof(CanWriteSelected))]
    private async Task FlashFullPackageAsync()
    {
        var path = PackagePath?.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Append("请选择一个线刷包目录");
            return;
        }

        var programmer = ResolveProgrammer();
        if (programmer is null)
        {
            Append("找不到 9008 引导文件，无法线刷。");
            return;
        }

        await RunAsync("9008 线刷整包", async token =>
        {
            var port = await ResolvePortAsync(token).ConfigureAwait(true);
            if (port is null)
            {
                return FlashResult.Fail("未检测到 9008 端口");
            }

            var result = await _flash.FlashRawPackageAsync(
                path, port, Storage, programmer, AuthCodeForProfile(),
                CreateProgress(), token).ConfigureAwait(true);

            if (result.Success && AutoFormat)
            {
                Append("按选项执行自动格式化 userdata…");
                await _clean.ErasePartitionAsync("userdata", port, Storage, CreateProgress(), token)
                    .ConfigureAwait(true);
            }

            return result;
        }).ConfigureAwait(true);
    }

    /// <summary>写入勾选的分区（每行要有镜像路径）。</summary>
    [RelayCommand]
    private async Task WriteSelectedAsync()
    {
        var targets = Partitions.Where(p => p.HasImage).ToList();
        if (targets.Count == 0)
        {
            Append("没有可写入的分区：先给行指定镜像（选中行后点「选择镜像」）。");
            return;
        }

        await RunAsync($"写入 {targets.Count} 个分区", async token =>
        {
            var failed = new List<string>();

            for (var i = 0; i < targets.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    return FlashResult.Fail("已取消");
                }

                var row = targets[i];
                if (ProtectBaseband && IsBasebandPartition(row.Name))
                {
                    Append($"跳过分区 {row.Name}：已开启基带保护。");
                    continue;
                }

                Report($"[{i + 1}/{targets.Count}] 写入 {row.Name}（LUN{row.Lun}）…");

                var one = await _flash.FlashPartitionAsync(
                    row.Name, row.ImagePath!, ConnectionMode.QcEdl,
                    SelectedPort, Storage, CreateProgress(), token).ConfigureAwait(true);

                if (!one.Success)
                {
                    failed.Add($"{row.Name}（{one.Message}）");
                }
            }

            return failed.Count == 0
                ? FlashResult.Ok($"已写入 {targets.Count} 个分区")
                : FlashResult.Fail("以下分区写入失败：" + string.Join("；", failed));
        }).ConfigureAwait(true);
    }

    private bool CanReadSelected() => !IsBusy && HasSelectedPartition;

    /// <summary>回读选中分区到 backups 目录。</summary>
    [RelayCommand(CanExecute = nameof(CanReadSelected))]
    private async Task ReadSelectedAsync()
    {
        if (SelectedPartition is not { } row)
        {
            return;
        }

        await RunAsync($"回读 {row.Name}", async token =>
        {
            var port = await ResolvePortAsync(token).ConfigureAwait(true);
            if (port is null)
            {
                return FlashResult.Fail("未检测到 9008 端口");
            }

            var dir = Path.Combine(_tools.AppDirectory, "backups");
            Directory.CreateDirectory(dir);
            var destination = Path.Combine(dir, $"{row.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.img");

            return await _reader.ReadPartitionAsync(
                row.Source, destination, port, Storage, CreateWorkDirectory(),
                CreateProgress(), token).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private bool CanEraseSelected() => !IsBusy && HasSelectedPartition;

    /// <summary>擦除选中分区。自动格式化用的也是这条路径。</summary>
    [RelayCommand(CanExecute = nameof(CanEraseSelected))]
    private async Task EraseSelectedAsync()
    {
        if (SelectedPartition is not { } row)
        {
            return;
        }

        if (ProtectData && IsDataPartition(row.Name))
        {
            Append($"{row.Name} 属于用户数据分区，已开启数据保护，拒绝擦除。");
            return;
        }

        await RunAsync($"擦除 {row.Name}", async token =>
        {
            var port = await ResolvePortAsync(token).ConfigureAwait(true);
            if (port is null)
            {
                return FlashResult.Fail("未检测到 9008 端口");
            }

            return await _clean.ErasePartitionAsync(
                row.Name, port, Storage, CreateProgress(), token).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>重启。参数是目标模式：system / qcedl / recovery / fastbootd。</summary>
    [RelayCommand]
    private async Task RebootToAsync(string? target)
    {
        var mode = string.IsNullOrWhiteSpace(target) ? "system" : target;

        await RunAsync($"重启到 {mode}", async token =>
        {
            return await _flash.RebootAsync(
                ConnectionMode.QcEdl, mode, SelectedPort, Storage, token).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>给选中行挑一个镜像（由视图调用，因为要用文件选择器）。</summary>
    public void AttachImageToSelected(string imagePath)
    {
        if (SelectedPartition is not { } row)
        {
            Append("先选中一行再选镜像。");
            return;
        }

        row.ImagePath = imagePath;
        Append($"已为 {row.Name} 指定镜像：{Path.GetFileName(imagePath)}");
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        Append("已请求取消…");
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    private string? ResolveProgrammer()
    {
        if (!string.IsNullOrWhiteSpace(ProgrammerPath) && File.Exists(ProgrammerPath))
        {
            return ProgrammerPath;
        }

        var found = _tools.FindProgrammer(Profile?.Product);
        if (found is not null)
        {
            ProgrammerPath = found;
        }

        return found;
    }

    private string? AuthCodeForProfile()
    {
        if (Profile is not { } profile)
        {
            return null;
        }

        var credentials = _credentials.Load(profile.Product);
        return credentials.HasAuthCode ? credentials.AuthCode : null;
    }

    private async Task<string?> ResolvePortAsync(CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(SelectedPort))
        {
            return SelectedPort;
        }

        var ports = await _firehose.ListPortsAsync(token).ConfigureAwait(true);
        if (ports.Count > 0)
        {
            SelectedPort = ports[0];
            return SelectedPort;
        }

        return null;
    }

    private async Task RunAsync(string title, Func<CancellationToken, Task<FlashResult>> action)
    {
        if (IsBusy)
        {
            return;
        }

        _cts = new CancellationTokenSource();

        await UiAsync(() =>
        {
            IsBusy = true;
            TaskTitle = title;
            TaskMessage = "正在准备…";
            TaskPercent = 0;
            TaskIndeterminate = true;
            TaskFailed = false;
            LogLines.Clear();
        }).ConfigureAwait(true);

        try
        {
            var result = await action(_cts.Token).ConfigureAwait(true);

            await UiAsync(() =>
            {
                TaskMessage = result.Message;
                TaskFailed = !result.Success;
                TaskIndeterminate = false;
                if (result.Success)
                {
                    TaskPercent = 100;
                }

                Append(result.Success ? $"✓ {result.Message}" : $"✗ {result.Message}");
            }).ConfigureAwait(true);

            _logger.Info($"EDL {title}: {(result.Success ? "成功" : "失败")} — {result.Message}");
        }
        catch (OperationCanceledException)
        {
            await UiAsync(() =>
            {
                TaskMessage = "已取消";
                TaskFailed = true;
                TaskIndeterminate = false;
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error($"EDL {title}: 未处理异常", ex);
            await UiAsync(() =>
            {
                TaskMessage = "发生错误：" + ex.Message;
                TaskFailed = true;
                TaskIndeterminate = false;
                Append("✗ " + ex.Message);
            }).ConfigureAwait(true);
        }
        finally
        {
            await UiAsync(() => IsBusy = false).ConfigureAwait(true);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private IProgress<FlashProgress> CreateProgress() =>
        new Progress<FlashProgress>(p => Post(() =>
        {
            TaskMessage = p.Message;
            if (!p.IsIndeterminate)
            {
                TaskPercent = p.Percent;
                TaskIndeterminate = false;
            }

            Append(p.Message);
        }));

    private void Report(string message) => Post(() =>
    {
        TaskMessage = message;
        Append(message);
    });

    private void Append(string line) => Post(() =>
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (LogLines.Count > 500)
        {
            LogLines.RemoveAt(0);
        }
    });

    /// <summary>所有界面更新都必须回 UI 线程，否则会抛 0xe0434352。</summary>
    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private static Task UiAsync(Action action)
    {
        var tcs = new TaskCompletionSource();

        Post(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return tcs.Task;
    }

    private static string CreateWorkDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MeiyouToolbox", "edl", DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool IsBasebandPartition(string name) =>
        name.ToLowerInvariant() is "persist" or "modemst1" or "modemst2" or "fsg" or "fsc";

    private static bool IsDataPartition(string name) =>
        name.ToLowerInvariant() is "userdata" or "metadata";
}
