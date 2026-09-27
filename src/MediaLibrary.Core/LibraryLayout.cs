namespace MediaLibrary.Core;

/// <summary>
/// 素材库在磁盘上的布局（设计文档 §3.3）。
///
/// <code>
/// E:\MediaLibrary\                  ← 库根（可配置，不硬编码）
/// ├─ .library.db                    SQLite 主库
/// ├─ .cache\thumbs\{hash}.jpg       缩略图缓存
/// ├─ .tmp\                          转码临时区（必须与库根同盘，保证原子移动）
/// ├─ 运动会\开幕式\开幕式_001.mp4
/// └─ 元旦晚会\元旦晚会_001.mp4
/// </code>
/// </summary>
public sealed class LibraryLayout
{
    public const string DatabaseFileName = ".library.db";
    public const string CacheDirName = ".cache";
    public const string ThumbDirName = "thumbs";
    public const string TempDirName = ".tmp";

    /// <summary>单个库的缩略图缓存上限（字节）。超出后按 LRU 清理。</summary>
    public const long ThumbCacheLimitBytes = 2L * 1024 * 1024 * 1024;

    public LibraryLayout(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("库根目录不能为空", nameof(root));

        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Root.Length == 2 && Root[1] == ':') Root += Path.DirectorySeparatorChar;
    }

    public string Root { get; }

    public string DatabasePath => Path.Combine(Root, DatabaseFileName);
    public string CacheDir => Path.Combine(Root, CacheDirName);
    public string ThumbDir => Path.Combine(CacheDir, ThumbDirName);
    public string TempDir => Path.Combine(Root, TempDirName);

    /// <summary>库内相对路径 → 绝对路径。</summary>
    public string ToAbsolute(string libraryRelativePath)
        => Path.Combine(Root, libraryRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>绝对路径 → 库内相对路径（正斜杠）。不在库内时返回 null。</summary>
    public string? ToRelative(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return null;

        var rel = Path.GetRelativePath(Root, full);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// 缩略图绝对路径。参数既可以是【裸哈希】也可以是【相对文件名】（带 .jpg）——
    /// 历史上 DB 存的是相对文件名（如 <c>abc.jpg</c>），而调用方两种都传过；
    /// 统一在此容错，避免"abc.jpg.jpg"双重扩展名导致缩略图永远加载失败。
    /// </summary>
    public string ThumbAbsolutePath(string hashOrRelativeName)
        => Path.Combine(ThumbDir,
            hashOrRelativeName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? hashOrRelativeName
                : hashOrRelativeName + ".jpg");

    public string ThumbRelativeName(string hash) => hash + ".jpg";

    /// <summary>目录对应的绝对路径（FolderKey 为空时即库根）。</summary>
    public string FolderAbsolutePath(string folderKey)
        => string.IsNullOrEmpty(folderKey)
            ? Root
            : ToAbsolute(folderKey);

    /// <summary>创建库必需目录，并把内部目录设为隐藏。</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(ThumbDir);
        Directory.CreateDirectory(TempDir);

        foreach (var dir in new[] { CacheDir, TempDir })
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (!info.Attributes.HasFlag(FileAttributes.Hidden))
                    info.Attributes |= FileAttributes.Hidden;
            }
            catch (IOException) { /* 隐藏失败不影响功能 */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>该路径是否看起来像一个已存在的素材库。</summary>
    public static bool LooksLikeLibrary(string path)
        => Directory.Exists(path) && File.Exists(Path.Combine(path, DatabaseFileName));

    /// <summary>库是否可写。</summary>
    public bool IsWritable()
    {
        try
        {
            Directory.CreateDirectory(Root);
            var probe = Path.Combine(Root, $".writetest_{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>库所在磁盘的可用空间（字节）。</summary>
    public long GetFreeSpaceBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Root);
            if (string.IsNullOrEmpty(root)) return 0;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 清理 .tmp 下崩溃残留的孤儿临时文件（设计文档 §5.1 崩溃恢复）。
    ///
    /// 注意：临时文件名形如 <c>&lt;guid&gt;.tmp.mp4</c> —— 结尾必须是目标扩展名，
    /// 因为 ffmpeg 靠扩展名决定输出容器格式。所以这里递归清空整个 .tmp 目录，
    /// 不能按 <c>*.tmp</c> 过滤。
    /// </summary>
    public int CleanOrphanTempFiles()
    {
        if (!Directory.Exists(TempDir)) return 0;

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(TempDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
                count++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return count;
    }
}
