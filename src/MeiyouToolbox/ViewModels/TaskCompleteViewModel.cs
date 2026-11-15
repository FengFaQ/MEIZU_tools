using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeiyouToolbox.Models;
using MeiyouToolbox.Services;

namespace MeiyouToolbox.ViewModels;

/// <summary>
/// 「完成任务提示」—— 任何任务结束时都能调用同一套收尾界面。
///
/// 用法：把本 VM 注入到需要收尾的页面 VM，任务结束后调
/// <see cref="Show"/> / <see cref="ShowFailure"/> 即可，
/// 界面由 <c>MainView</c> 顶层的遮罩渲染，不需要每个页面各写一份。
/// </summary>
public partial class TaskCompleteViewModel : ObservableObject
{
    private readonly IPostTaskService _postTask;
    private readonly IAppLogger _logger;

    private CancellationTokenSource? _cts;

    /// <summary>设计期 / XAML 内实例化用的无参构造。</summary>
    public TaskCompleteViewModel()
    {
        _postTask = null!;
        _logger = null!;
    }

    public TaskCompleteViewModel(IPostTaskService postTask, IAppLogger logger)
    {
        _postTask = postTask;
        _logger = logger;
    }

    [ObservableProperty]
    private bool _isVisible;

    /// <summary>大字标题，固定为"任务完成"或"任务失败"。</summary>
    [ObservableProperty]
    private string _title = "任务完成";

    /// <summary>本次执行的任务名，例如"解锁 Bootloader"。</summary>
    [ObservableProperty]
    private string _taskName = string.Empty;

    /// <summary>执行状态：完成 / 失败。</summary>
    [ObservableProperty]
    private string _statusText = "完成";

    /// <summary>成功为 true，用于给标题与状态换色。</summary>
    [ObservableProperty]
    private bool _isSuccess = true;

    /// <summary>详细说明，失败时是失败原因。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string _detail = string.Empty;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>收尾动作执行中（两个按钮此时禁用）。</summary>
    [ObservableProperty]
    private bool _isWorking;

    [ObservableProperty]
    private string _workMessage = string.Empty;

    /// <summary>收尾动作的失败信息（成功后置空）。</summary>
    [ObservableProperty]
    private string _actionError = string.Empty;

    [ObservableProperty]
    private bool _hasActionError;

    /// <summary>当前设备端口，由调用方提供，供 9008 擦除 userdata 用。</summary>
    public string? Port { get; set; }

    public StorageKind Memory { get; set; } = StorageKind.Auto;

    /// <summary>显示一条成功提示。</summary>
    public void Show(string taskName, string? detail = null)
    {
        Post(() =>
        {
            IsSuccess = true;
            Title = "任务完成";
            StatusText = "完成";
            TaskName = taskName;
            Detail = detail ?? string.Empty;
            ActionError = string.Empty;
            HasActionError = false;
            WorkMessage = string.Empty;
            IsWorking = false;
            IsVisible = true;
        });
    }

    /// <summary>显示一条失败提示。</summary>
    public void ShowFailure(string taskName, string reason)
    {
        Post(() =>
        {
            IsSuccess = false;
            Title = "任务失败";
            StatusText = "失败";
            TaskName = taskName;
            Detail = reason;
            ActionError = string.Empty;
            HasActionError = false;
            WorkMessage = string.Empty;
            IsWorking = false;
            IsVisible = true;
        });
    }

    [RelayCommand]
    private void Close()
    {
        _cts?.Cancel();
        IsVisible = false;
    }

    [RelayCommand]
    private Task WipeDataAndBoot() => RunPostActionAsync(wiping: true);

    [RelayCommand]
    private Task Boot() => RunPostActionAsync(wiping: false);

    private async Task RunPostActionAsync(bool wiping)
    {
        if (IsWorking)
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        IsWorking = true;
        HasActionError = false;
        ActionError = string.Empty;
        WorkMessage = wiping ? "正在清空数据…" : "正在开机…";

        var progress = new Progress<FlashProgress>(p =>
            Post(() => WorkMessage = string.IsNullOrWhiteSpace(p.Message) ? WorkMessage : p.Message));

        try
        {
            var result = wiping
                ? await _postTask.WipeDataAndBootAsync(Port, Memory, progress, _cts.Token)
                    .ConfigureAwait(true)
                : await _postTask.BootAsync(Port, Memory, progress, _cts.Token)
                    .ConfigureAwait(true);

            Post(() =>
            {
                IsWorking = false;

                if (result.Success)
                {
                    WorkMessage = result.Message;
                    IsVisible = false;
                }
                else
                {
                    ActionError = result.Message;
                    HasActionError = true;
                    WorkMessage = string.Empty;
                }
            });

            if (!result.Success)
            {
                _logger?.Warn($"完成任务提示的收尾动作失败：{result.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            Post(() =>
            {
                IsWorking = false;
                WorkMessage = string.Empty;
            });
        }
        catch (Exception ex)
        {
            _logger?.Error("完成任务提示的收尾动作异常", ex);
            Post(() =>
            {
                IsWorking = false;
                ActionError = ex.Message;
                HasActionError = true;
                WorkMessage = string.Empty;
            });
        }
    }

    /// <summary>保证属性变更发生在 UI 线程，避免跨线程访问导致 0xe0434352。</summary>
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
