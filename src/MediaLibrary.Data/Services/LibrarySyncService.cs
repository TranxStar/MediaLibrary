using System.Diagnostics;
using MediaLibrary.Core;
using MediaLibrary.Core.Hashing;
using MediaLibrary.Core.Models;
using MediaLibrary.Core.Naming;
using MediaLibrary.Data.Repositories;

namespace MediaLibrary.Data.Services;

public sealed record SyncProgress(int Processed, int Total, string CurrentItem);

/// <summary>一次文件系统同步的结果。</summary>
public sealed record SyncResult
{
    public int FilesOnDisk { get; init; }
    public int Added { get; init; }
    public int SizeChanged { get; init; }
    public int Missing { get; init; }
    public int Unchanged { get; init; }
    public TimeSpan Elapsed { get; init; }

    public override string ToString()
        => $"磁盘 {FilesOnDisk} 个文件 | 新增 {Added} | 大小变化 {SizeChanged} | " +
           $"丢失 {Missing} | 未变 {Unchanged} | 耗时 {Elapsed.TotalSeconds:F2}s";
}

/// <summary>
/// 库文件系统同步（设计文档 §5.1 的"巡检"部分）。
///
/// 职责：把磁盘上的实际文件与数据库记录对账。
/// <list type="bullet">
///   <item>磁盘有、库里没有 → 收编为 Ready（用户可能手工拷进来了）</item>
///   <item>磁盘有、库里也有但大小变了 → 更新大小</item>
///   <item>磁盘没有、库里有 → 标记 Missing</item>
/// </list>
///
/// 这是首次指向一个已有素材目录时，界面能立刻出内容的关键路径。
/// </summary>
public sealed class LibrarySyncService(
    LibraryLayout layout,
    AssetRepository assets,
    NamingCounterRepository counters)
{
    private readonly LibraryLayout _layout = layout;
    private readonly AssetRepository _assets = assets;
    private readonly NamingCounterRepository _counters = counters;

    public async Task<SyncResult> SyncAsync(
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // ── 1. 扫描磁盘 ────────────────────────────────────────
        var filesOnDisk = await Task.Run(
            () => LibraryPathHelper.ScanLibrary(_layout.Root).ToList(), ct).ConfigureAwait(false);

        var dbIndex = await _assets.GetPathIndexAsync(ct).ConfigureAwait(false);

        // ── 2. 找出需要新增 / 更新的文件 ───────────────────────
        var toInsert = new List<Asset>();
        var sizeChanged = new List<(long Id, long Size)>();
        var onDiskPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processed = 0;

        foreach (var file in filesOnDisk)
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(_layout.Root, file.AbsolutePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            onDiskPaths.Add(relative);

            processed++;
            if (processed % 25 == 0 || processed == filesOnDisk.Count)
                progress?.Report(new SyncProgress(processed, filesOnDisk.Count, file.FileName));

            long size;
            try
            {
                size = new FileInfo(file.AbsolutePath).Length;
            }
            catch (IOException) { continue; }

            if (dbIndex.TryGetValue(relative, out var existing))
            {
                if (existing.SizeBytes != size) sizeChanged.Add((existing.Id, size));
                continue;
            }

            var kind = MediaKindClassifier.Classify(file.FileName);
            if (kind is null) continue;

            string hash;
            try
            {
                hash = await QuickHash.ComputeAsync(file.AbsolutePath, ct).ConfigureAwait(false);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            toInsert.Add(new Asset
            {
                Kind = kind.Value,
                FileName = file.FileName,
                FolderKey = file.FolderKey,
                LibraryPath = relative,
                SourcePath = file.AbsolutePath,
                SourceFolder = NamingEngine.InnermostFolderName(file.FolderKey),
                SizeBytes = size,
                QuickHash = hash,
                Status = AssetStatus.Ready,
            });
        }

        // ── 3. 写库 ────────────────────────────────────────────
        await _assets.InsertManyAsync(toInsert, ct).ConfigureAwait(false);

        foreach (var (id, size) in sizeChanged)
            await _assets.UpdateSizeAsync(id, size, ct).ConfigureAwait(false);

        // ── 4. 标记丢失 ────────────────────────────────────────
        var missingIds = dbIndex
            .Where(kv => !onDiskPaths.Contains(kv.Key))
            .Select(kv => kv.Value.Id)
            .ToList();

        await _assets.MarkMissingAsync(missingIds, ct).ConfigureAwait(false);

        // ── 5. 重新播种命名计数器 ──────────────────────────────
        // 用户可能手工拷入文件，不播种将来导入会重号。
        await _counters.ReseedAllAsync(ct).ConfigureAwait(false);

        sw.Stop();

        return new SyncResult
        {
            FilesOnDisk = filesOnDisk.Count,
            Added = toInsert.Count,
            SizeChanged = sizeChanged.Count,
            Missing = missingIds.Count,
            Unchanged = filesOnDisk.Count - toInsert.Count - sizeChanged.Count,
            Elapsed = sw.Elapsed,
        };
    }
}
