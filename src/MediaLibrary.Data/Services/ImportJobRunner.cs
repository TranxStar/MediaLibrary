using System.Diagnostics;
using MediaLibrary.Core;
using MediaLibrary.Core.Abstractions;
using MediaLibrary.Core.Models;
using MediaLibrary.Data.Repositories;

namespace MediaLibrary.Data.Services;

/// <summary>一次任务队列执行的结果。</summary>
public sealed record JobRunStats(int Total, int Succeeded, int Failed, TimeSpan Elapsed)
{
    public static readonly JobRunStats Empty = new(0, 0, 0, TimeSpan.Zero);

    public override string ToString()
        => $"处理 {Total} 项（成功 {Succeeded} / 失败 {Failed}），耗时 {Elapsed.TotalSeconds:F1}s";
}

/// <summary>
/// 导入任务执行器（设计文档 §5.1 第 2~3 步）。
///
/// 核心保证：
/// <list type="bullet">
///   <item><b>原子落盘</b> —— 处理器只写 .tmp，成功后才 File.Move 到最终路径。
///         中途崩溃/断电，库里绝不会出现半截文件。</item>
///   <item><b>源文件零改动</b> —— 只读源文件，从不写入或删除。</item>
///   <item><b>崩溃可续跑</b> —— 启动时把 Running 任务退回队列。</item>
/// </list>
/// </summary>
public sealed class ImportJobRunner(
    LibraryLayout layout,
    AssetRepository assets,
    JobRepository jobs,
    JobQueueService queue,
    IMediaProbe probe,
    IVideoTranscoder transcoder,
    IImageConverter imageConverter,
    IFileCopier copier)
{
    /// <summary>失败重试上限。达到后任务进入终态，不再自动重试。</summary>
    public const int MaxAttempts = 3;

    /// <summary>转码 worker 并发。NVENC 会话数有限，设太大反而互相拖慢。</summary>
    private const int VideoConcurrency = 2;

    /// <summary>照片转换并发。PNG 编码是 CPU 密集且单线程，可以多开。</summary>
    private const int PhotoConcurrency = 4;

    private const int AudioConcurrency = 2;

    /// <summary>进度写库的节流粒度（百分比）。避免每 1% 都写一次库。</summary>
    private const int ProgressWriteStep = 5;

    private readonly LibraryLayout _layout = layout;
    private readonly AssetRepository _assets = assets;
    private readonly JobRepository _jobs = jobs;
    private readonly JobQueueService _queue = queue;
    private readonly IMediaProbe _probe = probe;
    private readonly IVideoTranscoder _transcoder = transcoder;
    private readonly IImageConverter _imageConverter = imageConverter;
    private readonly IFileCopier _copier = copier;

    /// <summary>当前是否还有任务在执行（供界面判断是否显示任务条）。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// 处理全部排队任务。可重复调用（幂等：队列空了就直接返回）。
    /// </summary>
    public async Task<JobRunStats> RunAsync(
        IProgress<JobProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 崩溃恢复（两件事一起做）：
        //   1. 把上次中断时停留在 Running 的任务退回队列
        //   2. 清掉它们留下的临时文件
        // 只做 1 不做 2 的话，残留的 .tmp 会一直堆在库里 —— 而且因为 ffmpeg 刚被强杀时
        // 文件句柄可能还没释放，单靠取消时的即时删除并不可靠，必须在这里兜一次底。
        var recovered = await _jobs.RecoverInterruptedAsync(ct).ConfigureAwait(false);
        if (recovered > 0)
            DiagnosticsNote($"恢复 {recovered} 个中断的任务");

        var orphans = _layout.CleanOrphanTempFiles();
        if (orphans > 0)
            DiagnosticsNote($"清理了 {orphans} 个残留临时文件");

        var queued = await _jobs.GetQueuedAsync(ct).ConfigureAwait(false);
        if (queued.Count == 0)
            return JobRunStats.Empty;

        var assetMap = (await _assets.GetByIdsAsync(queued.Select(j => j.AssetId).ToList(), ct)
                .ConfigureAwait(false))
            .ToDictionary(a => a.Id);

        var videoJobs = queued.Where(j => j.JobType == JobType.TranscodeVideo).ToList();
        var photoJobs = queued.Where(j => j.JobType == JobType.ConvertPhoto).ToList();
        var audioJobs = queued.Where(j => j.JobType == JobType.CopyAudio).ToList();

        var succeeded = 0;
        var failed = 0;

        IsRunning = true;
        try
        {
            // 三类任务并行推进，各自有独立的并发上限。
            // 视频用 2 个 worker（NVENC 会话受限），照片 4 个（CPU 密集），音频 2 个（IO 密集）。
            var groups = new List<Task>();

            if (videoJobs.Count > 0)
                groups.Add(ProcessGroupAsync(videoJobs, VideoConcurrency));

            if (photoJobs.Count > 0)
                groups.Add(ProcessGroupAsync(photoJobs, PhotoConcurrency));

            if (audioJobs.Count > 0)
                groups.Add(ProcessGroupAsync(audioJobs, AudioConcurrency));

            await Task.WhenAll(groups).ConfigureAwait(false);
        }
        finally
        {
            IsRunning = false;
        }

        sw.Stop();
        return new JobRunStats(queued.Count, succeeded, failed, sw.Elapsed);

        async Task ProcessGroupAsync(List<Job> group, int concurrency)
        {
            await Parallel.ForEachAsync(
                group,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                async (job, token) =>
                {
                    if (!assetMap.TryGetValue(job.AssetId, out var asset)) return;

                    var ok = await ProcessOneAsync(job, asset, progress, token).ConfigureAwait(false);

                    if (ok) Interlocked.Increment(ref succeeded);
                    else Interlocked.Increment(ref failed);
                }).ConfigureAwait(false);
        }
    }

    /// <summary>处理单个任务。成功返回 true。</summary>
    private async Task<bool> ProcessOneAsync(
        Job job,
        Asset asset,
        IProgress<JobProgress>? progress,
        CancellationToken ct)
    {
        // ★ 暂停门：全局暂停时，新任务在这里等待，不打断正在跑的任务
        await _queue.WaitWhilePausedAsync(ct).ConfigureAwait(false);

        // 注册可单独取消的令牌（"取消该任务"由 JobQueueService 打断）
        var jobToken = _queue.RegisterRunning(job.Id, ct);

        try
        {
            // 等待期间任务可能被取消/暂停 —— 只有仍为 Queued 才开始
            if (await _jobs.MarkRunningAsync(job.Id, jobToken).ConfigureAwait(false) == 0)
                return false;

            await _assets.UpdateStatusAsync(asset.Id, AssetStatus.Processing, null, jobToken)
                .ConfigureAwait(false);

            Report(progress, asset, job, 0, JobStatus.Running);

            var sourcePath = asset.SourcePath;

            // 源文件在排队期间被移走了
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                await FailAsync(job, asset, "源文件已不存在（可能在排队期间被移动或删除）", progress, jobToken)
                    .ConfigureAwait(false);
                return false;
            }

            var finalPath = _layout.ToAbsolute(asset.LibraryPath);
            var targetExtension = Path.GetExtension(asset.FileName);

            // 临时文件必须带正确的扩展名 —— ffmpeg 靠它决定输出容器格式
            var tempPath = Path.Combine(_layout.TempDir, $"{Guid.NewGuid():N}.tmp{targetExtension}");

            try
            {
                Directory.CreateDirectory(_layout.TempDir);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

                // ── 探测源文件（决定转码参数）──────────────────────
                var probeResult = await _probe.ProbeAsync(sourcePath, jobToken).ConfigureAwait(false);

                if (asset.Kind == AssetKind.Video && probeResult is null)
                {
                    await FailAsync(job, asset, "无法解析该视频（文件可能已损坏）", progress, jobToken)
                        .ConfigureAwait(false);
                    return false;
                }

                // ── 执行处理 ───────────────────────────────────────
                var request = new ProcessRequest(sourcePath, tempPath, probeResult);

                using var progressAdapter = new ThrottledProgress(
                    onUiUpdate: fraction => Report(progress, asset, job, fraction, JobStatus.Running),
                    onPersist: fraction => _jobs.UpdateProgressAsync(job.Id, fraction, CancellationToken.None),
                    step: ProgressWriteStep);

                var outcome = asset.Kind switch
                {
                    AssetKind.Video => await _transcoder
                        .TranscodeAsync(request, progressAdapter, jobToken).ConfigureAwait(false),

                    AssetKind.Photo => await _imageConverter
                        .ConvertToPngAsync(request, progressAdapter, jobToken).ConfigureAwait(false),

                    _ => await _copier
                        .CopyAsync(request, progressAdapter, jobToken).ConfigureAwait(false),
                };

                if (!outcome.Success)
                {
                    DeleteTemp(tempPath);
                    await FailAsync(job, asset, outcome.ErrorMessage ?? "处理失败", progress, jobToken)
                        .ConfigureAwait(false);
                    return false;
                }

                // ── 原子落盘 ───────────────────────────────────────
                // 同卷 File.Move 是原子的：要么看到完整文件，要么什么都看不到。
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(tempPath, finalPath);

                var finalSize = new FileInfo(finalPath).Length;

                // ── 用库内文件回读校验 ─────────────────────────────
                var libraryProbe = await _probe.ProbeAsync(finalPath, jobToken).ConfigureAwait(false) ?? probeResult ?? new MediaProbe();

                asset.Width = libraryProbe.Width;
                asset.Height = libraryProbe.Height;
                asset.DurationMs = libraryProbe.DurationMs > 0 ? libraryProbe.DurationMs : null;
                asset.VideoCodec = libraryProbe.VideoCodec;
                asset.AudioCodec = libraryProbe.AudioCodec;
                asset.PixFmt = libraryProbe.PixFmt;
                asset.IsHdr = libraryProbe.IsHdr;
                asset.Rotation = libraryProbe.Rotation;

                await _assets.MarkReadyAsync(asset, finalSize, jobToken).ConfigureAwait(false);
                await _jobs.MarkDoneAsync(job.Id, jobToken).ConfigureAwait(false);

                Report(progress, asset, job, 1, JobStatus.Done);
                return true;
            }
            catch (OperationCanceledException)
            {
                DeleteTemp(tempPath);

                // ★ 用户取消了这个任务（不是应用关闭）→ 优雅收尾：
                //   任务已是 Canceled（JobQueueService 改的库），素材退回 Pending。
                if (jobToken.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    try
                    {
                        await _assets.UpdateStatusAsync(
                            asset.Id, AssetStatus.Pending, null, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception) { }

                    Report(progress, asset, job, 0, JobStatus.Canceled, "已取消");
                    return false;
                }

                throw;
            }
            catch (Exception ex)
            {
                DeleteTemp(tempPath);
                await FailAsync(job, asset, $"{ex.GetType().Name}: {ex.Message}", progress, jobToken)
                    .ConfigureAwait(false);
                return false;
            }
        }
        finally
        {
            _queue.UnregisterRunning(job.Id);
        }
    }

    private async Task FailAsync(
        Job job,
        Asset asset,
        string message,
        IProgress<JobProgress>? progress,
        CancellationToken ct)
    {
        await _jobs.MarkFailedAsync(job.Id, message, MaxAttempts, ct).ConfigureAwait(false);

        // 任务还有重试机会时，素材退回 Pending；否则才是真失败
        var jobCounts = await _jobs.GetByAssetAsync(asset.Id, ct).ConfigureAwait(false);
        var stillRetrying = jobCounts.Any(j =>
            j.Id == job.Id && j.Status == JobStatus.Queued);

        var status = stillRetrying ? AssetStatus.Pending : AssetStatus.Failed;
        await _assets.UpdateStatusAsync(asset.Id, status, stillRetrying ? null : message, ct)
            .ConfigureAwait(false);

        Report(progress, asset, job, 0, stillRetrying ? JobStatus.Queued : JobStatus.Failed, message);
    }

    private static void Report(
        IProgress<JobProgress>? progress,
        Asset asset,
        Job job,
        double fraction,
        JobStatus status,
        string? error = null)
        => progress?.Report(new JobProgress(asset.Id, asset.FileName, job.JobType, fraction, status, error));

    private static void DeleteTemp(string path)
    {
        // 取消时 ffmpeg 刚被强杀，文件句柄可能还没释放 —— 重试几次而不是直接放弃
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(80);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static void DiagnosticsNote(string message)
        => Debug.WriteLine($"[ImportJobRunner] {message}");

    /// <summary>
    /// 进度节流器：转码进度回调很密集，直接每次写库会把磁盘打满。
    /// 界面回调每次都发（内存操作，很便宜），只有跨过 <c>step</c> 个百分点才真正落库。
    /// </summary>
    private sealed class ThrottledProgress : IProgress<double>, IDisposable
    {
        private readonly Action<double> _onUiUpdate;
        private readonly Func<double, Task> _onPersist;
        private readonly int _step;
        private readonly Lock _gate = new();

        private int _lastPersistedPercent = -1000;
        private Task _pending = Task.CompletedTask;

        public ThrottledProgress(Action<double> onUiUpdate, Func<double, Task> onPersist, int step)
        {
            _onUiUpdate = onUiUpdate;
            _onPersist = onPersist;
            _step = Math.Max(1, step);
        }

        public void Report(double value)
        {
            // 界面：每次都通知
            try { _onUiUpdate(value); }
            catch (Exception) { /* 界面回调异常不能影响转码 */ }

            var percent = (int)(value * 100);

            lock (_gate)
            {
                if (percent - _lastPersistedPercent < _step && percent < 100) return;
                _lastPersistedPercent = percent;

                // 串行化写库，避免并发写同一行
                _pending = _pending.ContinueWith(
                    _ => _onPersist(value),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
            }
        }

        public void Dispose()
        {
            try { _pending.Wait(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* 落库失败不影响主流程 */ }
        }
    }
}
