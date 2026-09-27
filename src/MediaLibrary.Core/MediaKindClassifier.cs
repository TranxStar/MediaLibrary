namespace MediaLibrary.Core;

using MediaLibrary.Core.Models;

/// <summary>按扩展名判定素材种类。</summary>
public static class MediaKindClassifier
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".flv", ".webm", ".m4v",
        ".mpg", ".mpeg", ".m2v", ".ts", ".m2ts", ".mts", ".3gp", ".3g2",
        ".mxf", ".vob", ".ogv", ".dv", ".rmvb", ".asf", ".f4v",
        // 专业摄像机格式（LibVLC 能播，但转码前需确认 ffmpeg 支持）
        ".r3d", ".braw", ".crm",
    };

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".heic", ".heif",
        ".webp", ".bmp", ".tif", ".tiff", ".gif", ".avif", ".tga", ".dng",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".aac", ".m4a", ".flac", ".ogg", ".oga", ".wma",
        ".aif", ".aiff", ".opus", ".ape", ".alac", ".amr", ".ac3",
    };

    /// <summary>无法识别时返回 null。</summary>
    public static AssetKind? Classify(string pathOrFileName)
    {
        var ext = Path.GetExtension(pathOrFileName);
        if (string.IsNullOrEmpty(ext)) return null;

        if (VideoExtensions.Contains(ext)) return AssetKind.Video;
        if (PhotoExtensions.Contains(ext)) return AssetKind.Photo;
        if (AudioExtensions.Contains(ext)) return AssetKind.Audio;
        return null;
    }

    public static bool IsSupported(string pathOrFileName) => Classify(pathOrFileName) is not null;

    /// <summary>枚举所有受支持的扩展名（用于文件对话框过滤）。</summary>
    public static IReadOnlyCollection<string> AllExtensions =>
        VideoExtensions.Concat(PhotoExtensions).Concat(AudioExtensions)
            .Select(e => e.TrimStart('.'))
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
