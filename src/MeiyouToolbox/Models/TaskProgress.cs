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

/// <summary>一次可长跑的操作（刷写 / 备份 / 回读 …）。</summary>
public sealed class TaskProgress
{
    public static TaskProgress Idle { get; } = new();

    public string Title { get; init; } = string.Empty;

    /// <summary>0-100。未知进度时为 <see cref="double.NaN"/>，界面显示为不确定态。</summary>
    public double Percent { get; init; }

    public TaskState State { get; init; }

    public string? Message { get; init; }

    public bool IsRunning => State == TaskState.Running;

    public bool IsIndeterminate => double.IsNaN(Percent);
}
