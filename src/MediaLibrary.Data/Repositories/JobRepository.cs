using Dapper;
using MediaLibrary.Core.Models;

namespace MediaLibrary.Data.Repositories;

/// <summary>Jobs 表读写（设计文档 §4.1 / §7.2）。</summary>
public sealed class JobRepository
{
    private const string SelectColumns = """
        Id, AssetId, JobType, Status, Progress, Attempts, Priority,
        ErrorMessage, CreatedAt, StartedAt, FinishedAt
        """;

    private readonly LibraryDb _db;

    public JobRepository(LibraryDb db)
    {
        DapperSetup.EnsureInitialized();
        _db = db;
    }

    /// <summary>入队一个任务。</summary>
    public Task<long> EnqueueAsync(long assetId, JobType jobType, int priority = 0, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO Jobs (AssetId, JobType, Status, Progress, Attempts, Priority, CreatedAt)
                VALUES ($assetId, $jobType, 'Queued', 0, 0, $priority, $now);
                SELECT last_insert_rowid();
                """,
                new
                {
                    assetId,
                    jobType = jobType.ToString(),
                    priority,
                    now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                },
                tx, cancellationToken: ct)).ConfigureAwait(false);

            return id;
        }, ct);

    /// <summary>批量入队。整批放在一个事务里，几百个文件也是一次提交。</summary>
    public Task EnqueueManyAsync(
        IReadOnlyList<(long AssetId, JobType JobType, int Priority)> jobs,
        CancellationToken ct = default)
    {
        if (jobs.Count == 0) return Task.CompletedTask;

        return _db.WriteAsync(async (conn, tx) =>
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            foreach (var (assetId, jobType, priority) in jobs)
            {
                ct.ThrowIfCancellationRequested();

                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO Jobs (AssetId, JobType, Status, Progress, Attempts, Priority, CreatedAt)
                    VALUES ($assetId, $jobType, 'Queued', 0, 0, $priority, $now);
                    """,
                    new { assetId, jobType = jobType.ToString(), priority, now },
                    tx, cancellationToken: ct)).ConfigureAwait(false);
            }
        }, ct);
    }

    /// <summary>取出全部排队中的任务，按优先级与入队顺序排列。</summary>
    public Task<IReadOnlyList<Job>> GetQueuedAsync(CancellationToken ct = default)
        => _db.ReadAsync<IReadOnlyList<Job>>(async conn =>
            (await conn.QueryAsync<Job>(new CommandDefinition(
                $"SELECT {SelectColumns} FROM Jobs WHERE Status = 'Queued' " +
                "ORDER BY Priority DESC, Id;",
                cancellationToken: ct)).ConfigureAwait(false)).ToList(), ct);

    public Task<IReadOnlyList<Job>> GetByAssetAsync(long assetId, CancellationToken ct = default)
        => _db.ReadAsync<IReadOnlyList<Job>>(async conn =>
            (await conn.QueryAsync<Job>(new CommandDefinition(
                $"SELECT {SelectColumns} FROM Jobs WHERE AssetId = $assetId ORDER BY Id;",
                new { assetId }, cancellationToken: ct)).ConfigureAwait(false)).ToList(), ct);

    /// <summary>
    /// 标记开始执行。只在任务仍为 Queued 时生效，返回受影响行数 ——
    /// 若任务在等待期间被取消/暂停（行数 0），执行器应跳过它。
    /// </summary>
    public Task<int> MarkRunningAsync(long jobId, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            return await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Jobs SET Status = 'Running', StartedAt = $now, Attempts = Attempts + 1
                WHERE Id = $jobId AND Status = 'Queued';
                """,
                new { jobId, now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    public Task MarkDoneAsync(long jobId, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Jobs SET Status = 'Done', Progress = 1, FinishedAt = $now, ErrorMessage = NULL
                WHERE Id = $jobId;
                """,
                new { jobId, now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    /// <summary>标记失败。未达尝试上限的任务会退回队列等待重试。</summary>
    public Task MarkFailedAsync(long jobId, string error, int maxAttempts, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            // 用一条 SQL 决定"重排队"还是"终态失败"，避免读-改-写竞态
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Jobs
                SET Status = CASE WHEN Attempts < $maxAttempts THEN 'Queued' ELSE 'Failed' END,
                    ErrorMessage = $error,
                    Progress = 0,
                    StartedAt = NULL,
                    FinishedAt = CASE WHEN Attempts < $maxAttempts THEN NULL ELSE $now END
                WHERE Id = $jobId;
                """,
                new { jobId, error, maxAttempts, now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    /// <summary>更新进度。界面每秒最多回写几次，不要每帧都写。</summary>
    public Task UpdateProgressAsync(long jobId, double progress, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE Jobs SET Progress = $progress WHERE Id = $jobId;",
                new { jobId, progress }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// 崩溃恢复：把上次中断时停留在 Running 的任务退回队列（设计文档 §5.1）。
    /// 应用启动时必须调用一次。
    /// </summary>
    public Task<int> RecoverInterruptedAsync(CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Jobs
                SET Status = 'Queued', Progress = 0, StartedAt = NULL
                WHERE Status = 'Running';
                """, transaction: tx, cancellationToken: ct)).ConfigureAwait(false), ct);

    /// <summary>把与素材状态不一致的任务清理掉（素材已就绪就不该再有任务）。</summary>
    public Task<int> PruneCompletedForReadyAssetsAsync(CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
            await conn.ExecuteAsync(new CommandDefinition("""
                DELETE FROM Jobs
                WHERE Status IN ('Queued', 'Running')
                  AND AssetId IN (SELECT Id FROM Assets WHERE Status = 'Ready');
                """, transaction: tx, cancellationToken: ct)).ConfigureAwait(false), ct);

    public Task<JobCounts> GetCountsAsync(CancellationToken ct = default)
        => _db.ReadAsync(async conn =>
        {
            var rows = (await conn.QueryAsync<(string Status, int Cnt)>(new CommandDefinition(
                "SELECT Status, COUNT(*) AS Cnt FROM Jobs GROUP BY Status;",
                cancellationToken: ct)).ConfigureAwait(false))
                .ToDictionary(r => r.Status, r => r.Cnt, StringComparer.OrdinalIgnoreCase);

            return new JobCounts(
                rows.GetValueOrDefault("Queued"),
                rows.GetValueOrDefault("Running"),
                rows.GetValueOrDefault("Done"),
                rows.GetValueOrDefault("Failed"),
                rows.GetValueOrDefault("Paused"),
                rows.GetValueOrDefault("Canceled"));
        }, ct);

    /// <summary>把某个素材的未完成任务全部作废（用于"取消导入"）。</summary>
    public Task CancelForAssetsAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return Task.CompletedTask;

        return _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Jobs SET Status = 'Canceled', FinishedAt = $now
                WHERE AssetId IN @assetIds AND Status IN ('Queued', 'Running');
                """,
                new { assetIds, now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>让失败的任务重新排队（用户点"重试"）。</summary>
    public Task RetryAsync(IReadOnlyCollection<long> jobIds, CancellationToken ct = default)
    {
        if (jobIds.Count == 0) return Task.CompletedTask;

        return _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Jobs SET Status = 'Queued', Attempts = 0, Progress = 0,
                                ErrorMessage = NULL, StartedAt = NULL, FinishedAt = NULL
                WHERE Id IN @jobIds;
                """, new { jobIds }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);
    }

    // ══════════════════════════ 阶段 4：队列控制 ══════════════════════════

    /// <summary>暂停：排队中的任务改为 Paused（运行中的继续跑完，语义上不打半截）。</summary>
    public Task<int> PauseAsync(IReadOnlyCollection<long> jobIds, CancellationToken ct = default)
    {
        if (jobIds.Count == 0) return Task.FromResult(0);

        return _db.WriteAsync(async (conn, tx) =>
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Jobs SET Status = 'Paused'
                WHERE Id IN @jobIds AND Status = 'Queued';
                """, new { jobIds }, tx, cancellationToken: ct)).ConfigureAwait(false), ct);
    }

    /// <summary>全部暂停：所有排队任务 → Paused。</summary>
    public Task<int> PauseAllQueuedAsync(CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE Jobs SET Status = 'Paused' WHERE Status = 'Queued';",
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false), ct);

    /// <summary>继续：Paused → Queued。</summary>
    public Task<int> ResumeAsync(IReadOnlyCollection<long> jobIds, CancellationToken ct = default)
    {
        if (jobIds.Count == 0) return Task.FromResult(0);

        return _db.WriteAsync(async (conn, tx) =>
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Jobs SET Status = 'Queued'
                WHERE Id IN @jobIds AND Status = 'Paused';
                """, new { jobIds }, tx, cancellationToken: ct)).ConfigureAwait(false), ct);
    }

    /// <summary>全部继续：所有 Paused → Queued。</summary>
    public Task<int> ResumeAllPausedAsync(CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE Jobs SET Status = 'Queued' WHERE Status = 'Paused';",
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false), ct);

    /// <summary>
    /// 取消任务：排队/暂停/运行中的任务 → Canceled。
    /// 运行中的任务由执行器感知（JobQueueService 会打断对应的 worker）。
    /// </summary>
    public Task<int> CancelAsync(IReadOnlyCollection<long> jobIds, CancellationToken ct = default)
    {
        if (jobIds.Count == 0) return Task.FromResult(0);

        return _db.WriteAsync(async (conn, tx) =>
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Jobs SET Status = 'Canceled', FinishedAt = $now
                WHERE Id IN @jobIds AND Status IN ('Queued', 'Paused', 'Running');
                """, new { jobIds, now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                tx, cancellationToken: ct)).ConfigureAwait(false), ct);
    }

    /// <summary>清除已结束的任务（完成/取消），失败的任务保留以便用户重试。</summary>
    public Task<int> DeleteFinishedAsync(CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM Jobs WHERE Status IN ('Done', 'Canceled');",
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false), ct);

    /// <summary>删除某批素材名下的全部任务（素材被删除时清理队列）。</summary>
    public Task DeleteForAssetsAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return Task.CompletedTask;

        return _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM Jobs WHERE AssetId IN @assetIds;",
                new { assetIds }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>队列面板快照：最近 N 条任务（连带素材文件名）。</summary>
    public Task<IReadOnlyList<Job>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
        => _db.ReadAsync<IReadOnlyList<Job>>(async conn =>
        {
            var safeLimit = Math.Clamp(limit, 1, 500);

            return (await conn.QueryAsync<Job>(new CommandDefinition("""
                SELECT j.Id, j.AssetId, j.JobType, j.Status, j.Progress, j.Attempts,
                       j.Priority, j.ErrorMessage, j.CreatedAt, j.StartedAt, j.FinishedAt,
                       a.FileName AS AssetName
                FROM Jobs j
                LEFT JOIN Assets a ON a.Id = j.AssetId
                ORDER BY j.Id DESC
                LIMIT $limit;
                """,
                new { limit = safeLimit }, cancellationToken: ct)).ConfigureAwait(false))
                .ToList();
        }, ct);
}
