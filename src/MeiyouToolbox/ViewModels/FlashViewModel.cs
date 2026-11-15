using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeiyouToolbox.Models;
using MeiyouToolbox.Services;

namespace MeiyouToolbox.ViewModels;

/// <summary>刷机页里"镜像刷写"的两种目标通路。</summary>
public enum FlashTarget
{
    Edl,
    Fastboot,
}

/// <summary>
/// 刷机页。解锁 / 上锁 / 镜像刷写 / 刷 Recovery / 写分区。
/// <para>
/// 与参考实现的关键差别：解锁与上锁不再要求用户先自己把手机切到正确状态，
/// 而是点下按钮后由 <see cref="IDeviceModeService"/> 自动从任何已知状态重启到位；
/// 任务结束后统一弹「完成任务提示」，把"清空数据并开机 / 直接开机"固化成一个可复用环节。
/// </para>
/// </summary>
public sealed partial class FlashViewModel : ObservableObject
{
    private readonly IDeviceProfileCatalog _catalog;
    private readonly ICredentialStore _credentials;
    private readonly IFlashService _flash;
    private readonly IDeviceService _devices;
    private readonly IDeviceModeService _modes;
    private readonly IToolLocator _tools;
    private readonly TaskCompleteViewModel _taskComplete;
    private readonly EdlFlashViewModel _edl;
    private readonly FastbootFlashViewModel _fastboot;
    private readonly IUnlockPlanner _planner;
    private readonly IAppLogger _logger;

    private CancellationTokenSource? _cts;

