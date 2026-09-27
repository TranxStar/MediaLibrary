namespace MediaLibrary.Core.Models;

/// <summary>一条素材记录。字段与 Assets 表一一对应（见设计文档 §4.1）。</summary>
public sealed class Asset
{
    public long Id { get; set; }

    public AssetKind Kind { get; set; }

    /// <summary>库内文件名，如 <c>开幕式_001.mp4</c>。</summary>
    public string FileName { get; set; } = "";

    /// <summary>归一化后的相对目录，如 <c>运动会/开幕式</c>；库根为 <c>""</c>。</summary>
    public string FolderKey { get; set; } = "";

    /// <summary>相对库根的路径，如 <c>运动会/开幕式/开幕式_001.mp4</c>。</summary>
    public string LibraryPath { get; set; } = "";

    /// <summary>原始来源绝对路径。转码完成后源文件即与本库无关，可能失效。</summary>
    public string? SourcePath { get; set; }

    /// <summary>拖入时最内层文件夹名，用于命名。</summary>
    public string? SourceFolder { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>XxHash64(大小 ‖ 首 64KB ‖ 尾 64KB) 的十六进制。</summary>
    public string QuickHash { get; set; } = "";

    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? DurationMs { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? PixFmt { get; set; }
    public bool IsHdr { get; set; }
    public int Rotation { get; set; }

    /// <summary>EXIF 拍摄时间，ISO8601。</summary>
    public string? TakenAt { get; set; }
    public string? CameraModel { get; set; }

    /// <summary>缩略图文件名（相对 .cache/thumbs）。</summary>
    public string? ThumbPath { get; set; }

    /// <summary>元数据/缩略图补全的尝试次数。达到上限后不再重试，避免死循环。</summary>
    public int EnrichAttempts { get; set; }

    public AssetStatus Status { get; set; }

    public string? ErrorMessage { get; set; }

    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";

    /// <summary>扩展名（含点，小写）。</summary>
    public string Extension => System.IO.Path.GetExtension(FileName).ToLowerInvariant();

    /// <summary>不含扩展名的显示名。</summary>
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(FileName);

    /// <summary>时长的显示文本；无时长返回空串。</summary>
    public string DurationText
    {
        get
        {
            if (DurationMs is not { } ms || ms <= 0) return "";
            var t = TimeSpan.FromMilliseconds(ms);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";
        }
    }
}
