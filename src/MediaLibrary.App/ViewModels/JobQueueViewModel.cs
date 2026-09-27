using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaLibrary.Core.Models;
using MediaLibrary.Data.Repositories;
using MediaLibrary.Data.Services;

namespace MediaLibrary.App.ViewModels;

/// <summary>队列面板里的一行任务。</summary>
public partial class JobItemViewModel : ObservableObject
{
    public JobItemViewModel(Job job)
    {
        Id = job.Id;
        AssetId = job.AssetId;
        AssetName = job.AssetName ?? $"(素材 #{job.AssetId})";
        UpdateFrom(job);
    }

    public long Id { get; }

    public long AssetId { get; }

    public string AssetName { get; }

    [ObservableProperty] private JobStatus _status;

    [ObservableProperty] private double _progress;

    [ObservableProperty] private int _attempts;

    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private string _typeText = "";

    [ObservableProperty] private string? _errorMessage;

    public bool IsActive => Status is JobStatus.Queued or JobStatus.Running or JobStatus.Paused;
    public bool CanCancel => IsActive;
    public bool CanRetry => Status == JobStatus.Failed;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnStatusChanged(JobStatus value)
    {
        StatusText = value switch
        {
            JobStatus.Queued => "排队中",
            JobStatus.Running => "处理中",
            JobStatus.Paused => "已暂停",
            JobStatus.Done => "已完成",
            JobStatus.Failed => "失败",
            JobStatus.Canceled => "已取消",
            _ => value.ToString(),
        };

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
    }

    public void UpdateFrom(Job job)
    {
        Status = job.Status;
        Progress = job.Progress;
        Attempts = job.Attempts;
        ErrorMessage = job.ErrorMessage;
        TypeText = job.JobType switch
        {
            JobType.TranscodeVideo => "转码视频",
            JobType.ConvertPhoto => "转换照片",
            JobType.CopyAudio => "复制音频",
            JobType.Thumbnail => "缩略图",
            _ => job.JobType.ToString(),
        };
    }
}

/// <summary>
/// 任务队列面板（阶段 4）。
///
/// 每秒轮询一次数据库快照（最近 60 条 + 计数），在内存里按 Id 对齐更新，
/// 避免整表重建导致的闪烁。所有按钮都走 <see cref="JobQueueService"/>。
/// </summary>
public partial class JobQueueViewModel : ObservableObject
{
    private readonly JobQueueService _queue;
    private readonly JobRepository _jobs;
    private readonly DispatcherTimer _pollTimer;

    public JobQueueViewModel(JobQueueService queue, JobRepository jobs)
    {
        _queue = queue;
        _jobs = jobs;

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(800),
        };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _pollTimer.Start();
    }

    /// <summary>队列有新任务可执行时通知（重试/继续后触发主流程跑队列）。</summary>
    public event Action? WorkRequested;

    public ObservableCollection<JobItemViewModel> Rows { get; } = [];

    [ObservableProperty] private bool _isPanelOpen;

    [ObservableProperty] private bool _isPaused;

    [ObservableProperty] private string _badgeText = "任务队列";

    [ObservableProperty] private string _summaryText = "没有任务";

    /// <summary>0.0 ~ 1.0，活动任务的平均进度（面板底部总进度条）。</summary>
    [ObservableProperty] private double _overallProgress;

    [ObservableProperty] private bool _hasActiveJobs;

    [RelayCommand]
    private void TogglePanel() => IsPanelOpen = !IsPanelOpen;

    [RelayCommand]
    private async Task PauseAllAsync()
    {
        await _queue.PauseAsync();
        IsPaused = true;
    }

    [RelayCommand]
    private async Task ResumeAllAsync()
    {
        await _queue.ResumeAsync();
        IsPaused = false;
        WorkRequested?.Invoke();
    }

    [RelayCommand]
    private async Task CancelAllAsync()
    {
        var ids = Rows.Where(r => r.IsActive).Select(r => r.Id).ToList();
        await _queue.CancelJobsAsync(ids);
    }

    [RelayCommand]
    private async Task ClearFinishedAsync()
    {
        await _queue.ClearFinishedAsync();
        await PollAsync();
    }

    [RelayCommand]
    private async Task CancelJobAsync(JobItemViewModel? row)
    {
        if (row is null) return;
        await _queue.CancelJobAsync(row.Id);
    }

    [RelayCommand]
    private async Task RetryJobAsync(JobItemViewModel? row)
    {
        if (row is null) return;

        await _queue.RetryAsync([row.Id]);
        WorkRequested?.Invoke();
    }

    private readonly Dictionary<long, JobItemViewModel> _rowsById = [];

    private async Task PollAsync()
    {
        try
        {
            var recent = await _jobs.GetRecentAsync(60);
            var counts = await _jobs.GetCountsAsync();

            IsPaused = _queue.IsPaused;
            HasActiveJobs = counts.Pending > 0 || counts.Paused > 0;

            // 活动任务排前面，其余按时间倒序
            var ordered = recent
                .OrderByDescending(j => j.Status is JobStatus.Queued or JobStatus.Running or JobStatus.Paused)
                .ThenByDescending(j => j.Id)
                .ToList();

            // 按 Id 对齐更新，避免重建集合
            var seen = new HashSet<long>();
            var index = 0;

            foreach (var job in ordered)
            {
                seen.Add(job.Id);

                if (_rowsById.TryGetValue(job.Id, out var row))
                {
                    row.UpdateFrom(job);
                    MoveTo(Rows, row, index);
                }
                else
                {
                    row = new JobItemViewModel(job);
                    _rowsById[job.Id] = row;
                    Rows.Insert(Math.Min(index, Rows.Count), row);
                }

                index++;
            }

            for (var i = Rows.Count - 1; i >= 0; i--)
            {
                if (seen.Contains(Rows[i].Id)) continue;

                _rowsById.Remove(Rows[i].Id);
                Rows.RemoveAt(i);
            }

            // 汇总文案与总进度
            if (counts.Total == 0)
            {
                SummaryText = "没有任务 —— 拖入素材或重新转码后，任务会显示在这里";
                OverallProgress = 0;
            }
            else
            {
                var active = counts.Queued + counts.Running + counts.Paused;
                var parts = new List<string>();

                if (active > 0) parts.Add($"待处理 {active}");
                if (counts.Running > 0) parts.Add($"进行中 {counts.Running}");
                if (counts.Paused > 0) parts.Add($"已暂停 {counts.Paused}");
                if (counts.Failed > 0) parts.Add($"失败 {counts.Failed}");
                if (counts.Done > 0) parts.Add($"已完成 {counts.Done}");

                SummaryText = string.Join(" · ", parts);

                var activeRows = Rows.Where(r => r.IsActive).ToList();
                OverallProgress = activeRows.Count > 0
                    ? activeRows.Average(r => r.Status == JobStatus.Running ? Math.Clamp(r.Progress, 0, 1) : 0)
                    : 0;
            }

            BadgeText = HasActiveJobs
                ? $"任务队列 · {counts.Queued + counts.Running + counts.Paused}"
                : "任务队列";
        }
        catch (Exception)
        {
            // 轮询失败下一轮再试，不影响界面
        }
    }

    private static void MoveTo(ObservableCollection<JobItemViewModel> list, JobItemViewModel row, int index)
    {
        var current = list.IndexOf(row);
        if (current < 0 || current == index) return;

        list.Move(current, Math.Min(index, list.Count - 1));
    }
}
