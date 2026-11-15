using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeiyouToolbox.Models;
using MeiyouToolbox.Services;

namespace MeiyouToolbox.ViewModels;

/// <summary>
/// 刷机页。解锁 / 上锁 / 刷 Recovery / 写分区 / 9008 线刷，对应参考实现主菜单的基础操作。
/// <para>
/// 设计要点：每个动作在执行前先说明缺什么（机型、资料、连接通路），
/// 而不是像参考实现那样跑一半才弹一句错误。所有界面属性都经 <see cref="Post"/> 修改，
/// 保证跨线程安全。
/// </para>
/// </summary>
public sealed partial class FlashViewModel : ObservableObject
{
    private readonly IDeviceProfileCatalog _catalog;
    private readonly ICredentialStore _credentials;
    private readonly IFlashService _flash;
    private readonly IDeviceService _devices;
    private readonly IToolLocator _tools;
    private readonly IAppLogger _logger;

    private CancellationTokenSource? _cts;

    public FlashViewModel(
        IDeviceProfileCatalog catalog,
        ICredentialStore credentials,
        IFlashService flash,
        IDeviceService devices,
        IToolLocator tools,
        IAppLogger logger)
    {
        _catalog = catalog;
        _credentials = credentials;
        _flash = flash;
        _devices = devices;
        _tools = tools;
        _logger = logger;

        Profiles = new ObservableCollection<DeviceProfile>(_catalog.All);
        LogLines = [];

        // 默认选中第一个可用机型，避免下拉框空白。
        if (Profiles.Count > 0)
        {
            _selectedProfile = Profiles[0];
        }

        RefreshCredentialState();
        RefreshToolState();
    }

    /// <summary>设计器 / XAML 内实例化用。</summary>
    public FlashViewModel()
    {
        _catalog = null!;
        _credentials = null!;
        _flash = null!;
        _devices = null!;
        _tools = null!;
        _logger = null!;

        Profiles = [];
        LogLines = [];
    }

    // ------------------------------------------------------------------
    // 机型与连接
    // ------------------------------------------------------------------

