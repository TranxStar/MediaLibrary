namespace MediaLibrary.Media;

/// <summary>
/// 定位 ffmpeg / ffprobe 可执行文件。
///
/// 搜索顺序（找到即止）：
/// <list type="number">
///   <item>构造时传入的显式目录</item>
///   <item>环境变量 <c>MEDIALIBRARY_FFMPEG</c>（指向目录或 exe）</item>
///   <item><c>&lt;程序目录&gt;\Tools\</c>（发布后的形态）</item>
///   <item>从程序目录向上找 <c>tools\ffmpeg\</c>（开发时的形态，避免每次构建拷贝 314MB）</item>
///   <item>系统 PATH</item>
/// </list>
/// </summary>
public static class FfmpegLocator
{
    private const int MaxSearchDepth = 8;

    private static string? _ffmpeg;
    private static string? _ffprobe;
    private static string _diagnostics = "尚未初始化";

    public static string? FfmpegPath => _ffmpeg;
    public static string? FfprobePath => _ffprobe;
    public static bool IsAvailable => _ffmpeg is not null;
    public static string Diagnostics => _diagnostics;

    /// <summary>定位并缓存路径。</summary>
    public static void Initialize(string? explicitDirectory = null)
    {
        var candidates = new List<(string Dir, string Source)>();

        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            AddCandidate(candidates, explicitDirectory, "显式指定");

        var env = Environment.GetEnvironmentVariable("MEDIALIBRARY_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env))
            AddCandidate(candidates, env, "环境变量 MEDIALIBRARY_FFMPEG");

        var baseDir = AppContext.BaseDirectory;
        AddCandidate(candidates, Path.Combine(baseDir, "Tools"), "程序目录\\Tools");

        // 开发时：从程序目录向上找仓库根的 tools\ffmpeg
        var dir = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < MaxSearchDepth && dir is not null; depth++, dir = dir.Parent)
        {
            AddCandidate(candidates, Path.Combine(dir.FullName, "tools", "ffmpeg"), $"向上 {depth + 1} 层");
        }

        foreach (var (candidateDir, source) in candidates)
        {
            var ffmpeg = ResolveIn(candidateDir, "ffmpeg.exe");
            var ffprobe = ResolveIn(candidateDir, "ffprobe.exe");
            if (ffmpeg is null) continue;

            _ffmpeg = ffmpeg;
            _ffprobe = ffprobe ?? Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
            if (!File.Exists(_ffprobe)) _ffprobe = null;

            _diagnostics = $"找到 ffmpeg（来源：{source}）\n  ffmpeg : {_ffmpeg}\n  ffprobe: {_ffprobe ?? "(缺失)"}";
            return;
        }

        // 兜底：PATH
        var onPath = FindOnPath("ffmpeg.exe");
        if (onPath is not null)
        {
            _ffmpeg = onPath;
            _ffprobe = FindOnPath("ffprobe.exe");
            _diagnostics = $"找到 ffmpeg（来源：PATH）\n  ffmpeg : {_ffmpeg}\n  ffprobe: {_ffprobe ?? "(缺失)"}";
            return;
        }

        _diagnostics = "未找到 ffmpeg.exe。已尝试：\n  " +
                       string.Join("\n  ", candidates.Select(c => c.Dir)) +
                       "\n  PATH\n请把 ffmpeg.exe / ffprobe.exe 放到 tools\\ffmpeg\\ 下。";
    }

    private static void AddCandidate(List<(string, string)> list, string dir, string source)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            if (!list.Any(x => string.Equals(x.Item1, full, StringComparison.OrdinalIgnoreCase)))
                list.Add((full, source));
        }
        catch (Exception)
        {
            // 非法路径忽略
        }
    }

    private static string? ResolveIn(string directory, string fileName)
    {
        // 传入的可能是目录，也可能直接是 exe
        if (File.Exists(directory) &&
            Path.GetFileName(directory).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(directory);

        if (!Directory.Exists(directory)) return null;

        var direct = Path.Combine(directory, fileName);
        if (File.Exists(direct)) return direct;

        // 也允许 bin\ 子目录
        var inBin = Path.Combine(directory, "bin", fileName);
        return File.Exists(inBin) ? inBin : null;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(segment.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
                // 忽略非法 PATH 段
            }
        }

        return null;
    }
}
