using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeiyouToolbox.Models;
using MeiyouToolbox.Services;

namespace MeiyouToolbox.ViewModels;

/// <summary>格式化文件系统在界面上的一个选项。</summary>
public sealed record FileSystemOption(FileSystemKind Kind, string Title)
{
    public override string ToString() => Title;
}

/// <summary>槽位下拉项。</summary>
public sealed record SlotChoice(string Value, string Display)
{
    public override string ToString() => Display;
}

/// <summary>管理页的分区行。与备份页的 <c>PartitionRow</c> 分开，避免两页互相牵制。</summary>
public sealed class ManagePartitionRow(GptPartition partition)
{
    public GptPartition Source { get; } = partition;

    public int Lun => Source.Lun;
    public string Name => Source.Name;
    public long StartSector => Source.StartSector;
    public long SectorCount => Source.SectorCount;
    public long SizeBytes => Source.SizeBytes;
    public string SizeText => Source.SizeText;
}

/// <summary>
/// 管理页。槽位查看与切换、分区表读取、擦除分区、格式化分区。
/// <para>
/// 对应参考实现主菜单的 <c>11.刷入任意分区</c>（写入侧在刷机页）、
/// <c>15.查看,设置槽位</c>，以及 <c>bin\clean.bat</c> 的擦除与格式化。
/// </para>
/// <para>
/// <b>为什么把这几个功能放一页：</b>它们都是"对设备结构做手术"而不是"刷个东西进去"，
/// 共同前提是必须先读出分区表、弄清当前槽位，否则很容易动错分区。
/// 所以本页要求先"读取分区表"，之后才允许对具体分区下手。
/// </para>
/// </summary>
public sealed partial class ManageViewModel : ObservableObject
{
    private readonly ISlotService _slots;
    private readonly ICleanService _clean;
    private readonly IDeviceService _devices;
    private readonly IEdlReader _reader;
    private readonly IDeviceProfileCatalog _catalog;
    private readonly IAppLogger _logger;

    private CancellationTokenSource? _cts;

    public ManageViewModel(
        ISlotService slots,
        ICleanService clean,
        IDeviceService devices,
        IEdlReader reader,
        IDeviceProfileCatalog catalog,
        IAppLogger logger)
    {
        _slots = slots;
        _clean = clean;
        _devices = devices;
        _reader = reader;
        _catalog = catalog;
        _logger = logger;

        Partitions = [];
        LogLines = [];
        FileSystems =
        [
            new(FileSystemKind.Fat32, "FAT32"),
            new(FileSystemKind.Ext4, "EXT4"),
            new(FileSystemKind.ExFat, "exFAT"),
            new(FileSystemKind.Ntfs, "NTFS"),
        ];
        _selectedFileSystem = FileSystems[0];
        Slot = SlotInfo.Unknown(ConnectionMode.None);
    }

    /// <summary>XAML 内实例化用。</summary>
    public ManageViewModel()
    {
        _slots = null!;
        _clean = null!;
        _devices = null!;
        _reader = null!;
        _catalog = null!;
        _logger = null!;

        Partitions = [];
        LogLines = [];
        FileSystems = [];
        Slot = SlotInfo.Unknown(ConnectionMode.None);
    }

