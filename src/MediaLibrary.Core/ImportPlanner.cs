using MediaLibrary.Core.Models;
using MediaLibrary.Core.Naming;

namespace MediaLibrary.Core;

/// <summary>
/// 入库后的目标格式（设计文档 §1.2 R6 / R7 / R8）。
/// </summary>
public static class TargetFormat
{
    /// <summary>视频统一转 H.265 装进 MP4。</summary>
    public const string VideoExtension = ".mp4";

    /// <summary>照片统一转 PNG。</summary>
    public const string PhotoExtension = ".png";

    /// <summary>音频保留原格式（仅复制）。</summary>
    public static string ExtensionFor(AssetKind kind, string sourcePathOrName) => kind switch
    {
        AssetKind.Video => VideoExtension,
        AssetKind.Photo => PhotoExtension,
        _ => Path.GetExtension(sourcePathOrName).ToLowerInvariant(),
    };

    /// <summary>目标格式是否与源格式相同（相同则仍需转码，但容器不变）。</summary>
    public static bool IsSameContainer(AssetKind kind, string sourcePathOrName)
        => string.Equals(
            ExtensionFor(kind, sourcePathOrName),
            Path.GetExtension(sourcePathOrName),
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 导入计划器（设计文档 §5.1 第 1 步 / §5.2 命名 / §5.3 去重）。
///
/// 这里只放<b>纯函数</b>逻辑（去重过滤、命名拼装），便于单元测试。
/// 需要数据库的部分（读取已有哈希、分配序号）由 <c>ImportService</c> 负责。
/// </summary>
public static class ImportPlanner
{
    public const string ReasonDuplicateInLibrary = "库内已存在相同内容";
    public const string ReasonDuplicateInBatch = "本次拖入内重复";
    public const string ReasonSourceMissing = "源文件不存在或无法读取";
    public const string ReasonUnsupported = "不支持的文件类型";

    /// <summary>
    /// 过滤掉重复与无效的文件。
    /// </summary>
    /// <param name="candidates">候选源文件（已算好哈希）。</param>
    /// <param name="existingHashes">库内已有内容的哈希集合。</param>
    /// <returns>可入库的项 + 被跳过的项。</returns>
    public static (List<SourceFile> Accepted, List<SkippedImport> Skipped) FilterDuplicates(
        IEnumerable<SourceFile> candidates,
        IReadOnlySet<string> existingHashes)
    {
        var accepted = new List<SourceFile>();
        var skipped = new List<SkippedImport>();

        // 同一次拖入里的重复也要拦掉：否则同一段素材会被转码两份
        var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (existingHashes.Contains(candidate.QuickHash))
            {
                skipped.Add(new SkippedImport(candidate.SourcePath, ReasonDuplicateInLibrary));
                continue;
            }

            if (!seenInBatch.Add(candidate.QuickHash))
            {
                skipped.Add(new SkippedImport(candidate.SourcePath, ReasonDuplicateInBatch));
                continue;
            }

            accepted.Add(candidate);
        }

        return (accepted, skipped);
    }

    /// <summary>
    /// 为源文件拼装库内文件名。
    ///
    /// 规则（设计文档 §5.2）：<c>{最内层文件夹名}_{序号:D3}.{目标扩展名}</c>
    /// </summary>
    public static PlannedImport Name(SourceFile source, int index)
    {
        var prefix = NamingEngine.InnermostFolderName(source.FolderKey);
        var extension = TargetFormat.ExtensionFor(source.Kind, source.SourcePath);
        var fileName = NamingEngine.BuildFileName(prefix, index, extension);

        var libraryPath = string.IsNullOrEmpty(source.FolderKey)
            ? fileName
            : $"{source.FolderKey}{LibraryPathHelper.Separator}{fileName}";

        return new PlannedImport(source, fileName, libraryPath);
    }
}
