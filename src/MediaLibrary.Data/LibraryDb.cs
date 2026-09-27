using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MediaLibrary.Data;

/// <summary>
/// SQLite 连接与迁移管理（设计文档 §4.2 / §4.4）。
///
/// 并发模型：写入串行化（单一 <see cref="SemaphoreSlim"/>），读取走连接池 + WAL。
/// </summary>
public sealed class LibraryDb : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public LibraryDb(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>当前 schema 版本。</summary>
    public int SchemaVersion { get; private set; }

    /// <summary>打开一个可用的连接（已应用每连接必需的 PRAGMA）。</summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();

        return conn;
    }

    /// <summary>初始化：设置库级 PRAGMA 并执行迁移。</summary>
    public void Initialize()
    {
        using (var conn = Open())
        {
            // 库级设置，只需设置一次（WAL 会持久化）
            conn.Execute("PRAGMA journal_mode = WAL;");
            conn.Execute("PRAGMA synchronous = NORMAL;");
        }

        SchemaVersion = MigrationRunner.Migrate(this);
    }

    /// <summary>
    /// 串行化写入。所有写操作必须走这里，避免 SQLITE_BUSY 与序号分配竞态。
    /// </summary>
    public async Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, Task<T>> action,
        CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            var result = await action(conn, tx).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task WriteAsync(
        Func<SqliteConnection, SqliteTransaction, Task> action,
        CancellationToken ct = default)
        => WriteAsync<object?>(async (c, t) => { await action(c, t).ConfigureAwait(false); return null; }, ct);

    /// <summary>同步写入（启动阶段使用）。</summary>
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        _writeGate.Wait();
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var result = action(conn, tx);
            tx.Commit();
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>执行查询。</summary>
    public async Task<T> ReadAsync<T>(Func<SqliteConnection, Task<T>> query, CancellationToken ct = default)
    {
        await using var conn = Open();
        return await query(conn).ConfigureAwait(false);
    }

    public T Read<T>(Func<SqliteConnection, T> query)
    {
        using var conn = Open();
        return query(conn);
    }

    /// <summary>库文件大小（字节），含 WAL。</summary>
    public long GetDatabaseSizeBytes()
    {
        long total = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = DatabasePath + suffix;
            if (File.Exists(path)) total += new FileInfo(path).Length;
        }
        return total;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 关闭前把 WAL 合并回主库文件。
        // 不做的话库数据会长期留在 .library.db-wal 里，主库文件看起来"是空的"，
        // 而且下次打开要多做一次恢复。
        TryCheckpoint();

        _writeGate.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private void TryCheckpoint()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // 强杀/断电场景下 WAL 恢复由 SQLite 自己负责，这里失败不影响正确性
        }
    }
}

/// <summary>
/// 从程序集内嵌资源按序号执行迁移脚本（设计文档 §4.4）。
/// </summary>
internal static class MigrationRunner
{
    private static readonly Regex VersionPattern = new(@"^(\d+)_", RegexOptions.Compiled);

    /// <summary>匹配内嵌资源名，如 <c>MediaLibrary.Data.Migrations.001_init.sql</c>。</summary>
    private static readonly Regex ResourcePattern = new(@"\.(\d+)_([A-Za-z0-9_]+)\.sql$", RegexOptions.Compiled);

    internal static int Migrate(LibraryDb db)
    {
        var scripts = LoadScripts();
        if (scripts.Count == 0) return 0;

        using var conn = db.Open();

        var current = GetCurrentVersion(conn);

        foreach (var (version, name, sql) in scripts)
        {
            if (version <= current) continue;

            using var tx = conn.BeginTransaction();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }

                if (version > 1)
                {
                    using var update = conn.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText = "UPDATE SchemaInfo SET Version = $v;";
                    update.Parameters.AddWithValue("$v", version);
                    update.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                throw new InvalidOperationException($"迁移脚本 {name} 执行失败：{ex.Message}", ex);
            }
        }

        return GetCurrentVersion(conn);
    }

    private static int GetCurrentVersion(SqliteConnection conn)
    {
        try
        {
            var hasTable = conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SchemaInfo';");
            if (hasTable == 0) return 0;

            return conn.ExecuteScalar<int>("SELECT Version FROM SchemaInfo LIMIT 1;");
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static List<(int Version, string Name, string Sql)> LoadScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var result = new List<(int, string, string)>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ResourcePattern.Match(resource);
            if (!match.Success) continue;

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);

            result.Add((
                int.Parse(match.Groups[1].Value),
                match.Groups[2].Value,
                reader.ReadToEnd()));
        }

        return result.OrderBy(r => r.Item1).ToList();
    }
}
