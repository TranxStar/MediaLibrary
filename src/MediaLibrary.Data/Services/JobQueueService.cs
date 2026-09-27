using System.Collections.Concurrent;
using System.Diagnostics;
using MediaLibrary.Core.Models;
using MediaLibrary.Data.Repositories;

namespace MediaLibrary.Data.Services;

/// <summary>
/// 任务队列的控制中枢（阶段 4，设计文档 §7.2）。
///
/// 职责：
/// <list type="bullet">
///   <item><b>全局暂停/继续</b> —— 暂停时：所有排队任务标记为 Paused，执行器在
///         开始新任务前会在此等待；运行中的任务优雅跑完（不打断 ffmpeg 半截输出）。</item>
///   <item><b>单个任务取消</b> —— 先改库，再打断对应 worker 的取消令牌，
///         处理器会清理 .tmp 并把素材退回 Pending。</item>
///   <item><b>入队路由</b> —— 暂停状态下新入队的任务直接进 Paused，不会绕过暂停。</item>
/// </list>
/// </summary>
public sealed class JobQueueService
{
    private readonly JobRepository _jobs;

    /// <summary>正在执行的任务及其取消令牌（用于"取消单个任务"）。</summary>
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();

    private volatile bool _isPaused;

    public JobQueueService(JobRepository jobs)
    {
        _jobs = jobs;
    }

    /// <summary>队列是否处于全局暂停。</summary>
    public bool IsPaused => _isPaused;

    /// <summary>暂停状态变化（界面与主流程据此刷新/续跑）。</summary>
    public event Action? PauseStateChanged;

    /// <summary>暂停全部：排队 → Paused；运行中的继续跑完。</summary>
    public async Task PauseAsync(CancellationToken ct = default)
    {
        if (_isPaused) return;

        _isPaused = true;
        var paused = await _jobs.PauseAllQueuedAsync(ct).ConfigureAwait(false);
        if (paused > 0)
            Note($"[队列] 暂停 → {paused} 个排队任务转为 Paused");

        PauseStateChanged?.Invoke();
    }

    /// <summary>继续全部：Paused → Queued。调用方随后会触发执行器续跑。</summary>
    public async Task ResumeAsync(CancellationToken ct = default)
    {
        if (!_isPaused) return;

        // ★ 先恢复数据库状态，再放行等待中的 worker ——
        //   否则 worker 醒来时任务还是 Paused，MarkRunning 会因
        //   "仅 Queued 可开始"而跳过它（任务只能等下一轮执行）。
        var resumed = await _jobs.ResumeAllPausedAsync(ct).ConfigureAwait(false);

        _isPaused = false;

        if (resumed > 0)
            Note($"[队列] 继续 → {resumed} 个暂停任务转为 Queued");

        PauseStateChanged?.Invoke();
    }

    /// <summary>
    /// 执行器在开始处理每个任务前调用：暂停期间在此等待，直到继续或取消。
    /// </summary>
    public async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        while (_isPaused && !ct.IsCancellationRequested)
            await Task.Delay(200, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>注册一个正在执行的任务，返回可被"取消该任务"打断的令牌。</summary>
    public CancellationToken RegisterRunning(long jobId, CancellationToken outer)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _running[jobId] = cts;
        return cts.Token;
    }

    /// <summary>注销执行结束的任务。</summary>
    public void UnregisterRunning(long jobId)
    {
        if (_running.TryRemove(jobId, out var cts))
        {
            try { cts.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>取消单个任务：先改库，再打断正在执行的 worker。</summary>
    public async Task CancelJobAsync(long jobId, CancellationToken ct = default)
    {
        await _jobs.CancelAsync([jobId], ct).ConfigureAwait(false);

        if (_running.TryGetValue(jobId, out var cts))
        {
            try { cts.Cancel(); } catch (Exception) { }
        }

        Note($"[队列] 取消任务 #{jobId}");
    }

    /// <summary>批量取消（队列面板"取消全部"）。</summary>
    public async Task CancelJobsAsync(IReadOnlyCollection<long> jobIds, CancellationToken ct = default)
    {
        if (jobIds.Count == 0) return;

        await _jobs.CancelAsync(jobIds, ct).ConfigureAwait(false);

        foreach (var id in jobIds)
        {
            if (_running.TryGetValue(id, out var cts))
            {
                try { cts.Cancel(); } catch (Exception) { }
            }
        }

        Note($"[队列] 批量取消 {jobIds.Count} 个任务");
    }

    /// <summary>失败任务重新排队。</summary>
    public Task RetryAsync(IReadOnlyCollection<long> jobIds, CancellationToken ct = default)
        => _jobs.RetryAsync(jobIds, ct);

    /// <summary>入队（尊重暂停状态：暂停时直接进 Paused）。</summary>
    public async Task<long> EnqueueAsync(long assetId, JobType jobType, int priority = 0, CancellationToken ct = default)
    {
        var id = await _jobs.EnqueueAsync(assetId, jobType, priority, ct).ConfigureAwait(false);

        if (_isPaused)
            await _jobs.PauseAsync([id], ct).ConfigureAwait(false);

        return id;
    }

    /// <summary>批量入队（导入管线用；暂停状态下整批标记为 Paused）。</summary>
    public async Task EnqueueManyAsync(
        IReadOnlyList<(long AssetId, JobType JobType, int Priority)> jobs,
        CancellationToken ct = default)
    {
        if (jobs.Count == 0) return;

        await _jobs.EnqueueManyAsync(jobs, ct).ConfigureAwait(false);

        if (_isPaused)
        {
            // 入队接口不返回 Id，直接用"该批素材的未完成任务"来暂停
            var assetIds = jobs.Select(j => j.AssetId).Distinct().ToList();
            await _jobs.PauseAsync(
                (await GetActiveJobIdsForAssetsAsync(assetIds, ct).ConfigureAwait(false)),
                ct).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyCollection<long>> GetActiveJobIdsForAssetsAsync(
        IReadOnlyCollection<long> assetIds, CancellationToken ct)
    {
        var all = new List<long>();

        foreach (var assetId in assetIds)
        {
            var jobs = await _jobs.GetByAssetAsync(assetId, ct).ConfigureAwait(false);
            all.AddRange(jobs.Where(j => j.Status == JobStatus.Queued).Select(j => j.Id));
        }

        return all;
    }

    /// <summary>清除已结束的任务（完成/取消）。</summary>
    public Task<int> ClearFinishedAsync(CancellationToken ct = default)
        => _jobs.DeleteFinishedAsync(ct);

    /// <summary>删除某批素材名下的全部任务。</summary>
    public Task DeleteForAssetsAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
        => _jobs.DeleteForAssetsAsync(assetIds, ct);

    private static void Note(string message) => Debug.WriteLine(message);
}
