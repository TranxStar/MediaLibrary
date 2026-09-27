using System.Data;
using Dapper;
using MediaLibrary.Core.Models;

namespace MediaLibrary.Data;

/// <summary>
/// Dapper 枚举映射配置。
///
/// 枚举在库里存为 <b>可读的 TEXT</b>（'Video' / 'Pending'），
/// 而不是数字 —— 出问题时用 DB 工具直接看就能懂。
///
/// ⚠️ 注意：写入方向<b>不依赖</b>这里的类型处理器。
/// 实测发现 Dapper 对"把整个实体对象当参数"的场景不会应用注册的 SetValue，
/// 会退回存整数（Status 会变成 '2' 而不是 'Ready'，进而让所有按状态过滤的查询全部失效）。
/// 因此 <c>AssetRepository</c> 写库时一律显式传 <c>enum.ToString()</c>。
/// 本类只负责读取方向的 TEXT → 枚举。
/// </summary>
internal static class DapperSetup
{
    private static int _initialized;

    internal static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1) return;

        // 读方向：TEXT → 枚举
        SqlMapper.AddTypeHandler(new EnumStringHandler<AssetKind>());
        SqlMapper.AddTypeHandler(new EnumStringHandler<AssetStatus>());
        SqlMapper.AddTypeHandler(new EnumStringHandler<JobType>());
        SqlMapper.AddTypeHandler(new EnumStringHandler<JobStatus>());
    }

    private sealed class EnumStringHandler<T> : SqlMapper.TypeHandler<T> where T : struct, Enum
    {
        public override void SetValue(IDbDataParameter parameter, T value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToString();
        }

        public override T Parse(object value) => value switch
        {
            string s => Enum.Parse<T>(s, ignoreCase: true),
            long l => (T)Enum.ToObject(typeof(T), l),
            int i => (T)Enum.ToObject(typeof(T), i),
            _ => throw new DataException($"无法把 {value?.GetType().Name ?? "null"} 转换为 {typeof(T).Name}"),
        };
    }
}
