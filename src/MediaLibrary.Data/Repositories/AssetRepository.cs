using Dapper;
using MediaLibrary.Core.Models;

namespace MediaLibrary.Data.Repositories;

/// <summary>某个库内目录的汇总信息，用于构建左侧文件夹树。</summary>
public sealed class FolderSummary
{
    public string FolderKey { get; init; } = "";
    public long AssetCount { get; init; }
}

/// <summary>按素材种类统计的占用。</summary>
public sealed class KindStats
{
    public AssetKind Kind { get; init; }
    public long Count { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>库整体统计。</summary>
public sealed class LibraryStats
{
    public IReadOnlyList<KindStats> ByKind { get; init; } = [];
    public long TotalCount => ByKind.Sum(k => k.Count);
    public long TotalBytes => ByKind.Sum(k => k.TotalBytes);
    public long VideoBytes => ByKind.FirstOrDefault(k => k.Kind == AssetKind.Video)?.TotalBytes ?? 0;
    public long PhotoBytes => ByKind.FirstOrDefault(k => k.Kind == AssetKind.Photo)?.TotalBytes ?? 0;
    public long AudioBytes => ByKind.FirstOrDefault(k => k.Kind == AssetKind.Audio)?.TotalBytes ?? 0;
    public long CountPending { get; init; }
    public long CountFailed { get; init; }
    public long CountMissing { get; init; }
}

/// <summary>Assets 表读写。</summary>
public sealed class AssetRepository
{
    private const string SelectColumns = """
        Id, Kind, FileName, FolderKey, LibraryPath, SourcePath, SourceFolder,
        SizeBytes, QuickHash, Width, Height, DurationMs, VideoCodec, AudioCodec,
        PixFmt, IsHdr, Rotation, TakenAt, CameraModel, ThumbPath, EnrichAttempts, Status,
        ErrorMessage, CreatedAt, UpdatedAt
        """;

    private readonly LibraryDb _db;

    /// <summary>
    /// 构造写库参数。
    ///
    /// ★ 枚举必须显式 <c>ToString()</c>。
    /// 实测：把整个 Asset 对象直接当 Dapper 参数时，注册的枚举类型处理器<b>不会</b>生效，
    /// Status 会以整数落库（Ready → '2'），导致所有 <c>WHERE Status = 'Ready'</c> 的查询静默失效。
    /// 这里全部显式转换，行为可预期。
    /// </summary>
    private static object BuildParams(Asset a) => new
    {
        a.Id,
        Kind = a.Kind.ToString(),
        a.FileName,
        a.FolderKey,
        a.LibraryPath,
        a.SourcePath,
        a.SourceFolder,
        a.SizeBytes,
        a.QuickHash,
        a.Width,
        a.Height,
        a.DurationMs,
        a.VideoCodec,
        a.AudioCodec,
        a.PixFmt,
        a.IsHdr,
        a.Rotation,
        a.TakenAt,
        a.CameraModel,
        a.ThumbPath,
        a.EnrichAttempts,
        Status = a.Status.ToString(),
        a.ErrorMessage,
        a.CreatedAt,
        a.UpdatedAt,
    };

    public AssetRepository(LibraryDb db)
    {
        DapperSetup.EnsureInitialized();
        _db = db;
    }

    // ══════════════════════════ 查询 ══════════════════════════

    /// <summary>取某个库内目录下的全部素材，按文件名排序。</summary>
    public Task<IReadOnlyList<Asset>> GetByFolderAsync(string folderKey, CancellationToken ct = default)
        => _db.ReadAsync<IReadOnlyList<Asset>>(async conn =>
            (await conn.QueryAsync<Asset>(new CommandDefinition(
                $"SELECT {SelectColumns} FROM Assets WHERE FolderKey = $key ORDER BY FileName COLLATE NOCASE;",
                new { key = folderKey }, cancellationToken: ct)).ConfigureAwait(false)).ToList(), ct);

    public Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct = default)
        => _db.ReadAsync<IReadOnlyList<Asset>>(async conn =>
            (await conn.QueryAsync<Asset>(new CommandDefinition(
                $"SELECT {SelectColumns} FROM Assets ORDER BY FolderKey, FileName COLLATE NOCASE;",
                cancellationToken: ct)).ConfigureAwait(false)).ToList(), ct);

    public Task<Asset?> GetByIdAsync(long id, CancellationToken ct = default)
        => _db.ReadAsync(conn => conn.QueryFirstOrDefaultAsync<Asset>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM Assets WHERE Id = $id;", new { id }, cancellationToken: ct)), ct);

    /// <summary>批量按 Id 取素材（任务执行器用）。</summary>
    public Task<IReadOnlyList<Asset>> GetByIdsAsync(
        IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Task.FromResult<IReadOnlyList<Asset>>([]);

        return _db.ReadAsync<IReadOnlyList<Asset>>(async conn =>
            (await conn.QueryAsync<Asset>(new CommandDefinition(
                $"SELECT {SelectColumns} FROM Assets WHERE Id IN @ids;",
                new { ids }, cancellationToken: ct)).ConfigureAwait(false)).ToList(), ct);
    }

    /// <summary>
    /// 转码/转换完成、文件已原子落盘后调用：写入最终大小与探测到的元数据，状态置 Ready。
    /// <c>ThumbPath</c> 故意不动 —— 留给补全服务按库内文件生成缩略图。
    /// </summary>
    public Task MarkReadyAsync(Asset asset, long finalSizeBytes, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            asset.SizeBytes = finalSizeBytes;
            asset.Status = AssetStatus.Ready;
            asset.ErrorMessage = null;
            asset.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Assets SET
                    SizeBytes = $SizeBytes, Status = $Status, ErrorMessage = NULL,
                    Width = $Width, Height = $Height, DurationMs = $DurationMs,
                    VideoCodec = $VideoCodec, AudioCodec = $AudioCodec,
                    PixFmt = $PixFmt, IsHdr = $IsHdr, Rotation = $Rotation,
                    UpdatedAt = $UpdatedAt
                WHERE Id = $Id;
                """, BuildParams(asset), tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    /// <summary>取库内全部相对路径，用于文件系统同步时的差异比对。</summary>
    public Task<Dictionary<string, (long Id, string FileName, long SizeBytes)>> GetPathIndexAsync(
        CancellationToken ct = default)
        => _db.ReadAsync(async conn =>
        {
            var rows = await conn.QueryAsync<(string LibraryPath, long Id, string FileName, long SizeBytes)>(
                new CommandDefinition(
                    "SELECT LibraryPath, Id, FileName, SizeBytes FROM Assets;",
                    cancellationToken: ct)).ConfigureAwait(false);

            return rows.ToDictionary(r => r.LibraryPath, r => (r.Id, r.FileName, r.SizeBytes),
                StringComparer.OrdinalIgnoreCase);
        }, ct);

    /// <summary>取库内全部已知哈希（去重用）。</summary>
    public Task<HashSet<string>> GetAllHashesAsync(CancellationToken ct = default)
        => _db.ReadAsync(async conn =>
        {
            var hashes = await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT QuickHash FROM Assets;", cancellationToken: ct)).ConfigureAwait(false);
            return hashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }, ct);

    /// <summary>各目录的素材数量，用于文件夹树。</summary>
    public Task<IReadOnlyList<FolderSummary>> GetFolderSummariesAsync(CancellationToken ct = default)
        => _db.ReadAsync<IReadOnlyList<FolderSummary>>(async conn =>
            (await conn.QueryAsync<FolderSummary>(new CommandDefinition(
                "SELECT FolderKey, COUNT(*) AS AssetCount FROM Assets GROUP BY FolderKey;",
                cancellationToken: ct)).ConfigureAwait(false)).ToList(), ct);

    /// <summary>库统计（占用空间 / 各状态数量）。</summary>
    public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
        => _db.ReadAsync(async conn =>
        {
            var byKind = (await conn.QueryAsync<KindStats>(new CommandDefinition(
                "SELECT Kind, COUNT(*) AS Count, COALESCE(SUM(SizeBytes),0) AS TotalBytes " +
                "FROM Assets WHERE Status = 'Ready' GROUP BY Kind;",
                cancellationToken: ct)).ConfigureAwait(false)).ToList();

            var statusCounts = (await conn.QueryAsync<(string Status, long Cnt)>(new CommandDefinition(
                "SELECT Status, COUNT(*) AS Cnt FROM Assets GROUP BY Status;",
                cancellationToken: ct)).ConfigureAwait(false)).ToDictionary(x => x.Status, x => x.Cnt,
                StringComparer.OrdinalIgnoreCase);

            return new LibraryStats
            {
                ByKind = byKind,
                CountPending = statusCounts.GetValueOrDefault("Pending"),
                CountFailed = statusCounts.GetValueOrDefault("Failed"),
                CountMissing = statusCounts.GetValueOrDefault("Missing"),
            };
        }, ct);

    // ══════════════════════════ 写入 ══════════════════════════

    public Task<long> InsertAsync(Asset asset, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            asset.CreatedAt = string.IsNullOrEmpty(asset.CreatedAt) ? now : asset.CreatedAt;
            asset.UpdatedAt = now;

            var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO Assets
                    (Kind, FileName, FolderKey, LibraryPath, SourcePath, SourceFolder,
                     SizeBytes, QuickHash, Width, Height, DurationMs, VideoCodec, AudioCodec,
                     PixFmt, IsHdr, Rotation, TakenAt, CameraModel, ThumbPath, Status,
                     ErrorMessage, CreatedAt, UpdatedAt)
                VALUES
                    ($Kind, $FileName, $FolderKey, $LibraryPath, $SourcePath, $SourceFolder,
                     $SizeBytes, $QuickHash, $Width, $Height, $DurationMs, $VideoCodec, $AudioCodec,
                     $PixFmt, $IsHdr, $Rotation, $TakenAt, $CameraModel, $ThumbPath, $Status,
                     $ErrorMessage, $CreatedAt, $UpdatedAt);
                SELECT last_insert_rowid();
                """, BuildParams(asset), tx, cancellationToken: ct)).ConfigureAwait(false);

            asset.Id = id;
            return id;
        }, ct);

    /// <summary>更新探测到的元数据（尺寸/时长/编码等）。</summary>
    public Task UpdateMetadataAsync(Asset asset, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            asset.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Assets SET
                    SizeBytes = $SizeBytes, Width = $Width, Height = $Height,
                    DurationMs = $DurationMs, VideoCodec = $VideoCodec, AudioCodec = $AudioCodec,
                    PixFmt = $PixFmt, IsHdr = $IsHdr, Rotation = $Rotation,
                    TakenAt = $TakenAt, CameraModel = $CameraModel, UpdatedAt = $UpdatedAt
                WHERE Id = $Id;
                """, BuildParams(asset), tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    public Task UpdateStatusAsync(long id, AssetStatus status, string? errorMessage = null,
        CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Assets SET Status = $status, ErrorMessage = $err, UpdatedAt = $now
                WHERE Id = $id;
                """,
                new { id, status = status.ToString(), err = errorMessage, now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    public Task UpdateThumbPathAsync(long id, string? thumbName, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE Assets SET ThumbPath = $thumb, UpdatedAt = $now WHERE Id = $id;",
                new { id, thumb = thumbName, now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    public Task RenameAsync(long id, string newFileName, string newLibraryPath, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE Assets SET FileName = $fn, LibraryPath = $lp, UpdatedAt = $now WHERE Id = $id;",
                new { id, fn = newFileName, lp = newLibraryPath, now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    public Task DeleteAsync(long id, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM Assets WHERE Id = $id;", new { id }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    /// <summary>某个文件夹（含全部子文件夹）下的素材 Id 列表（删除文件夹用）。</summary>
    public Task<IReadOnlyList<long>> GetIdsByFolderPrefixAsync(string folderKey, CancellationToken ct = default)
        => _db.ReadAsync<IReadOnlyList<long>>(async conn =>
            (await conn.QueryAsync<long>(new CommandDefinition("""
                SELECT Id FROM Assets
                WHERE FolderKey = $folderKey OR FolderKey LIKE $prefix;
                """,
                new { folderKey, prefix = folderKey + "/%" },
                cancellationToken: ct)).ConfigureAwait(false)).ToList(), ct);

    /// <summary>
    /// 文件夹重命名：把该文件夹（含子目录）下所有素材的
    /// FolderKey 与 LibraryPath 的前缀从 oldKey 改写为 newKey。
    /// </summary>
    public Task<int> RenameFolderAsync(string oldKey, string newKey, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            var affected = 0;
            affected += await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Assets
                SET FolderKey = $newKey || substr(FolderKey, length($oldKey) + 1)
                WHERE FolderKey = $oldKey OR FolderKey LIKE $prefix;
                """,
                new { oldKey, newKey, prefix = oldKey + "/%" },
                tx, cancellationToken: ct)).ConfigureAwait(false);

            affected += await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Assets
                SET LibraryPath = $newKey || substr(LibraryPath, length($oldKey) + 1)
                WHERE LibraryPath = $oldKey OR LibraryPath LIKE $libPrefix;
                """,
                new { oldKey, newKey, libPrefix = oldKey + "/%" },
                tx, cancellationToken: ct)).ConfigureAwait(false);

            return affected;
        }, ct);

    /// <summary>批量更新状态，用于文件系统同步。</summary>
    public Task MarkMissingAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Task.CompletedTask;

        return _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE Assets SET Status = 'Missing', UpdatedAt = $now WHERE Id IN @ids;",
                new { ids, now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>
    /// 批量插入。首次扫描几百个文件时逐条开事务太慢，故合并到单个事务。
    /// </summary>
    public Task InsertManyAsync(IReadOnlyList<Asset> assets, CancellationToken ct = default)
    {
        if (assets.Count == 0) return Task.CompletedTask;

        return _db.WriteAsync(async (conn, tx) =>
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            foreach (var asset in assets)
            {
                ct.ThrowIfCancellationRequested();

                asset.CreatedAt = string.IsNullOrEmpty(asset.CreatedAt) ? now : asset.CreatedAt;
                asset.UpdatedAt = now;

                var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT OR IGNORE INTO Assets
                        (Kind, FileName, FolderKey, LibraryPath, SourcePath, SourceFolder,
                         SizeBytes, QuickHash, Width, Height, DurationMs, VideoCodec, AudioCodec,
                         PixFmt, IsHdr, Rotation, TakenAt, CameraModel, ThumbPath, Status,
                         ErrorMessage, CreatedAt, UpdatedAt)
                    VALUES
                        ($Kind, $FileName, $FolderKey, $LibraryPath, $SourcePath, $SourceFolder,
                         $SizeBytes, $QuickHash, $Width, $Height, $DurationMs, $VideoCodec, $AudioCodec,
                         $PixFmt, $IsHdr, $Rotation, $TakenAt, $CameraModel, $ThumbPath, $Status,
                         $ErrorMessage, $CreatedAt, $UpdatedAt);
                    SELECT last_insert_rowid();
                    """, BuildParams(asset), tx, cancellationToken: ct)).ConfigureAwait(false);

                asset.Id = id;
            }
        }, ct);
    }

    /// <summary>
    /// 批量插入并返回每行的真实 Id。
    ///
    /// 与 <see cref="InsertManyAsync"/> 的区别：这里用 <c>INSERT ... RETURNING Id</c>，
    /// 主键从数据库直接回传。导入流程必须用这个方法 ——
    /// <c>INSERT OR IGNORE</c> + <c>last_insert_rowid()</c> 在发生冲突时会返回上一行的
    /// rowid，导致任务挂到错误的素材上。
    /// </summary>
    public Task<IReadOnlyList<long>> InsertManyReturningIdsAsync(
        IReadOnlyList<Asset> assets, CancellationToken ct = default)
    {
        if (assets.Count == 0) return Task.FromResult<IReadOnlyList<long>>([]);

        return _db.WriteAsync<IReadOnlyList<long>>(async (conn, tx) =>
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var ids = new List<long>(assets.Count);

            foreach (var asset in assets)
            {
                ct.ThrowIfCancellationRequested();

                asset.CreatedAt = string.IsNullOrEmpty(asset.CreatedAt) ? now : asset.CreatedAt;
                asset.UpdatedAt = now;

                var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT INTO Assets
                        (Kind, FileName, FolderKey, LibraryPath, SourcePath, SourceFolder,
                         SizeBytes, QuickHash, Width, Height, DurationMs, VideoCodec, AudioCodec,
                         PixFmt, IsHdr, Rotation, TakenAt, CameraModel, ThumbPath, Status,
                         ErrorMessage, CreatedAt, UpdatedAt)
                    VALUES
                        ($Kind, $FileName, $FolderKey, $LibraryPath, $SourcePath, $SourceFolder,
                         $SizeBytes, $QuickHash, $Width, $Height, $DurationMs, $VideoCodec, $AudioCodec,
                         $PixFmt, $IsHdr, $Rotation, $TakenAt, $CameraModel, $ThumbPath, $Status,
                         $ErrorMessage, $CreatedAt, $UpdatedAt)
                    RETURNING Id;
                    """, BuildParams(asset), tx, cancellationToken: ct)).ConfigureAwait(false);

                asset.Id = id;
                ids.Add(id);
            }

            return ids;
        }, ct);
    }

    /// <summary>更新文件大小（外部改动检测）。</summary>
    public Task UpdateSizeAsync(long id, long sizeBytes, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE Assets SET SizeBytes = $size, Status = 'Ready', UpdatedAt = $now WHERE Id = $id;",
                new { id, size = sizeBytes, now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// 取需要补全元数据/缩略图的素材。
    /// 用 <c>EnrichAttempts</c> 上限兜底，避免永久失败的文件被反复重试拖死队列。
    /// </summary>
    public Task<IReadOnlyList<Asset>> GetNeedingEnrichmentAsync(
        int limit, int maxAttempts = 3, CancellationToken ct = default)
        => _db.ReadAsync<IReadOnlyList<Asset>>(async conn =>
            (await conn.QueryAsync<Asset>(new CommandDefinition(
                $"""
                 SELECT {SelectColumns} FROM Assets
                 WHERE Status = 'Ready'
                   AND ThumbPath IS NULL
                   AND EnrichAttempts < $maxAttempts
                 ORDER BY Id
                 LIMIT $limit;
                 """,
                new { limit, maxAttempts }, cancellationToken: ct)).ConfigureAwait(false)).ToList(), ct);

    /// <summary>补全成功：写入元数据与缩略图名。</summary>
    public Task CompleteEnrichmentAsync(Asset asset, string thumbName, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            asset.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            asset.ThumbPath = thumbName;

            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE Assets SET
                    Width = $Width, Height = $Height, DurationMs = $DurationMs,
                    VideoCodec = $VideoCodec, AudioCodec = $AudioCodec,
                    PixFmt = $PixFmt, IsHdr = $IsHdr, Rotation = $Rotation,
                    TakenAt = $TakenAt, CameraModel = $CameraModel,
                    ThumbPath = $ThumbPath, UpdatedAt = $UpdatedAt
                WHERE Id = $Id;
                """, BuildParams(asset), tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    /// <summary>补全失败：只增加尝试计数。达到上限后该行不会再进入队列。</summary>
    public Task MarkEnrichmentAttemptAsync(long id, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE Assets SET EnrichAttempts = EnrichAttempts + 1, UpdatedAt = $now WHERE Id = $id;",
                new { id, now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    /// <summary>等待补全的素材数量。</summary>
    public Task<int> ReadPendingEnrichmentCountAsync(int maxAttempts = 3, CancellationToken ct = default)
        => _db.ReadAsync(conn => conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM Assets " +
            "WHERE Status = 'Ready' AND ThumbPath IS NULL AND EnrichAttempts < $maxAttempts;",
            new { maxAttempts }, cancellationToken: ct)), ct);

    /// <summary>把缩略图列重置，强制重新补全（用于"重新生成缩略图"操作）。</summary>
    public Task ResetEnrichmentAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return Task.CompletedTask;

        return _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE Assets SET ThumbPath = NULL, EnrichAttempts = 0 WHERE Id IN @ids;",
                new { ids }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);
    }
}