    // ------------------------------------------------------------------
    // 连接与机型
    // ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsConnected),
        nameof(ConnectionText),
        nameof(IsEdlConnected),
        nameof(IsAdbMode),
        nameof(CanReadPartitions),
        nameof(CanSwitchSlot),
        nameof(CanErase),
        nameof(CanFormat))]
    [NotifyCanExecuteChangedFor(
        nameof(ReadPartitionsCommand),
        nameof(ReadSlotCommand),
        nameof(SwitchSlotCommand),
        nameof(ErasePartitionCommand),
        nameof(FormatPartitionCommand))]
    private ConnectionMode _connection = ConnectionMode.None;

    public bool IsConnected => Connection != ConnectionMode.None;

    public bool IsEdlConnected => Connection == ConnectionMode.QcEdl;

    /// <summary>格式化走 adb shell 里的 mkfs 工具，只有系统 / Recovery 模式能用。</summary>
    public bool IsAdbMode => Connection is ConnectionMode.System or ConnectionMode.Recovery;

    public string ConnectionText => Connection.Display();

    public bool CanReadPartitions => !IsBusy && IsConnected;

    public bool CanSwitchSlot => !IsBusy && IsConnected && Slot.HasSlot;

    public bool CanErase =>
        !IsBusy && DangerConfirmed && IsEdlConnected && SelectedPartition is not null;

    public bool CanFormat =>
        !IsBusy && DangerConfirmed && IsAdbMode && SelectedPartition is not null;

    public ObservableCollection<DeviceProfile> Profiles { get; } = [];

    [ObservableProperty]
    private DeviceProfile? _selectedProfile;

    /// <summary>9008 端口，留空则自动取第一个。</summary>
    [ObservableProperty]
    private string? _edlPort;

    // ------------------------------------------------------------------
    // 槽位
    // ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(SlotDisplay),
        nameof(HasSlot),
        nameof(SlotWarning),
        nameof(HasSlotWarning),
        nameof(CanSwitchSlot))]
    [NotifyCanExecuteChangedFor(nameof(SwitchSlotCommand))]
    private SlotInfo _slot;

    public string SlotDisplay => Slot.Display;

    public bool HasSlot => Slot.HasSlot;

    /// <summary>切换前的提示：目标已是当前槽位，或目标被标记为不可启动。</summary>
    public string SlotWarning
    {
        get
        {
            if (!Slot.HasSlot || SelectedSlotTarget is not { } target)
            {
                return string.Empty;
            }

            if (target.Value == Slot.Current)
            {
                return $"槽位 {target.Value.ToUpperInvariant()} 已经是当前活动槽位";
            }

            return Slot.IsUnbootable(target.Value)
                ? $"警告：槽位 {target.Value.ToUpperInvariant()} 被标记为不可启动，切过去可能无法开机"
                : string.Empty;
        }
    }

    public bool HasSlotWarning => SlotWarning.Length > 0;

    public IReadOnlyList<SlotChoice> SlotChoices { get; } =
    [
        new("a", "槽位 A"),
        new("b", "槽位 B"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlotWarning), nameof(HasSlotWarning))]
    private SlotChoice? _selectedSlotTarget;

    // ------------------------------------------------------------------
    // 分区
    // ------------------------------------------------------------------

    public ObservableCollection<ManagePartitionRow> Partitions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(CanErase), nameof(CanFormat), nameof(SelectedPartitionText))]
    [NotifyCanExecuteChangedFor(
        nameof(ErasePartitionCommand), nameof(FormatPartitionCommand))]
    private ManagePartitionRow? _selectedPartition;

    public string SelectedPartitionText => SelectedPartition is { } p
        ? $"{p.Name} · LUN {p.Lun} · {p.SizeText}"
        : "尚未选择分区";

    [ObservableProperty]
    private string _partitionStatus = "尚未读取分区表。9008 模式下点「读取分区表」，需要先发送引导。";

    public bool HasPartitions => Partitions.Count > 0;

    // ------------------------------------------------------------------
    // 格式化
    // ------------------------------------------------------------------

    public ObservableCollection<FileSystemOption> FileSystems { get; }

    [ObservableProperty]
    private FileSystemOption? _selectedFileSystem;

    [ObservableProperty]
    private string? _volumeLabel;

    /// <summary>擦除 / 格式化的二次确认。危险操作必须打勾。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanErase), nameof(CanFormat))]
    [NotifyCanExecuteChangedFor(
        nameof(ErasePartitionCommand), nameof(FormatPartitionCommand))]
    private bool _dangerConfirmed;

    // ------------------------------------------------------------------
    // 任务状态
    // ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsIdle),
        nameof(CanReadPartitions),
        nameof(CanSwitchSlot),
        nameof(CanErase),
        nameof(CanFormat))]
    [NotifyCanExecuteChangedFor(
        nameof(ReadPartitionsCommand),
        nameof(ReadSlotCommand),
        nameof(SwitchSlotCommand),
        nameof(ErasePartitionCommand),
        nameof(FormatPartitionCommand),
        nameof(CancelCommand))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    private string _taskTitle = "空闲";

    [ObservableProperty]
    private string _taskMessage = "选择上方操作开始。";

    [ObservableProperty]
    private double _taskPercent;

    [ObservableProperty]
    private bool _taskIndeterminate;

    [ObservableProperty]
    private bool _taskFailed;

    public ObservableCollection<string> LogLines { get; }

    // ------------------------------------------------------------------
    // 命令
    // ------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanReadPartitions))]
    private async Task ReadPartitionsAsync()
    {
        await RunAsync("读取分区表", async ct =>
        {
            var memory = ResolveStorage(SelectedProfile);
            var list = await _reader
                .ReadPartitionsAsync(EdlPort, memory, CreateWorkDirectory(), CreateProgress(), ct)
                .ConfigureAwait(true);

            Post(() =>
            {
                Partitions.Clear();
                foreach (var p in list.OrderBy(x => x.Lun).ThenBy(x => x.StartSector))
                {
                    Partitions.Add(new ManagePartitionRow(p));
                }

                OnPropertyChanged(nameof(HasPartitions));
                PartitionStatus = list.Count == 0
                    ? "读取失败：请确认 9008 已就绪并已发送引导文件"
                    : $"共 {Partitions.Count} 个分区，合计 {TotalSizeText()}";
                SelectedPartition = Partitions.FirstOrDefault();
            });

            if (list.Count > 0)
            {
                // 顺手把槽位也读出来，省用户一次点击。
                await RefreshSlotCoreAsync(ct).ConfigureAwait(true);
            }

            return list.Count > 0
                ? FlashResult.Ok($"读到 {list.Count} 个分区")
                : FlashResult.Fail("没读到任何分区");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanReadPartitions))]
    private async Task ReadSlotAsync()
    {
        await RunAsync("读取槽位", async ct =>
        {
            var info = await RefreshSlotCoreAsync(ct).ConfigureAwait(true);
            return info.HasSlot
                ? FlashResult.Ok($"当前活动槽位：{info.Display}")
                : FlashResult.Fail($"未检测到 A/B 槽位（{info.Display}）");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanSwitchSlot))]
    private async Task SwitchSlotAsync()
    {
        if (SelectedSlotTarget is not { } target || IsBusy)
        {
            return;
        }

        await RunAsync($"切换到槽位 {target.Display}", async ct =>
        {
            var result = await _slots.SwitchAsync(
                target.Value, Connection, EdlPort, ResolveStorage(SelectedProfile),
                CreateProgress(), ct).ConfigureAwait(true);

            if (result.Success)
            {
                await RefreshSlotCoreAsync(ct).ConfigureAwait(true);
            }

            return result;
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanErase))]
    private async Task ErasePartitionAsync()
    {
        if (SelectedPartition is not { } partition)
        {
            return;
        }

        await RunAsync($"擦除 {partition.Name}", async ct =>
        {
            var result = await _clean.ErasePartitionAsync(
                partition.Name, EdlPort, ResolveStorage(SelectedProfile),
                CreateProgress(), ct).ConfigureAwait(true);

            if (result.Success)
            {
                Post(() => DangerConfirmed = false);
            }

            return result;
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanFormat))]
    private async Task FormatPartitionAsync()
    {
        if (SelectedPartition is not { } partition || SelectedFileSystem is not { } fs)
        {
            return;
        }

        await RunAsync($"格式化 {partition.Name} 为 {fs.Title}", async ct =>
        {
            var result = await _clean.FormatPartitionAsync(
                partition.Name, fs.Kind, VolumeLabel, CreateProgress(), ct).ConfigureAwait(true);

            if (result.Success)
            {
                Post(() => DangerConfirmed = false);
            }

            return result;
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _cts?.Cancel();
        Report("正在取消…");
    }

    [RelayCommand]
    public async Task RefreshConnectionAsync()
    {
        try
        {
            var info = await _devices.ProbeAsync(default).ConfigureAwait(true);
            Post(() =>
            {
                Connection = info.Mode;
                if (IsEdlConnected && !string.IsNullOrWhiteSpace(info.SerialOrPort))
                {
                    EdlPort = info.SerialOrPort;
                }
            });
            Append($"当前连接：{ConnectionText}");
        }
        catch (Exception ex)
        {
            _logger.Error("探测连接失败", ex);
        }
    }

    /// <summary>页面首次显示时调用。</summary>
    public async Task InitializeAsync()
    {
        if (Profiles.Count == 0 && _catalog.All.Count > 0)
        {
            Post(() =>
            {
                foreach (var profile in _catalog.All)
                {
                    Profiles.Add(profile);
                }

                SelectedProfile = Profiles.FirstOrDefault();
            });
        }

        await RefreshConnectionAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    private async Task<SlotInfo> RefreshSlotCoreAsync(CancellationToken cancellationToken)
    {
        var info = await _slots
            .ReadAsync(Connection, EdlPort, ResolveStorage(SelectedProfile), cancellationToken)
            .ConfigureAwait(true);

        Post(() =>
        {
            Slot = info;
            SelectedSlotTarget = SlotChoices.FirstOrDefault(t => t.Value != info.Current)
                ?? SlotChoices[0];
        });

        return info;
    }

    private string TotalSizeText()
    {
        var total = Partitions.Sum(p => p.SizeBytes);
        return total switch
        {
            >= 1024L * 1024 * 1024 => $"{total / (1024.0 * 1024 * 1024):F2} GB",
            >= 1024 * 1024 => $"{total / (1024.0 * 1024):F1} MB",
            _ => $"{total / 1024.0:F0} KB",
        };
    }

    private static StorageKind ResolveStorage(DeviceProfile? profile)
    {
        var raw = profile?.StorageType?.Trim().ToLowerInvariant();
        return raw switch
        {
            "ufs" => StorageKind.Ufs,
            "emmc" => StorageKind.Emmc,
            "spinor" => StorageKind.Spinor,
            _ => StorageKind.Auto,
        };
    }

    private static string CreateWorkDirectory()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "MeiyouToolbox", "manage", DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private IProgress<FlashProgress> CreateProgress() =>
        new Progress<FlashProgress>(p => Post(() =>
        {
            TaskMessage = p.Message;
            if (!double.IsNaN(p.Percent))
            {
                TaskIndeterminate = false;
                TaskPercent = p.Percent * 100;
            }
        }));

    private async Task RunAsync(
        string title,
        Func<CancellationToken, Task<FlashResult>> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        TaskTitle = title;
        TaskMessage = "正在准备…";
        TaskPercent = 0;
        TaskIndeterminate = true;
        TaskFailed = false;
        _cts = new CancellationTokenSource();

        Append($"=== {title} 开始 ===");

        try
        {
            var result = await action(_cts.Token).ConfigureAwait(true);

            Post(() =>
            {
                TaskMessage = result.Message;
                TaskFailed = !result.Success;
                TaskIndeterminate = false;
                TaskPercent = result.Success ? 100 : 0;
            });

            Append(result.Success ? $"✓ {result.Message}" : $"✗ {result.Message}");
        }
        catch (OperationCanceledException)
        {
            Post(() =>
            {
                TaskMessage = "已取消";
                TaskFailed = true;
                TaskIndeterminate = false;
            });
            Append("已取消");
        }
        catch (Exception ex)
        {
            _logger.Error($"{title} 失败", ex);
            Post(() =>
            {
                TaskMessage = $"发生错误：{ex.Message}";
                TaskFailed = true;
                TaskIndeterminate = false;
            });
            Append($"✗ {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Post(() => IsBusy = false);
        }
    }

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

    private void Append(string line)
    {
        var text = $"[{DateTime.Now:HH:mm:ss}] {line}";
        Post(() =>
        {
            LogLines.Add(text);
            while (LogLines.Count > 500)
            {
                LogLines.RemoveAt(0);
            }
        });
    }

    private void Report(string message, bool failed = false)
    {
        Post(() =>
        {
            TaskMessage = message;
            TaskFailed = failed;
        });
        Append(message);
    }
}
