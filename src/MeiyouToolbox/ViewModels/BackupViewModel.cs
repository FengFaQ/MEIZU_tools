using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeiyouToolbox.Models;
using MeiyouToolbox.Services;

namespace MeiyouToolbox.ViewModels;

/// <summary>备份范围在界面上的一个选项。</summary>
public sealed record BackupScopeOption(BackupScope Scope, string Title, string Description)
{
    public override string ToString() => Title;
}

/// <summary>
/// 备份页。全分区备份 / 分区表 / 单个分区 / QCN 基带，对应参考实现主菜单的
/// <c>5.备份全分区</c>、<c>12.回读任意分区</c>、<c>16.备份,恢复QCN</c>。
/// <para>
/// 与参考实现的差别：参考实现把这些散在四个不同菜单项里，用户要先想清楚"我该走哪条路"；
/// 这里合成一页，选范围 → 选目录 → 开始，分区勾选由程序读出来列给你。
/// </para>
/// </summary>
public sealed partial class BackupViewModel : ObservableObject
{
    private readonly IBackupService _backup;
    private readonly IDeviceService _devices;
    private readonly IDeviceProfileCatalog _catalog;
    private readonly IToolLocator _tools;
    private readonly IAppLogger _logger;

    private CancellationTokenSource? _cts;

    public BackupViewModel(
        IBackupService backup,
        IDeviceService devices,
        IDeviceProfileCatalog catalog,
        IToolLocator tools,
        IAppLogger logger)
    {
        _backup = backup;
        _devices = devices;
        _catalog = catalog;
        _tools = tools;
        _logger = logger;

        Scopes =
        [
            new(BackupScope.AllPartitions, "全分区备份",
                "备份分区表与绝大部分分区，产出可直接 9008 线刷的包。不含用户数据。"),
            new(BackupScope.PartitionTable, "只备份分区表", "只回读 GPT，体积很小，适合改分区前留底。"),
            new(BackupScope.SinglePartition, "备份单个分区", "按名字回读一个分区，比如 boot、modem。" ),
            new(BackupScope.Qcn, "QCN 基带", "不在普通分区里，必须让设备进入 DIAG 模式（拨号 *#*#88#*#*）。"),
        ];

        _selectedScope = Scopes[0];
        Partitions = [];
        LogLines = [];

        // 默认保存到「程序目录\backups」，用户可改。
        _targetDirectory = Path.Combine(_tools.AppDirectory, "backups");
    }

    /// <summary>XAML 内实例化用。</summary>
    public BackupViewModel()
    {
        _backup = null!;
        _devices = null!;
        _catalog = null!;
        _tools = null!;
        _logger = null!;

        Scopes = [];
        Partitions = [];
        LogLines = [];
        _targetDirectory = string.Empty;
    }

    // ------------------------------------------------------------------
    // 范围与路径
    // ------------------------------------------------------------------

