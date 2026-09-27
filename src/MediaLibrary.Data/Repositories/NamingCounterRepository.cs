using Dapper;
using MediaLibrary.Core.Naming;

namespace MediaLibrary.Data.Repositories;

/// <summary>
/// 命名序号计数器（设计文档 §5.2）。
///
/// 序号分配必须在事务内原子完成，否则并发导入会重号。
/// 采用 SQLite 的 <c>UPDATE ... RETURNING</c>（需 3.35+，Microsoft.Data.Sqlite 自带版本满足）。
/// </summary>
public sealed class NamingCounterRepository
{
    private readonly LibraryDb _db;

    public NamingCounterRepository(LibraryDb db)
    {
        DapperSetup.EnsureInitialized();
        _db = db;
    }

    /// <summary>
    /// 为指定目录分配下一个序号。
    /// 返回的序号在本目录内唯一且单调递增；失败的任务会"烧号"不复用（比冒重号风险安全）。
    /// </summary>
    public Task<int> AllocateNextAsync(string folderKey, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO NamingCounters(FolderKey, NextIndex) VALUES ($k, 1) " +
                "ON CONFLICT(FolderKey) DO NOTHING;",
                new { k = folderKey }, tx, cancellationToken: ct)).ConfigureAwait(false);

            var allocated = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "UPDATE NamingCounters SET NextIndex = NextIndex + 1 " +
                "WHERE FolderKey = $k RETURNING NextIndex - 1;",
                new { k = folderKey }, tx, cancellationToken: ct)).ConfigureAwait(false);

            return allocated;
        }, ct);

    /// <summary>读取当前下一个可用序号（不占用）。</summary>
    public Task<int> PeekNextAsync(string folderKey, CancellationToken ct = default)
        => _db.ReadAsync(async conn =>
            await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT NextIndex FROM NamingCounters WHERE FolderKey = $k;",
                new { k = folderKey }, cancellationToken: ct)).ConfigureAwait(false) ?? 1, ct);

    /// <summary>
    /// 按库内现存文件名重新播种所有目录的计数器。
    ///
    /// 必要性：用户可能手工把文件拷进库里（或首次指向一个已有素材目录），
    /// 此时计数器表是空的，不播种就会从 001 开始重号。
    ///
    /// 只往大了调，<b>绝不回退</b>（避免复用已烧掉的号）。
    /// </summary>
    public Task<int> ReseedAllAsync(CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            var rows = (await conn.QueryAsync<(string FolderKey, string FileName)>(new CommandDefinition(
                "SELECT FolderKey, FileName FROM Assets;",
                transaction: tx, cancellationToken: ct)).ConfigureAwait(false)).ToList();

            // 目录 → 已用的最大序号
            var maxByFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (folderKey, fileName) in rows)
            {
                var prefix = NamingEngine.InnermostFolderName(folderKey);
                var stem = Path.GetFileNameWithoutExtension(fileName);

                if (!stem.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase)) continue;

                var tail = stem[(prefix.Length + 1)..];
                if (!int.TryParse(tail, out var index)) continue;

                if (!maxByFolder.TryGetValue(folderKey, out var current) || index > current)
                    maxByFolder[folderKey] = index;
            }

            foreach (var (folderKey, maxIndex) in maxByFolder)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO NamingCounters(FolderKey, NextIndex) VALUES ($k, $seed)
                    ON CONFLICT(FolderKey) DO UPDATE SET NextIndex = MAX(NextIndex, $seed);
                    """,
                    new { k = folderKey, seed = maxIndex + 1 },
                    tx, cancellationToken: ct)).ConfigureAwait(false);
            }

            return maxByFolder.Count;
        }, ct);
}

/// <summary>Settings 表读写（键值对）。</summary>
public sealed class SettingsRepository
{
    private readonly LibraryDb _db;

    public SettingsRepository(LibraryDb db)
    {
        DapperSetup.EnsureInitialized();
        _db = db;
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => _db.ReadAsync(conn => conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Value FROM Settings WHERE Key = $key;", new { key }, cancellationToken: ct)), ct);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
        => _db.WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO Settings(Key, Value) VALUES ($key, $value)
                ON CONFLICT(Key) DO UPDATE SET Value = $value;
                """, new { key, value }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }, ct);

    public Task<T> GetAsync<T>(string key, T fallback, CancellationToken ct = default)
        => _db.ReadAsync(async conn =>
        {
            var raw = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT Value FROM Settings WHERE Key = $key;", new { key }, cancellationToken: ct))
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(raw)) return fallback;

            try
            {
                return (T)Convert.ChangeType(raw, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }, ct);
}
