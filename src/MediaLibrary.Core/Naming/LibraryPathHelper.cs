using MediaLibrary.Core.Models;

namespace MediaLibrary.Core.Naming;

/// <summary>拖入的一个文件，以及它在库内应归属的目录。</summary>
public readonly record struct DropItem(string SourcePath, string FolderKey);

/// <summary>库根目录下的一个媒体文件。</summary>
public readonly record struct LibraryFile(string AbsolutePath, string FolderKey, string FileName);

/// <summary>
/// 库内路径规则（设计文档 §5.2）。
///
/// 核心规则：<b>拖入的文件夹本身成为库内的一层目录</b>，其子树在库内镜像。
/// 文件名前缀取 <see cref="FolderKey"/> 的最内层目录名。
///
/// 例：
/// <code>
/// 拖入 D:\素材\运动会\                → FolderKey "运动会"              → 运动会_001.mp4
/// 拖入 D:\素材\运动会\开幕式\          → FolderKey "开幕式"              → 开幕式_001.mp4
///     其中 D:\素材\运动会\开幕式\颁奖\  → FolderKey "开幕式/颁奖"        → 颁奖_001.mp4
/// 拖入单个文件 E:\随便\VID_1234.mp4    → FolderKey "随便"                → 随便_001.mp4
/// </code>
/// </summary>
public static class LibraryPathHelper
{
    /// <summary>无法确定文件夹名时的兜底目录。</summary>
    public const string FallbackFolder = "未分类";

    /// <summary>FolderKey 的分隔符统一用正斜杠，与磁盘分隔符解耦。</summary>
    public const char Separator = '/';

    /// <summary>库内保留目录，扫描时一律忽略。</summary>
    public static readonly string[] ReservedDirNames = [".tmp", ".cache"];

    /// <summary>把磁盘相对目录转成 FolderKey（统一分隔符 + 清洗每一段）。</summary>
    public static string ToFolderKey(string? relativeDir)
    {
        if (string.IsNullOrWhiteSpace(relativeDir)) return "";

        var parts = relativeDir
            .Replace('\\', Separator)
            .Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p is not "." and not "..")
            .Select(NamingEngine.SanitizeFolderName)
            .ToArray();

        return string.Join(Separator, parts);
    }

    /// <summary>拼接 FolderKey。</summary>
    public static string Combine(string parentKey, string childName)
    {
        var child = NamingEngine.SanitizeFolderName(childName);
        return string.IsNullOrEmpty(parentKey) ? child : $"{parentKey}{Separator}{child}";
    }

    /// <summary>由 FolderKey 得到文件名的前缀。</summary>
    public static string PrefixFor(string folderKey) => NamingEngine.InnermostFolderName(folderKey);

    /// <summary>
    /// 展开拖入的路径为待入库文件清单。
    /// 目录会递归展开并镜像其子目录结构；单个文件取其父目录名作为 FolderKey。
    /// </summary>
    public static IEnumerable<DropItem> ExpandDrop(IEnumerable<string> droppedPaths)
    {
        foreach (var raw in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            if (Directory.Exists(raw))
            {
                var rootName = NamingEngine.SanitizeFolderName(new DirectoryInfo(raw).Name);
                foreach (var absolute in EnumerateMediaFiles(raw))
                {
                    var relDir = Path.GetRelativePath(raw, Path.GetDirectoryName(absolute)!);
                    var sub = ToFolderKey(relDir);
                    var key = string.IsNullOrEmpty(sub) ? rootName : $"{rootName}{Separator}{sub}";
                    yield return new DropItem(absolute, key);
                }
            }
            else if (File.Exists(raw))
            {
                if (MediaKindClassifier.Classify(raw) is null) continue;

                var parent = Path.GetDirectoryName(raw);
                var name = string.IsNullOrEmpty(parent)
                    ? FallbackFolder
                    : new DirectoryInfo(parent).Name;

                // 盘符根目录（如 E:\）没有有意义的目录名
                if (string.IsNullOrWhiteSpace(name) || name.EndsWith(':'))
                    name = FallbackFolder;

                yield return new DropItem(raw, NamingEngine.SanitizeFolderName(name));
            }
        }
    }

    /// <summary>递归枚举目录下的媒体文件，跳过隐藏文件与库内保留目录。</summary>
    public static IEnumerable<string> EnumerateMediaFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var sub in subDirs)
            {
                var subName = Path.GetFileName(sub);
                if (ReservedDirNames.Contains(subName, StringComparer.OrdinalIgnoreCase)) continue;
                if (subName.StartsWith('.')) continue;
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.Hidden)) continue;
                stack.Push(sub);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith('.')) continue;
                if (!MediaKindClassifier.IsSupported(fileName)) continue;

                try
                {
                    if (new FileInfo(file).Attributes.HasFlag(FileAttributes.Hidden)) continue;
                }
                catch (IOException) { continue; }

                yield return file;
            }
        }
    }

    /// <summary>
    /// 扫描已存在的库根目录，得到库内全部媒体文件。
    /// 用于首次打开库、以及检测库文件被外部增删改。
    /// </summary>
    public static IEnumerable<LibraryFile> ScanLibrary(string libraryRoot)
    {
        if (!Directory.Exists(libraryRoot)) yield break;

        foreach (var absolute in EnumerateMediaFiles(libraryRoot))
        {
            var rel = Path.GetRelativePath(libraryRoot, absolute);
            var relDir = Path.GetDirectoryName(rel);
            yield return new LibraryFile(
                absolute,
                ToFolderKey(relDir),
                Path.GetFileName(absolute));
        }
    }
}