    public ObservableCollection<BackupScopeOption> Scopes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsQcnScope),
        nameof(IsSingleScope),
        nameof(NeedsPartitionPick),
        nameof(ScopeHint))]
    [NotifyCanExecuteChangedFor(nameof(StartBackupCommand), nameof(ListPartitionsCommand))]
    private BackupScopeOption? _selectedScope;

    public bool IsQcnScope => SelectedScope?.Scope == BackupScope.Qcn;

    public bool IsSingleScope => SelectedScope?.Scope == BackupScope.SinglePartition;

    public bool NeedsPartitionPick => IsQcnScope || IsSingleScope;

    public string ScopeHint => SelectedScope?.Description ?? string.Empty;

    /// <summary>设备当前连接方式。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(ConnectionText), nameof(IsEdlConnected))]
    [NotifyCanExecuteChangedFor(
        nameof(StartBackupCommand), nameof(ListPartitionsCommand), nameof(RestoreQcnCommand))]
    private ConnectionMode _connection = ConnectionMode.None;

    public bool IsConnected => Connection != ConnectionMode.None;

    public bool IsEdlConnected => Connection == ConnectionMode.QcEdl;

    public string ConnectionText => Connection.Display();

    /// <summary>机型（用于生成目录名与清单）。</summary>
    public ObservableCollection<DeviceProfile> Profiles { get; } = [];

    [ObservableProperty]
    private DeviceProfile? _selectedProfile;

    [ObservableProperty]
    private string? _edlPort;

    [ObservableProperty]
    private string _storageText = "自动识别";

    [ObservableProperty]
    private string _targetDirectory = string.Empty;

    [ObservableProperty]
    private string? _qcnFilePath;

    // ------------------------------------------------------------------
    // 分区列表
    // ------------------------------------------------------------------

    public ObservableCollection<PartitionRow> Partitions { get; }

    [ObservableProperty]
    private PartitionRow? _selectedPartition;

    [ObservableProperty]
    private string _partitionStatus = "尚未读取分区表";

    [ObservableProperty]
    private bool _hasPartitions;

    // ------------------------------------------------------------------
    // 任务状态
    // ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

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

    [ObservableProperty]
    private string? _lastBackupPath;

    public ObservableCollection<string> LogLines { get; }

    // ------------------------------------------------------------------
    // 命令
    // ------------------------------------------------------------------

    private bool CanStart() => !IsBusy && IsConnected && SelectedScope is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartBackupAsync()
    {
        if (SelectedScope is not { } scope)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(TargetDirectory))
        {
            Report("请先选择保存目录", failed: true);
            return;
        }

        if (scope.Scope == BackupScope.SinglePartition && SelectedPartition is null)
        {
            Report("请先在分区列表里选一个分区", failed: true);
            return;
        }

        if (scope.Scope == BackupScope.PartitionTable || scope.Scope == BackupScope.SinglePartition)
        {
            // 后两种也要先把分区表读出来。
            if (Partitions.Count == 0)
            {
                var loaded = await LoadPartitionsAsync().ConfigureAwait(true);
                if (!loaded)
                {
                    return;
                }
            }
        }

        var request = new BackupRequest
        {
            TargetDirectory = TargetDirectory,
            Scope = scope.Scope,
            Product = SelectedProfile?.Product,
            Port = EdlPort,
            Memory = ResolveStorage(SelectedProfile),
            PartitionName = SelectedPartition?.Name,
        };

        await RunAsync(scope.Title, async ct =>
        {
            var progress = CreateProgress();
            var result = await _backup.RunAsync(request, progress, ct).ConfigureAwait(true);
            return (result.Success, result.Message, result.OutputPath);
        }).ConfigureAwait(true);
    }

    private bool CanList() => !IsBusy && IsConnected;

    [RelayCommand(CanExecute = nameof(CanList))]
    private async Task ListPartitionsAsync() => await LoadPartitionsAsync().ConfigureAwait(true);

    private async Task<bool> LoadPartitionsAsync()
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        TaskTitle = "读取分区表";
        TaskIndeterminate = true;
        TaskFailed = false;
        TaskMessage = "正在从设备读取 GPT…";

        try
        {
            var memory = ResolveStorage(SelectedProfile);
            var list = await _backup.ListPartitionsAsync(
                EdlPort, memory, CreateProgress(), _cts?.Token ?? default).ConfigureAwait(true);

            Post(() =>
            {
                Partitions.Clear();
                foreach (var p in list.OrderBy(p => p.Lun).ThenBy(p => p.StartSector))
                {
                    Partitions.Add(new PartitionRow(p));
                }

                HasPartitions = Partitions.Count > 0;
                PartitionStatus = Partitions.Count == 0
                    ? "读取失败：请确认设备已在 9008 且已发送引导"
                    : $"共 {Partitions.Count} 个分区，合计 {TotalSizeText()}";
                SelectedPartition = Partitions.FirstOrDefault();
                TaskMessage = PartitionStatus;
                TaskFailed = Partitions.Count == 0;
            });

            return list.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.Error("读取分区表失败", ex);
            Post(() =>
            {
                TaskFailed = true;
                TaskMessage = $"读取分区表失败：{ex.Message}";
                PartitionStatus = TaskMessage;
            });
            return false;
        }
        finally
        {
            Post(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private async Task PickTargetDirectoryAsync()
    {
        // 实际的目录选择由 View 的代码后置提供（需要 TopLevel）。
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private bool CanRestoreQcn() => !IsBusy && IsConnected && !string.IsNullOrWhiteSpace(QcnFilePath);

    [RelayCommand(CanExecute = nameof(CanRestoreQcn))]
    private async Task RestoreQcnAsync()
    {
        if (string.IsNullOrWhiteSpace(QcnFilePath))
        {
            return;
        }

        await RunAsync("恢复 QCN", async ct =>
        {
            var result = await _backup
                .RestoreQcnAsync(EdlPort, QcnFilePath!, CreateProgress(), ct)
                .ConfigureAwait(true);
            return (result.Success, result.Message, result.OutputPath);
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
            Post(() => Connection = info.Mode);
            Append($"当前连接：{ConnectionText}");
        }
        catch (Exception ex)
        {
            _logger.Error("探测连接失败", ex);
        }
    }

    /// <summary>刷新目录选择（由 View 回填）。</summary>
    public void SetTargetDirectory(string directory)
    {
        TargetDirectory = directory;
        Append($"保存目录：{directory}");
    }

    /// <summary>刷新 QCN 文件（由 View 回填）。</summary>
    public void SetQcnFile(string path)
    {
        QcnFilePath = path;
        RestoreQcnCommand.NotifyCanExecuteChanged();
        Append($"已选择 QCN：{Path.GetFileName(path)}");
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

        Append($"保存目录：{TargetDirectory}");
        await RefreshConnectionAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

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

    private IProgress<BackupProgress> CreateProgress() =>
        new Progress<BackupProgress>(p => Post(() =>
        {
            TaskMessage = p.Message;
            if (!p.IsIndeterminate)
            {
                TaskIndeterminate = false;
                TaskPercent = p.Percent * 100;
            }
        }));

    private async Task RunAsync(
        string title,
        Func<CancellationToken, Task<(bool Success, string Message, string? OutputPath)>> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        TaskTitle = title;
        TaskMessage = "正在准备…";
        TaskIndeterminate = true;
        TaskFailed = false;
        _cts = new CancellationTokenSource();

        Append($"=== {title} 开始 ===");

        try
        {
            var (success, message, outputPath) = await action(_cts.Token).ConfigureAwait(true);
            Post(() =>
            {
                TaskFailed = !success;
                TaskIndeterminate = false;
                TaskPercent = success ? 100 : 0;
                TaskMessage = message;
                if (outputPath is not null)
                {
                    LastBackupPath = outputPath;
                }
            });

            Append(success ? $"完成：{message}" : $"失败：{message}");
        }
        catch (OperationCanceledException)
        {
            Post(() =>
            {
                TaskFailed = true;
                TaskMessage = "已取消";
            });
            Append("已取消");
        }
        catch (Exception ex)
        {
            _logger.Error($"{title} 失败", ex);
            Post(() =>
            {
                TaskFailed = true;
                TaskMessage = $"失败：{ex.Message}";
            });
            Append($"失败：{ex.Message}");
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

/// <summary>界面上的分区行。</summary>
public sealed class PartitionRow(GptPartition partition)
{
    public GptPartition Source { get; } = partition;

    public int Lun => Source.Lun;
    public string Name => Source.Name;
    public long StartSector => Source.StartSector;
    public long SectorCount => Source.SectorCount;
    public long SizeBytes => Source.SizeBytes;
    public string SizeText => Source.SizeText;
}