    public FlashViewModel(
        IDeviceProfileCatalog catalog,
        ICredentialStore credentials,
        IFlashService flash,
        IDeviceService devices,
        IDeviceModeService modes,
        IToolLocator tools,
        TaskCompleteViewModel taskComplete,
        EdlFlashViewModel edl,
        FastbootFlashViewModel fastboot,
        IUnlockPlanner planner,
        IAppLogger logger)
    {
        _catalog = catalog;
        _credentials = credentials;
        _flash = flash;
        _devices = devices;
        _modes = modes;
        _tools = tools;
        _taskComplete = taskComplete;
        _edl = edl;
        _fastboot = fastboot;
        _planner = planner;
        _logger = logger;

        Profiles = new ObservableCollection<DeviceProfile>(_catalog.All);
        LogLines = [];

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
        _modes = null!;
        _tools = null!;
        _taskComplete = null!;
        _edl = null!;
        _fastboot = null!;
        _planner = null!;
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
        nameof(SelectedProduct),
        nameof(UnlockRequiresModeText),
        nameof(CanUnlockNow))]
    [NotifyCanExecuteChangedFor(
        nameof(UnlockCommand),
        nameof(LockCommand),
        nameof(FlashRecoveryCommand),
        nameof(FlashPartitionCommand),
        nameof(SendFirehoseCommand))]
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
        nameof(FlashPackageCommand),
        nameof(SendFirehoseCommand))]
    private ConnectionMode _connection = ConnectionMode.None;

    public bool IsConnected => Connection != ConnectionMode.None;

    public string ConnectionText => Connection.Display();

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

    /// <summary>
    /// 解锁时需要的连接状态。由解锁方案 + 已导入资料共同决定，界面上直接告诉用户
    /// "点下去会自动切到 X"；两阶段的机型（Fastboot → 9008）会完整写出来。
    /// </summary>
    public string UnlockRequiresModeText
    {
        get
        {
            if (SelectedProfile is not { } p)
            {
                return string.Empty;
            }

            if (_planner is null || _credentials is null)
            {
                return string.Empty;
            }

            var plan = _planner.Plan(p, _credentials.Load(p.Product));

            if (plan.BlockedReason is { } blocked)
            {
                return blocked;
            }

            var steps = plan.Steps.Count;

            // 单步时用步骤自身的说明，别再重复一遍模式名 —— 前半句已经说了"切到 9008"。
            return steps > 1
                ? $"解锁分 {steps} 步：{plan.Summary}"
                : $"会自动切到 {plan.EntryMode.Display()}：{plan.Steps[0].Label}";
        }
    }

    /// <summary>解锁按钮是否可点：机型已选、未在忙、且资料齐备（计划能成立）。</summary>
    public bool CanUnlockNow
    {
        get
        {
            if (IsBusy || SelectedProfile is not { } p || _planner is null || _credentials is null)
            {
                return false;
            }

            return _planner.Plan(p, _credentials.Load(p.Product)).CanRun;
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
    [NotifyPropertyChangedFor(nameof(CanUnlockNow))]
    [NotifyCanExecuteChangedFor(
        nameof(CancelCommand),
        nameof(UnlockCommand),
        nameof(LockCommand),
        nameof(FlashPackageCommand),
        nameof(FlashRecoveryCommand),
        nameof(FlashPartitionCommand),
        nameof(SendFirehoseCommand))]
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

    /// <summary>刷入镜像：单个输入框，支持 9008 线刷包目录或 fastboot 镜像文件。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackageKindText), nameof(IsPackageDirectory), nameof(HasPackagePath))]
    [NotifyCanExecuteChangedFor(nameof(FlashPackageCommand))]
    private string? _packagePath;

    [ObservableProperty]
    private string? _programmerPath;

    [ObservableProperty]
    private string? _unlockCodeInput;

    [ObservableProperty]
    private string? _authCodeInput;

    /// <summary>镜像刷写当前选中的通路，决定"刷入"按钮走哪条路。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlashTargetText), nameof(FlashHereButtonText))]
    [NotifyCanExecuteChangedFor(nameof(FlashPackageCommand))]
    private FlashTarget _selectedTarget = FlashTarget.Edl;

    public string FlashTargetText => SelectedTarget == FlashTarget.Edl ? "9008 刷写" : "Fastboot 刷写";

    public string FlashHereButtonText => "刷入";

    /// <summary>通路选择子窗口是否可见（在窗口内自渲染，不开新窗口）。</summary>
    [ObservableProperty]
    private bool _isTargetPickerOpen;

    public bool HasPackagePath => !string.IsNullOrWhiteSpace(PackagePath);

    public bool IsPackageDirectory =>
        !string.IsNullOrWhiteSpace(PackagePath) && Directory.Exists(PackagePath);

    /// <summary>根据输入路径判断是线刷包目录还是单个镜像，给界面一个即时反馈。</summary>
    public string PackageKindText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PackagePath))
            {
                return "未选择";
            }

            if (Directory.Exists(PackagePath))
            {
                var xmlCount = 0;
                try
                {
                    xmlCount = Directory.GetFiles(PackagePath, "rawprogram*.xml", SearchOption.AllDirectories).Length;
                }
                catch (Exception)
                {
                    // 目录不可读时按普通目录处理。
                }

                return xmlCount > 0
                    ? $"9008 线刷包（{xmlCount} 个 rawprogram xml）"
                    : "目录（未找到 rawprogram*.xml，可能不是线刷包）";
            }

            if (File.Exists(PackagePath))
            {
                var ext = Path.GetExtension(PackagePath).ToLowerInvariant();
                return ext switch
                {
                    ".img" or ".bin" or ".mbn" => $"镜像文件（{ext}）",
                    ".zip" => "压缩包（需解包后再刷）",
                    _ => $"文件（{ext}）",
                };
            }

            return "路径不存在";
        }
    }

    // ------------------------------------------------------------------
    // 命令：资料导入
    // ------------------------------------------------------------------

    /// <summary>解锁资料子窗口是否展开。</summary>
    [ObservableProperty]
    private bool _isCredentialEditorOpen;

    [RelayCommand]
    private void OpenCredentialEditor()
    {
        if (SelectedProfile is null)
        {
            Report("请先选择机型", failed: true);
            return;
        }

        IsCredentialEditorOpen = true;
    }

    [RelayCommand]
    private void CloseCredentialEditor() => IsCredentialEditorOpen = false;

    [RelayCommand]
    private void SaveUnlockCode() => SaveCode(UnlockCodeInput, isAuthCode: false);

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
        OnPropertyChanged(nameof(UnlockRequiresModeText));
    }

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
        OnPropertyChanged(nameof(UnlockRequiresModeText));
    }

    // ------------------------------------------------------------------
    // 命令：解锁 / 上锁（自动切模式）
    // ------------------------------------------------------------------

    /// <summary>
    /// 解锁：按 <see cref="IUnlockPlanner"/> 算出的阶段逐步执行。
    /// <para>
    /// 关键点是**每一步自己切状态**：auth-private 机型可能是
    /// 「Fastboot 写解锁码 → 9008 写 private」两阶段，中间设备会重启，
    /// 所以每个阶段前都重新 <see cref="IDeviceModeService.EnsureModeAsync"/> 一次。
    /// 这对应参考实现 <c>L778-L809</c>（Fastboot 段）与 <c>L819-L856</c>（9008 段）。
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUnlockNow))]
    private async Task UnlockAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        var credentials = _credentials.Load(profile.Product);
        var plan = _planner.Plan(profile, credentials);

        if (plan.BlockedReason is { } blocked)
        {
            Report(blocked, failed: true);
            _logger.Warn($"解锁被阻止：{blocked}");
            return;
        }

        var taskName = $"解锁 Bootloader · {profile.Model}";
        var stepIndex = 0;

        var ok = await RunAsync(taskName, async token =>
        {
            foreach (var step in plan.Steps)
            {
                stepIndex++;
                var prefix = plan.Steps.Count > 1 ? $"[{stepIndex}/{plan.Steps.Count}] " : string.Empty;

                Report($"{prefix}正在把设备切换到 {step.Mode.Display()}…");

                var navigation = await _modes.EnsureModeAsync(
                    step.Mode, EdlPort, ResolveStorage(profile), CreateFlashProgress(), token)
                    .ConfigureAwait(true);

                if (!navigation.Success)
                {
                    return FlashResult.Fail(navigation.Message);
                }

                // 切模式后端口可能变了，重新读一次再下发。
                await ReReadPortAsync(navigation.Mode).ConfigureAwait(true);

                Report($"{prefix}{step.Label}");

                var result = await _flash.UnlockBootloaderAsync(
                    profile, credentials, navigation.Mode, EdlPort,
                    ResolveStorage(profile), CreateFlashProgress(), token).ConfigureAwait(true);

                if (!result.Success)
                {
                    return result;
                }

                // 阶段之间设备可能需要重新枚举，给一点缓冲。
                if (stepIndex < plan.Steps.Count)
                {
                    await System.Threading.Tasks.Task
                        .Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(true);
                }
            }

            return FlashResult.Ok(plan.Steps.Count > 1
                ? $"已完成 {plan.Steps.Count} 个阶段的解锁"
                : "解锁完成");
        }).ConfigureAwait(true);

        ShowTaskComplete(taskName, ok);
    }

    /// <summary>上锁只看「机型已选 + 未在忙」；上锁不需要任何解锁资料。</summary>
    private bool CanRunLock() => !IsBusy && SelectedProfile is not null;

    [RelayCommand(CanExecute = nameof(CanRunLock))]
    private async Task LockAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        var taskName = $"上锁 Bootloader · {profile.Model}";

        var ok = await RunAsync(taskName, async token =>
        {
            // 上锁必须走 Fastboot（原工具箱的顺序：先刷 misc 再 oem lock）。
            Report("正在把设备切换到 Fastboot 模式…");

            var navigation = await _modes.EnsureModeAsync(
                ConnectionMode.Fastboot, EdlPort, ResolveStorage(profile),
                CreateFlashProgress(), token).ConfigureAwait(true);

            if (!navigation.Success)
            {
                return FlashResult.Fail(navigation.Message);
            }

            var result = await _flash.LockBootloaderAsync(
                profile, navigation.Mode, EdlPort, ResolveStorage(profile),
                CreateFlashProgress(), token).ConfigureAwait(true);

            return result;
        }).ConfigureAwait(true);

        ShowTaskComplete(taskName, ok);
    }

    // ------------------------------------------------------------------
    // 命令：镜像刷写（9008 / fastboot 两条通路）
    // ------------------------------------------------------------------

    private bool CanFlashPackage() => !IsBusy && !string.IsNullOrWhiteSpace(PackagePath);

    /// <summary>
    /// 刷入：按 <see cref="SelectedTarget"/> 决定走 9008 线刷还是 fastboot 刷写。
    /// 输入是目录 → 9008 整包；输入是镜像文件 → 单分区刷写。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFlashPackage))]
    private async Task FlashPackageAsync()
    {
        var path = PackagePath?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            Report("请先选择救砖包位置", failed: true);
            return;
        }

        if (SelectedTarget == FlashTarget.Edl)
        {
            await FlashViaEdlAsync(path).ConfigureAwait(true);
        }
        else
        {
            await FlashViaFastbootAsync(path).ConfigureAwait(true);
        }
    }

    /// <summary>9008 线刷：自动选引导文件、自动选存储、自动选串口。</summary>
    private async Task FlashViaEdlAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            Report("9008 刷写需要一个线刷包目录（含 rawprogram*.xml）", failed: true);
            return;
        }

        var profile = SelectedProfile;
        var taskName = $"9008 线刷 · {profile?.Model ?? path}";

        // 引导文件：界面填了就用，否则按机型自动找。
        var programmer = ProgrammerPath;
        if (string.IsNullOrWhiteSpace(programmer) || !File.Exists(programmer))
        {
            programmer = _tools.FindProgrammer(profile?.Product);
            if (programmer is null)
            {
                Report(
                    $"{profile?.Model ?? "当前机型"} 缺少 9008 引导文件（devprg）。" +
                    "请在界面上手动指定，或确认 Tools\\res\\<代号>\\devprg 存在。", failed: true);
                return;
            }

            ProgrammerPath = programmer;
            Report($"已自动选择引导文件：{Path.GetFileName(programmer)}");
        }

        var ok = await RunAsync(taskName, async token =>
        {
            var credentials = profile is { } p ? _credentials.Load(p.Product) : null;

            return await _flash.FlashRawPackageAsync(
                path, EdlPort, ResolveStorage(profile), programmer,
                credentials?.AuthCode, CreateFlashProgress(), token).ConfigureAwait(true);
        }).ConfigureAwait(true);

        ShowTaskComplete(taskName, ok);
    }

    /// <summary>Fastboot 刷写：单镜像按文件名猜分区名；目录则逐个刷。</summary>
    private async Task FlashViaFastbootAsync(string path)
    {
        if (File.Exists(path))
        {
            var partition = PartitionName;
            if (string.IsNullOrWhiteSpace(partition))
            {
                // 用文件名当分区名，这是最通行的约定（boot.img → boot）。
                partition = Path.GetFileNameWithoutExtension(path);
                // 去掉常见的 _a/_b 后缀与 -magisk 之类修饰。
                partition = partition.Split('_')[0].Split('-')[0];
                PartitionName = partition;
                Report($"已按文件名推断分区名：{partition}（可手动修改）");
            }

            var taskName = $"Fastboot 刷写 {partition}";
            var ok = await RunAsync(taskName, async token =>
            {
                var navigation = await EnsureFastbootAsync(token).ConfigureAwait(true);
                if (!navigation.Success)
                {
                    return FlashResult.Fail(navigation.Message);
                }

                return await _flash.FlashPartitionAsync(
                    partition!, path, navigation.Mode, EdlPort,
                    ResolveStorage(SelectedProfile), CreateFlashProgress(), token).ConfigureAwait(true);
            }).ConfigureAwait(true);

            ShowTaskComplete(taskName, ok);
            return;
        }

        if (Directory.Exists(path))
        {
            // 目录：所有 .img 逐个刷，文件名即分区名。
            var images = Directory.GetFiles(path, "*.img", SearchOption.TopDirectoryOnly);
            if (images.Length == 0)
            {
                Report("该目录下没有 .img 文件", failed: true);
                return;
            }

            var taskName = $"Fastboot 批量刷写 · {images.Length} 个镜像";
            var ok = await RunAsync(taskName, async token =>
            {
                var navigation = await EnsureFastbootAsync(token).ConfigureAwait(true);
                if (!navigation.Success)
                {
                    return FlashResult.Fail(navigation.Message);
                }

                var failed = new List<string>();
                for (var i = 0; i < images.Length; i++)
                {
                    if (token.IsCancellationRequested)
                    {
                        return FlashResult.Fail("已取消");
                    }

                    var image = images[i];
                    var partition = Path.GetFileNameWithoutExtension(image).Split('_')[0].Split('-')[0];

                    Report($"[{i + 1}/{images.Length}] 刷入 {partition}…");

                    var one = await _flash.FlashPartitionAsync(
                        partition, image, navigation.Mode, EdlPort,
                        ResolveStorage(SelectedProfile), CreateFlashProgress(), token).ConfigureAwait(true);

                    if (!one.Success)
                    {
                        failed.Add($"{partition}（{one.Message}）");
                    }
                }

                return failed.Count == 0
                    ? FlashResult.Ok($"全部 {images.Length} 个镜像刷写完成")
                    : FlashResult.Fail("以下分区刷写失败：" + string.Join("；", failed));
            }).ConfigureAwait(true);

            ShowTaskComplete(taskName, ok);
            return;
        }

        Report("路径不存在，请重新选择", failed: true);
    }

    private async Task<ModeNavigateResult> EnsureFastbootAsync(CancellationToken token)
    {
        Report("正在把设备切换到 Fastboot 模式…");
        var navigation = await _modes.EnsureModeAsync(
            ConnectionMode.Fastboot, EdlPort, ResolveStorage(SelectedProfile),
            CreateFlashProgress(), token).ConfigureAwait(true);

        if (navigation.Success)
        {
            await ReReadPortAsync(navigation.Mode).ConfigureAwait(true);
        }

        return navigation;
    }

    /// <summary>切换通路（由子窗口的两个按钮调用）。</summary>
    [RelayCommand]
    private async Task SetTargetAsync(string? target)
    {
        SelectedTarget = string.Equals(target, "fastboot", StringComparison.OrdinalIgnoreCase)
            ? FlashTarget.Fastboot
            : FlashTarget.Edl;

        IsTargetPickerOpen = false;
        Report($"刷写通路：{FlashTargetText}");

        // 选 9008 就把完整的 9008 刷写页打开，让用户在里面读分区表、刷整包。
        if (SelectedTarget == FlashTarget.Edl && _edl is not null)
        {
            await _edl.OpenAsync(SelectedProfile).ConfigureAwait(true);
        }
        else if (SelectedTarget == FlashTarget.Fastboot && _fastboot is not null)
        {
            await _fastboot.OpenAsync().ConfigureAwait(true);
        }
    }

    /// <summary>打开 / 关闭通路选择子窗口。</summary>
    [RelayCommand]
    private void ToggleTargetPicker() => IsTargetPickerOpen = !IsTargetPickerOpen;

    [RelayCommand]
    private void CloseTargetPicker() => IsTargetPickerOpen = false;

    // ------------------------------------------------------------------
    // 命令：其它刷写动作
    // ------------------------------------------------------------------

    private bool CanRunAction() => !IsBusy && SelectedProfile is not null;

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
            Report(
                $"{profile.Model} 是 A/B 分区设备，没有独立 recovery 分区。" +
                "请改用「刷入任意分区」，目标是 boot 或 init_boot。", failed: true);
            return;
        }

        var taskName = $"刷入 Recovery · {profile.Model}";
        var ok = await RunAsync(taskName, async token =>
        {
            return await _flash.FlashPartitionAsync(
                "recovery", RecoveryImagePath!, Connection, EdlPort,
                ResolveStorage(profile), CreateFlashProgress(), token).ConfigureAwait(true);
        }).ConfigureAwait(true);

        ShowTaskComplete(taskName, ok);
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

        var taskName = $"刷入分区 {PartitionName}";
        var ok = await RunAsync(taskName, async token =>
        {
            return await _flash.FlashPartitionAsync(
                PartitionName!.Trim(), PartitionImagePath!, Connection, EdlPort,
                ResolveStorage(SelectedProfile), CreateFlashProgress(), token).ConfigureAwait(true);
        }).ConfigureAwait(true);

        ShowTaskComplete(taskName, ok);
    }

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    private async Task SendFirehoseAsync()
    {
        if (string.IsNullOrWhiteSpace(ProgrammerPath) || !File.Exists(ProgrammerPath))
        {
            Report("请先选择 Firehose 引导文件（devprg）", failed: true);
            return;
        }

        var credentials = SelectedProfile is { } p ? _credentials.Load(p.Product) : null;

        var ok = await RunAsync("9008 发送引导", async token =>
        {
            return await _flash.SendFirehoseAsync(
                EdlPort, ProgrammerPath!, ResolveStorage(SelectedProfile),
                credentials?.AuthCode, CreateFlashProgress(), token).ConfigureAwait(true);
        }).ConfigureAwait(true);

        if (!ok)
        {
            Report("发送引导失败，请检查端口与授权码", failed: true);
        }
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
            await ReReadPortAsync(info.Mode).ConfigureAwait(true);

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

        // 换机型后清掉旧的引导文件，让它按新机型重新推导。
        ProgrammerPath = null;

        // 换了机型就把该机型已保存的资料回填到输入框 —— 用户不必每次重新粘贴。
        LoadCredentialsIntoInputs(value);
    }

    /// <summary>
    /// 把已保存的解锁码/授权码读进输入框。
    /// <para>
    /// 参考实现是每次解锁时从 <c>res\%product%\bak\blunlockcode.txt</c> 现读；
    /// 我们多走一步，把内容显示出来，用户能直观看到"这台的资料已经在了"。
    /// </para>
    /// </summary>
    private void LoadCredentialsIntoInputs(DeviceProfile? profile)
    {
        if (profile is null)
        {
            UnlockCodeInput = null;
            AuthCodeInput = null;
            return;
        }

        try
        {
            var stored = _credentials.Load(profile.Product);
            UnlockCodeInput = stored.UnlockCode;
            AuthCodeInput = stored.AuthCode;
        }
        catch (Exception ex)
        {
            _logger.Warn($"读取 {profile.Product} 的解锁资料失败：{ex.Message}");
            UnlockCodeInput = null;
            AuthCodeInput = null;
        }
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    /// <summary>把 <see cref="ConnectionMode"/> 对应的端口写进 <see cref="EdlPort"/>。</summary>
    private async Task ReReadPortAsync(ConnectionMode mode)
    {
        if (mode != ConnectionMode.QcEdl)
        {
            return;
        }

        try
        {
            var info = await _devices.ProbeAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(info.SerialOrPort))
            {
                EdlPort = info.SerialOrPort
                    .Replace("COM", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"读取 9008 端口失败：{ex.Message}");
        }
    }

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

            if (p.Message.Length > 0 && !p.Message.Contains('%'))
            {
                Append(p.Message);
            }
        }));

    /// <summary>任务结束后弹「完成任务提示」。返回是否成功。</summary>
    private void ShowTaskComplete(string taskName, bool success)
    {
        if (_taskComplete is null)
        {
            return;
        }

        _taskComplete.Port = EdlPort;
        _taskComplete.Memory = ResolveStorage(SelectedProfile);

        if (success)
        {
            _taskComplete.Show(taskName, TaskMessage);
        }
        else
        {
            _taskComplete.ShowFailure(taskName, TaskMessage);
        }
    }

    /// <summary>统一的动作包装：返回是否成功，供收尾提示判断。</summary>
    private async Task<bool> RunAsync(string title, Func<CancellationToken, Task<FlashResult>> action)
    {
        if (IsBusy)
        {
            return false;
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
            return result.Success;
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
            return false;
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
            return false;
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

        // 资料变了，解锁按钮的可用性与提示文案都要跟着变。
        OnPropertyChanged(nameof(UnlockRequiresModeText));
        OnPropertyChanged(nameof(CanUnlockNow));
        UnlockCommand.NotifyCanExecuteChanged();
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
