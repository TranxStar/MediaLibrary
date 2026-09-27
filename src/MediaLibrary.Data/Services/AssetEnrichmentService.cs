using System.Diagnostics;
using MediaLibrary.Core;
using MediaLibrary.Core.Abstractions;
using MediaLibrary.Core.Models;
using MediaLibrary.Data.Repositories;

namespace MediaLibrary.Data.Services;

/// <summary>单项补全完成通知。缩略图一生成就能立刻刷界面，不必等整批跑完。</summary>
public sealed record EnrichProgress(long AssetId, string FileName, bool Succeeded, string? ThumbName);

public sealed record EnrichStats(int Total, int Succeeded, int Failed, TimeSpan Elapsed)
{
    public override string ToString()
        => $"补全 {Total} 项（成功 {Succeeded} / 失败 {Failed}），耗时 {Elapsed.TotalSeconds:F1}s";
}

/// <summary>
/// 素材信息补全：ffprobe 探测元数据 + 生成缩略图（设计文档 §5.7）。
///
/// 与"入库管线"解耦：入库只负责把文件落到库里并写 DB，
/// 缩略图与元数据由这个后台服务慢慢补，界面先用占位图，补好了自动替换。
/// </summary>
public sealed class AssetEnrichmentService(
    LibraryLayout layout,
    AssetRepository assets,
    IMediaProbe probe,
    IThumbnailer thumbnailer,
    int concurrency = 4)
{
    private const int BatchSize = 48;
    private const int MaxAttempts = 3;

    private readonly LibraryLayout _layout = layout;
    private readonly AssetRepository _assets = assets;
    private readonly IMediaProbe _probe = probe;
    private readonly IThumbnailer _thumbnailer = thumbnailer;
    private readonly int _concurrency = Math.Max(1, concurrency);

    /// <summary>还有多少项等待补全。</summary>
    public Task<int> GetPendingCountAsync(CancellationToken ct = default)
        => _assets.ReadPendingEnrichmentCountAsync(MaxAttempts, ct);

    /// <summary>
    /// 补全所有待处理素材，跑到没有待办为止。
    /// 失败项每次 +1 尝试计数，达到上限后自动退出队列，不会死循环。
    /// </summary>
    public async Task<EnrichStats> EnrichAllAsync(
        IProgress<EnrichProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var succeeded = 0;
        var failed = 0;

        var pending = await _assets.ReadPendingEnrichmentCountAsync(MaxAttempts, ct).ConfigureAwait(false);
        if (pending == 0) return new EnrichStats(0, 0, 0, sw.Elapsed);

        // ★ 轮数上限必须按"待办总量"算，不能直接用 MaxAttempts。
        //   否则最多只能处理 MaxAttempts × BatchSize 项，剩下的永远不处理。
        //   多出的 MaxAttempts + 2 轮用于消化"整批都失败"的情况（失败会累加尝试计数，最终自然淘汰）。
        var maxPasses = (pending / BatchSize) + MaxAttempts + 2;

        for (var pass = 0; pass < maxPasses; pass++)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await _assets.GetNeedingEnrichmentAsync(BatchSize, MaxAttempts, ct)
                .ConfigureAwait(false);

            if (batch.Count == 0) break;

            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions { MaxDegreeOfParallelism = _concurrency, CancellationToken = ct },
                async (asset, token) =>
                {
                    var thumbName = await EnrichOneAsync(asset, token).ConfigureAwait(false);

                    if (thumbName is not null) Interlocked.Increment(ref succeeded);
                    else Interlocked.Increment(ref failed);

                    progress?.Report(new EnrichProgress(
                        asset.Id, asset.FileName, thumbName is not null, thumbName));
                }).ConfigureAwait(false);
        }

        sw.Stop();
        return new EnrichStats(succeeded + failed, succeeded, failed, sw.Elapsed);
    }

    /// <summary>
    /// 补全单个素材。
    /// </summary>
    /// <returns>成功返回缩略图文件名；失败返回 null（并累加尝试计数）。</returns>
    public async Task<string?> EnrichOneAsync(Asset asset, CancellationToken ct = default)
    {
        var absolute = _layout.ToAbsolute(asset.LibraryPath);
        if (!File.Exists(absolute))
        {
            await _assets.MarkEnrichmentAttemptAsync(asset.Id, ct).ConfigureAwait(false);
            return null;
        }

        // ── 1. 探测元数据 ──────────────────────────────────────
        var probeResult = await _probe.ProbeAsync(absolute, ct).ConfigureAwait(false);
        if (probeResult is not null)
        {
            asset.Width = probeResult.Width;
            asset.Height = probeResult.Height;
            asset.DurationMs = probeResult.DurationMs > 0 ? probeResult.DurationMs : null;
            asset.VideoCodec = probeResult.VideoCodec;
            asset.AudioCodec = probeResult.AudioCodec;
            asset.PixFmt = probeResult.PixFmt;
            asset.IsHdr = probeResult.IsHdr;
            asset.Rotation = probeResult.Rotation;
        }

        // ── 2. 生成缩略图（按内容哈希命名，天然去重复用）─────────
        var thumbName = _layout.ThumbRelativeName(asset.QuickHash);
        var thumbAbsolute = _layout.ThumbAbsolutePath(asset.QuickHash);

        var ok = await _thumbnailer
            .CreateAsync(absolute, asset.Kind, thumbAbsolute, asset.DurationMs, ct)
            .ConfigureAwait(false);

        if (!ok)
        {
            await _assets.MarkEnrichmentAttemptAsync(asset.Id, ct).ConfigureAwait(false);
            return null;
        }

        await _assets.CompleteEnrichmentAsync(asset, thumbName, ct).ConfigureAwait(false);
        return thumbName;
    }
}
