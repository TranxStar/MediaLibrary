using System.Diagnostics;
using MediaLibrary.Core;
using MediaLibrary.Core.Hashing;
using MediaLibrary.Core.Models;
using MediaLibrary.Core.Naming;
using MediaLibrary.Data.Repositories;

namespace MediaLibrary.Data.Services;

/// <summary>一次导入的结果。</summary>
public sealed record ImportResult(
    ImportPlan Plan,
    int Enqueued,
    int Skipped,
    TimeSpan Elapsed,
    string? PrimaryFolderKey)
{
    public static readonly ImportResult Empty =
        new(ImportPlan.Empty, 0, 0, TimeSpan.Zero, null);

    public override string ToString()
        => $"入库 {Enqueued} 项，跳过 {Skipped} 项，耗时 {Elapsed.TotalSeconds:F2}s";
}

/// <summary>输出体积预估（用于导入前的磁盘空间预检）。</summary>
public sealed record SpaceEstimate(long RequiredBytes, long FreeBytes)
{
    public bool IsSufficient => FreeBytes >= RequiredBytes;

    public double RequiredGb => RequiredBytes / 1024.0 / 1024 / 1024;
    public double FreeGb => FreeBytes / 1024.0 / 1024 / 1024;
}

/// <summary>
/// 导入服务（设计文档 §5.1 第 0~1 步）。
///
/// 职责：扫描拖入路径 → 哈希去重 → 分配序号命名 → 写库 → 入队。
/// <b>实际的文件落地由 <see cref="ImportJobRunner"/> 在后台完成</b>，
/// 这里只负责把"要做的事"变成数据库里的记录，让界面立刻能显示占位卡片。
/// </summary>
public sealed class ImportService(
    LibraryLayout layout,
    AssetRepository assets,
    NamingCounterRepository counters,
    JobQueueService queue)
{
    private readonly LibraryLayout _layout = layout;
    private readonly AssetRepository _assets = assets;
    private readonly NamingCounterRepository _counters = counters;
    private readonly JobQueueService _queue = queue;

    /// <summary>低于此倍数的剩余空间才拦截导入，避免估算过严把正常导入挡掉。</summary>
    private const double SpaceSafetyFactor = 1.2;

    /// <summary>
    /// 执行一次导入。返回后素材已是 <see cref="AssetStatus.Pending"/> 状态并已排入任务队列。
    /// </summary>
    public async Task<ImportResult> ImportAsync(
        IEnumerable<string> droppedPaths,
        IProgress<ImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // ── 第 1 步：展开拖入路径 ──────────────────────────────
        progress?.Report(new ImportProgress("正在扫描", 0, 0, ""));

        var expanded = await Task.Run(
            () => LibraryPathHelper.ExpandDrop(droppedPaths).ToList(), ct).ConfigureAwait(false);

        if (expanded.Count == 0)
            return ImportResult.Empty with { Elapsed = sw.Elapsed };

        // ── 第 2 步：哈希 + 分类 ───────────────────────────────
        var skipped = new List<SkippedImport>();
        var sources = new List<SourceFile>(expanded.Count);
        var processed = 0;

        foreach (var item in expanded)
        {
            ct.ThrowIfCancellationRequested();

            processed++;
            progress?.Report(new ImportProgress(
                "正在校验与去重", processed, expanded.Count, Path.GetFileName(item.SourcePath)));

            var kind = MediaKindClassifier.Classify(item.SourcePath);
            if (kind is null)
            {
                skipped.Add(new SkippedImport(item.SourcePath, ImportPlanner.ReasonUnsupported));
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(item.SourcePath).Length;
            }
            catch (Exception)
            {
                skipped.Add(new SkippedImport(item.SourcePath, ImportPlanner.ReasonSourceMissing));
                continue;
            }

            string hash;
            try
            {
                hash = await QuickHash.ComputeAsync(item.SourcePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                skipped.Add(new SkippedImport(item.SourcePath, ImportPlanner.ReasonSourceMissing));
                continue;
            }

            sources.Add(new SourceFile(item.SourcePath, item.FolderKey, kind.Value, size, hash));
        }

        // ── 第 3 步：去重 ──────────────────────────────────────
        progress?.Report(new ImportProgress("正在比对库内已有素材", 0, 0, ""));

        var existingHashes = await _assets.GetAllHashesAsync(ct).ConfigureAwait(false);
        var (accepted, duplicateSkips) = ImportPlanner.FilterDuplicates(sources, existingHashes);
        skipped.AddRange(duplicateSkips);

        if (accepted.Count == 0)
            return new ImportResult(new ImportPlan([], skipped), 0, skipped.Count, sw.Elapsed, null);

        // ── 第 4 步：磁盘空间预检 ──────────────────────────────
        var estimate = EstimateSpace(accepted);
        if (!estimate.IsSufficient)
        {
            throw new InvalidOperationException(
                $"磁盘空间不足：预计需要约 {estimate.RequiredGb:F1} GB，但只剩 {estimate.FreeGb:F1} GB。\n" +
                $"请先清理空间，或把素材库移到空间更大的磁盘。");
        }

        // ── 第 5 步：分配序号 + 命名 ───────────────────────────
        progress?.Report(new ImportProgress("正在分配文件名", 0, accepted.Count, ""));

        var planned = new List<PlannedImport>(accepted.Count);

        foreach (var group in accepted.GroupBy(s => s.FolderKey, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var source in group)
            {
                ct.ThrowIfCancellationRequested();

                // 序号在事务内原子分配。失败的任务会"烧号"不复用 —— 比冒重号风险安全。
                var index = await _counters.AllocateNextAsync(source.FolderKey, ct).ConfigureAwait(false);
                planned.Add(ImportPlanner.Name(source, index));
            }
        }

        var plan = new ImportPlan(planned, skipped);

        // ── 第 6 步：写库 + 入队 ───────────────────────────────
        progress?.Report(new ImportProgress("正在建立索引", 0, planned.Count, ""));

        var assetRows = planned.Select(p => new Asset
        {
            Kind = p.Source.Kind,
            FileName = p.FileName,
            FolderKey = p.Source.FolderKey,
            LibraryPath = p.LibraryPath,
            SourcePath = p.Source.SourcePath,
            SourceFolder = NamingEngine.InnermostFolderName(p.Source.FolderKey),
            SizeBytes = p.Source.SizeBytes,
            QuickHash = p.Source.QuickHash,
            Status = AssetStatus.Pending,
        }).ToList();

        // 用 RETURNING 拿真实 Id，失败会抛异常而不是静默错配
        var ids = await _assets.InsertManyReturningIdsAsync(assetRows, ct).ConfigureAwait(false);

        var jobRows = ids
            .Select((id, i) => (AssetId: id, JobType: JobTypeFor(planned[i].Source.Kind), Priority: 0))
            .ToList();

        await _queue.EnqueueManyAsync(jobRows, ct).ConfigureAwait(false);

        // 目标目录要先建出来，否则原子改名会失败
        foreach (var folderKey in planned.Select(p => p.Source.FolderKey).Distinct(StringComparer.OrdinalIgnoreCase))
            Directory.CreateDirectory(_layout.FolderAbsolutePath(folderKey));

        sw.Stop();

        return new ImportResult(
            plan,
            ids.Count,
            skipped.Count,
            sw.Elapsed,
            planned.FirstOrDefault()?.Source.FolderKey);
    }

    /// <summary>素材种类 → 处理任务类型。</summary>
    public static JobType JobTypeFor(AssetKind kind) => kind switch
    {
        AssetKind.Video => JobType.TranscodeVideo,
        AssetKind.Photo => JobType.ConvertPhoto,
        _ => JobType.CopyAudio,
    };

    /// <summary>
    /// 预估输出体积。
    ///
    /// 视频转 H.265 通常压到源的 30~50%；照片转 PNG 会膨胀（实测 3~6 倍）；
    /// 音频原样复制。取偏保守的系数，并在总量上再留安全余量。
    /// </summary>
    internal SpaceEstimate EstimateSpace(IReadOnlyList<SourceFile> files)
    {
        double estimate = 0;

        foreach (var file in files)
        {
            estimate += file.Kind switch
            {
                AssetKind.Video => file.SizeBytes * 0.5,
                AssetKind.Photo => file.SizeBytes * 4.0,
                _ => file.SizeBytes,
            };
        }

        var required = (long)(estimate * SpaceSafetyFactor);
        return new SpaceEstimate(required, _layout.GetFreeSpaceBytes());
    }
}
