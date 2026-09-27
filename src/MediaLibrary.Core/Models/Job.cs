namespace MediaLibrary.Core.Models;

/// <summary>后台任务记录，对应 Jobs 表。</summary>
public sealed class Job
{
    public long Id { get; set; }

    public long AssetId { get; set; }

    public JobType JobType { get; set; }

    public JobStatus Status { get; set; }

    /// <summary>0.0 ~ 1.0。</summary>
    public double Progress { get; set; }

    /// <summary>已尝试次数。达到上限后不再自动重试。</summary>
    public int Attempts { get; set; }

    /// <summary>数值越大越先执行。</summary>
    public int Priority { get; set; }

    public string? ErrorMessage { get; set; }

    public string CreatedAt { get; set; } = "";
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }

    /// <summary>关联素材的文件名（JOIN 查询填充，队列界面显示用；不落库）。</summary>
    public string? AssetName { get; set; }
}

/// <summary>任务队列的数量概览，用于界面显示。</summary>
public sealed record JobCounts(
    int Queued,
    int Running,
    int Done,
    int Failed,
    int Paused,
    int Canceled)
{
    public int Pending => Queued + Running;
    public int Total => Queued + Running + Done + Failed + Paused + Canceled;
}
