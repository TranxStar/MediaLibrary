using MediaLibrary.Core.Models;

namespace MediaLibrary.Core.Models;

/// <summary>扫描到的待导入源文件。</summary>
/// <param name="SourcePath">源文件绝对路径。</param>
/// <param name="FolderKey">应归属的库内目录（正斜杠）。</param>
/// <param name="Kind">素材种类。</param>
/// <param name="SizeBytes">源文件大小。</param>
/// <param name="QuickHash">内容快速哈希。</param>
public sealed record SourceFile(
    string SourcePath,
    string FolderKey,
    AssetKind Kind,
    long SizeBytes,
    string QuickHash);

/// <summary>已分配好库内文件名的待导入项。</summary>
public sealed record PlannedImport(
    SourceFile Source,
    string FileName,
    string LibraryPath);

/// <summary>被跳过的文件及原因。</summary>
public sealed record SkippedImport(string SourcePath, string Reason);

/// <summary>导入计划。</summary>
public sealed record ImportPlan(
    IReadOnlyList<PlannedImport> Items,
    IReadOnlyList<SkippedImport> Skipped)
{
    public static readonly ImportPlan Empty = new([], []);

    public int TotalSkipped => Skipped.Count;

    /// <summary>按跳过原因归类统计，用于界面汇总提示。</summary>
    public IReadOnlyDictionary<string, int> SkipSummary =>
        Skipped.GroupBy(s => s.Reason).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>导入过程中的进度反馈。</summary>
public sealed record ImportProgress(string Stage, int Processed, int Total, string CurrentItem);

/// <summary>导入任务的执行进度（转码/转换阶段）。</summary>
public sealed record JobProgress(
    long AssetId,
    string FileName,
    JobType JobType,
    double Fraction,
    JobStatus Status,
    string? ErrorMessage = null);