    public ObservableCollection<DeviceProfile> Profiles { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(SelectedProfileDetail),
        nameof(HasProfile),
        nameof(ImportCodesHint),
        nameof(IsOdmDevice),
        nameof(SelectedProduct))]
    private DeviceProfile? _selectedProfile;

    public bool HasProfile => SelectedProfile is not null;

    public string SelectedProduct => SelectedProfile?.Product ?? string.Empty;

    public bool IsOdmDevice => SelectedProfile?.IsOdm == true;

    public string SelectedProfileDetail
    {
        get
        {
            if (SelectedProfile is not { } p)
            {
                return "尚未选择机型。刷机前请先选择与设备一致的机型，机型决定解锁方案与分区布局。";
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(p.Cpu))
            {
                parts.Add(p.Cpu);
            }

            parts.Add(p.StorageType.Length == 0 ? "存储未知" : p.StorageType.ToUpperInvariant());
            parts.Add(p.LayoutText);
            parts.Add(p.UnlockPlanText);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>当前设备连接方式。由页面被激活时刷新。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsConnected),
        nameof(ConnectionText),
        nameof(CanFlashHere))]
    [NotifyCanExecuteChangedFor(
        nameof(UnlockCommand),
        nameof(LockCommand),
        nameof(FlashRecoveryCommand),
        nameof(FlashPartitionCommand),
        nameof(FlashFullPackageCommand),
        nameof(SendFirehoseCommand))]
    private ConnectionMode _connection = ConnectionMode.None;

    public bool IsConnected => Connection != ConnectionMode.None;

    public string ConnectionText => Connection.Display();

    /// <summary>当前连接方式是否允许刷写。</summary>
    public bool CanFlashHere => _flash is not null && _flash.ResolveTransport(Connection) is not null;

    /// <summary>9008 端口号（有就显示，没有就留空）。</summary>
    [ObservableProperty]
    private string? _edlPort;

    // ------------------------------------------------------------------
    // 授权资料状态
    // ------------------------------------------------------------------

    [ObservableProperty]
    private string _credentialStatus = "尚未导入任何资料";

    [ObservableProperty]
    private bool _hasUnlockCode;

    [ObservableProperty]
    private bool _hasAuthCode;

    [ObservableProperty]
    private bool _hasMzkv;

    /// <summary>当前机型需要导入哪些东西（显式提示，而不是让用户猜）。</summary>
    public string ImportCodesHint
    {
        get
        {
            if (SelectedProfile is not { } p)
            {
                return string.Empty;
            }

            if (p.IsOdm)
            {
                return $"{p.Model} 使用 {p.UnlockPlanText}，不需要解锁码或授权码。";
            }

            var needed = new List<string>();
            if (p.NeedsUnlockCode)
            {
                needed.Add("512 字符解锁码");
            }

            if (p.NeedsAuthCode)
            {
                needed.Add("512 字符授权码");
            }

            if (p.NeedsMzkv)
            {
                needed.Add("解锁用 mzkv 镜像");
            }

            return needed.Count == 0
                ? "该机型未在机型表里声明所需资料。"
                : "需要：" + string.Join(" + ", needed);
        }
    }

    // ------------------------------------------------------------------
    // 工具可用性
    // ------------------------------------------------------------------

    [ObservableProperty]
    private string _toolStatus = string.Empty;

    [ObservableProperty]
    private bool _toolsReady;

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
    private bool _taskIndeterminate;

    [ObservableProperty]
    private bool _taskFailed;

    /// <summary>实时输出。用 ObservableCollection 是为了随刷写过程滚动可见。</summary>
    public ObservableCollection<string> LogLines { get; }

    // ------------------------------------------------------------------
    // 用户输入（文件路径）
    // ------------------------------------------------------------------

    [ObservableProperty]
    private string? _recoveryImagePath;

    [ObservableProperty]
    private string? _partitionName;

    [ObservableProperty]
    private string? _partitionImagePath;

    [ObservableProperty]
    private string? _packageDirectory;

    [ObservableProperty]
    private string? _programmerPath;

    /// <summary>界面上正在编辑的解锁码（导入前）。</summary>
    [ObservableProperty]
    private string? _unlockCodeInput;

    /// <summary>界面上正在编辑的授权码（导入前）。</summary>
    [ObservableProperty]
    private string? _authCodeInput;

    // ------------------------------------------------------------------
    // 命令：资料导入
    // ------------------------------------------------------------------

    /// <summary>保存粘贴进来的解锁码。</summary>
    [RelayCommand]
    private void SaveUnlockCode() => SaveCode(UnlockCodeInput, isAuthCode: false);

    /// <summary>保存粘贴进来的授权码。</summary>
    [RelayCommand]
    private void SaveAuthCode() => SaveCode(AuthCodeInput, isAuthCode: true);

    private void SaveCode(string? code, bool isAuthCode)
    {
        if (SelectedProfile is not { } profile)
        {
            Report("请先选择机型", failed: true);
            return;
        }

        var result = isAuthCode
            ? _credentials.SaveAuthCode(profile.Product, code ?? string.Empty)
            : _credentials.SaveUnlockCode(profile.Product, code ?? string.Empty);

        Report(result.Message, failed: !result.Success);
        RefreshCredentialState();
    }

    /// <summary>导入 mzkv 镜像。参数是绝对路径（由视图的文件选择器给出）。</summary>
    [RelayCommand]
    private void ImportMzkv(string? path)
    {
        if (SelectedProfile is not { } profile)
        {
            Report("请先选择机型", failed: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var result = _credentials.ImportMzkv(profile.Product, path);
        Report(result.Message, failed: !result.Success);
        RefreshCredentialState();
    }

    [RelayCommand]
    private void ClearCredentials()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        _credentials.Clear(profile.Product);
        RefreshCredentialState();
        Report($"{profile.Product} 的资料已清空");
    }

    // ------------------------------------------------------------------
    // 命令：刷机动作
    // ------------------------------------------------------------------

    private bool CanRunAction() => !IsBusy && SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    private async Task UnlockAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        await RunAsync($"解锁 BL · {profile.Model}", async token =>
        {
            var credentials = _credentials.Load(profile.Product);
            var progress = CreateFlashProgress();

            var result = await _flash.UnlockBootloaderAsync(
                profile, credentials, Connection, EdlPort,
                ResolveStorage(profile), progress, token).ConfigureAwait(true);

            return result;
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    private async Task LockAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        await RunAsync($"上锁 BL · {profile.Model}", async token =>
        {
            var progress = CreateFlashProgress();
            return await _flash.LockBootloaderAsync(
                profile, Connection, EdlPort, ResolveStorage(profile), progress, token)
                .ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    private async Task FlashRecoveryAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(RecoveryImagePath) || !File.Exists(RecoveryImagePath))
        {
            Report("请先选择 Recovery 镜像文件", failed: true);
            return;
        }

        if (profile.Layout is PartitionLayout.Ab or PartitionLayout.AbRec)
        {
            // A/B 设备没有独立 recovery 分区，刷进 recovery 会写坏 boot。
            Report(
                $"{profile.Model} 是 A/B 分区设备，没有独立 recovery 分区。" +
                "请改用「刷入任意分区」，目标是 boot 或 init_boot。", failed: true);
            return;
        }

        await RunAsync($"刷入 Recovery · {profile.Model}", async token =>
        {
            return await _flash.FlashPartitionAsync(
                "recovery", RecoveryImagePath!, Connection, EdlPort,
                ResolveStorage(profile), CreateFlashProgress(), token).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    private async Task FlashPartitionAsync()
    {
        if (string.IsNullOrWhiteSpace(PartitionName))
        {
            Report("请填写分区名", failed: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(PartitionImagePath) || !File.Exists(PartitionImagePath))
        {
            Report("请选择要刷入的镜像文件", failed: true);
            return;
        }

        await RunAsync($"刷入分区 {PartitionName}", async token =>
        {
            return await _flash.FlashPartitionAsync(
                PartitionName!.Trim(), PartitionImagePath!, Connection, EdlPort,
                ResolveStorage(SelectedProfile), CreateFlashProgress(), token).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    private async Task FlashFullPackageAsync()
    {
        if (string.IsNullOrWhiteSpace(PackageDirectory) || !Directory.Exists(PackageDirectory))
        {
            Report("请选择 9008 线刷包目录", failed: true);
            return;
        }

        await RunAsync("9008 线刷完整包", async token =>
        {
            var credentials = SelectedProfile is { } p
                ? _credentials.Load(p.Product)
                : null;

            return await _flash.FlashRawPackageAsync(
                PackageDirectory!, EdlPort,
                ResolveStorage(SelectedProfile),
                ProgrammerPath,
                credentials?.AuthCode,
                CreateFlashProgress(), token).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    private async Task SendFirehoseAsync()
    {
        if (string.IsNullOrWhiteSpace(ProgrammerPath) || !File.Exists(ProgrammerPath))
        {
            Report("请先选择 Firehose 引导文件（prog_firehose_ddr.elf）", failed: true);
            return;
        }

        await RunAsync("9008 发送引导", async token =>
        {
            var credentials = SelectedProfile is { } p ? _credentials.Load(p.Product) : null;

            return await _flash.SendFirehoseAsync(
                EdlPort, ProgrammerPath!, ResolveStorage(SelectedProfile),
                credentials?.AuthCode, CreateFlashProgress(), token).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _cts?.Cancel();
        Append("已请求取消…");
    }

    /// <summary>页面激活时刷新连接与端口。由视图调用。</summary>
    [RelayCommand]
    public async Task RefreshConnectionAsync()
    {
        if (_devices is null)
        {
            return;
        }

        try
        {
            var info = await _devices.ProbeAsync().ConfigureAwait(true);
            Connection = info.Mode;

            if (info.Mode == ConnectionMode.QcEdl && !string.IsNullOrWhiteSpace(info.SerialOrPort))
            {
                var port = info.SerialOrPort.Replace("COM", string.Empty, StringComparison.OrdinalIgnoreCase);
                EdlPort = port.Trim();
            }
            else if (info.Mode != ConnectionMode.QcEdl)
            {
                EdlPort = null;
            }

            // 探测到设备时自动匹配机型（用户仍可手动改）。
            if (!HasProfile)
            {
                var guess = _catalog.Guess([info.Codename, info.Model]);
                if (guess is not null)
                {
                    SelectedProfile = guess;
                }
            }

            Append($"当前连接：{ConnectionText}");
        }
        catch (Exception ex)
        {
            _logger.Error("刷新连接状态失败", ex);
        }
    }

    partial void OnSelectedProfileChanged(DeviceProfile? value)
    {
        RefreshCredentialState();
        RefreshToolState();
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    private static StorageKind ResolveStorage(DeviceProfile? profile) =>
        profile?.StorageType.ToLowerInvariant() switch
        {
            "ufs" => StorageKind.Ufs,
            "emmc" => StorageKind.Emmc,
            _ => StorageKind.Auto,
        };

    private IProgress<FlashProgress> CreateFlashProgress() =>
        new Progress<FlashProgress>(p => Post(() =>
        {
            TaskMessage = p.Message;
            if (!p.IsIndeterminate)
            {
                TaskPercent = p.Percent;
                TaskIndeterminate = false;
            }

            // 只把有意义的一行放进日志，避免刷写时几千行把界面刷爆。
            if (p.Message.Length > 0 && !p.Message.Contains('%'))
            {
                Append(p.Message);
            }
        }));

    /// <summary>
    /// 统一的动作包装：状态复位 → 执行 → 结果落到 <see cref="TaskMessage"/>。
    /// 所有 UI 更新都经过 <see cref="Post"/>，避免跨线程访问。
    /// </summary>
    private async Task RunAsync(string title, Func<CancellationToken, Task<FlashResult>> action)
    {
        if (IsBusy)
        {
            return;
        }

        _cts = new CancellationTokenSource();

        Post(() =>
        {
            IsBusy = true;
            TaskTitle = title;
            TaskMessage = "正在准备…";
            TaskPercent = 0;
            TaskIndeterminate = true;
            TaskFailed = false;
            LogLines.Clear();
        });

        try
        {
            var result = await action(_cts.Token).ConfigureAwait(true);

            Post(() =>
            {
                TaskMessage = result.Message;
                TaskFailed = !result.Success;
                TaskIndeterminate = false;
                TaskPercent = result.Success ? 100 : TaskPercent;
                Append(result.Success ? $"✓ {result.Message}" : $"✗ {result.Message}");
            });

            _logger.Info($"{title}: {(result.Success ? "成功" : "失败")} — {result.Message}");
            RefreshCredentialState();
        }
        catch (OperationCanceledException)
        {
            Post(() =>
            {
                TaskMessage = "已取消";
                TaskFailed = true;
                TaskIndeterminate = false;
            });

            _logger.Warn($"{title}: 用户取消");
        }
        catch (Exception ex)
        {
            _logger.Error($"{title}: 未处理异常", ex);
            Post(() =>
            {
                TaskMessage = $"发生错误：{ex.Message}";
                TaskFailed = true;
                TaskIndeterminate = false;
                Append($"✗ {ex}");
            });
        }
        finally
        {
            Post(() => IsBusy = false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void RefreshCredentialState()
    {
        if (SelectedProfile is not { } profile || _credentials is null)
        {
            CredentialStatus = "尚未选择机型";
            HasUnlockCode = HasAuthCode = HasMzkv = false;
            return;
        }

        var credentials = _credentials.Load(profile.Product);
        HasUnlockCode = credentials.HasUnlockCode;
        HasAuthCode = credentials.HasAuthCode;
        HasMzkv = credentials.HasMzkv;

        var items = new List<string>();
        if (profile.NeedsUnlockCode)
        {
            items.Add(HasUnlockCode ? "解锁码 ✓" : "解锁码 ✗");
        }

        if (profile.NeedsAuthCode)
        {
            items.Add(HasAuthCode ? "授权码 ✓" : "授权码 ✗");
        }

        if (profile.NeedsMzkv)
        {
            items.Add(HasMzkv ? "mzkv ✓" : "mzkv ✗");
        }

        CredentialStatus = items.Count == 0
            ? "该机型不需要授权资料"
            : string.Join("    ", items);
    }

    private void RefreshToolState()
    {
        if (_tools is null)
        {
            return;
        }

        var missing = new List<string>();
        if (_tools.AdbPath is null)
        {
            missing.Add("adb");
        }

        if (_tools.FastbootPath is null)
        {
            missing.Add("fastboot");
        }

        var edlTools = new[]
        {
            "QSaharaServer.exe", "fh_loader.exe", "qcedlcmdhelper.exe", "gpttool.exe",
        };
        var missingEdl = edlTools.Where(t => !_tools.HasTool(t)).ToList();

        ToolsReady = missing.Count == 0;

        if (missing.Count == 0 && missingEdl.Count == 0)
        {
            ToolStatus = "工具齐全（adb / fastboot / 9008 全套）";
        }
        else if (missing.Count > 0)
        {
            ToolStatus = "缺少基础工具：" + string.Join("、", missing) + "。9008 相关功能" +
                         (missingEdl.Count == 0 ? "可用" : "还需：" + string.Join("、", missingEdl));
        }
        else
        {
            ToolStatus = "缺少 9008 工具：" + string.Join("、", missingEdl) +
                         "。基础刷写（adb / fastboot）可用。";
        }
    }

    /// <summary>把 UI 更新切到 UI 线程。所有绑定属性都通过它修改。</summary>
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
        Post(() =>
        {
            LogLines.Add(line);
            if (LogLines.Count > 500)
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
            TaskIndeterminate = false;
            Append(failed ? $"✗ {message}" : message);
        });

        if (failed)
        {
            _logger.Warn(message);
        }
        else
        {
            _logger.Info(message);
        }
    }
}
