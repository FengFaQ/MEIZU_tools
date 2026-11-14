namespace MeiyouToolbox.Models;

/// <summary>任务执行状态：用于概览页的"当前任务"。</summary>
public enum TaskState
{
    Idle,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// 一次可长跑的操作（刷写 / 备份 / 回读 …）。
/// <para>
/// 注意：类型名刻意不叫 <c>TaskProgress</c>，避免与 <see cref="System.Threading.Tasks.Task"/> 混淆。
/// 早期命名为 TaskProgress 时，同命名空间内的 `Task.Run(...)` 会被解析到这个类上导致 CS1061。
/// </para>
/// </summary>
public sealed class BackgroundTaskProgress
{
    public static BackgroundTaskProgress Idle { get; } = new();

    public string Title { get; init; } = string.Empty;

    /// <summary>0-100。未知进度时为 <see cref="double.NaN"/>，界面显示为不确定态。</summary>
    public double Percent { get; init; }

    public TaskState State { get; init; }

    public string? Message { get; init; }

    public bool IsRunning => State == TaskState.Running;

    public bool IsIndeterminate => double.IsNaN(Percent);
}
