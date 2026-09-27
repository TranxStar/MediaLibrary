namespace MediaLibrary.Core.Models;

/// <summary>ffprobe 探测结果。用于入库时填充元数据、以及决定转码参数。</summary>
public sealed class MediaProbe
{
    public bool HasVideo { get; init; }
    public bool HasAudio { get; init; }
    public int AudioStreamCount { get; init; }

    public long DurationMs { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }

    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? PixFmt { get; init; }

    /// <summary>每通道位数（8 / 10 / 12）。</summary>
    public int BitDepth { get; init; } = 8;

    /// <summary>色彩传递特性，如 <c>smpte2084</c>(PQ) / <c>arib-std-b67</c>(HLG)。</summary>
    public string? ColorTransfer { get; init; }

    /// <summary>显示矩阵旋转角度（手机竖拍视频常见 90/270）。</summary>
    public int Rotation { get; init; }

    /// <summary>
    /// 是否 HDR / 10-bit 以上。
    /// 设计文档 §5.4：检测到 HDR 时保留 10-bit（main10 + p010le），不做有损压平。
    /// </summary>
    public bool IsHdr =>
        BitDepth > 8
        || string.Equals(ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 音频能否直接 <c>-c:a copy</c> 进 MP4 容器。
    /// PCM / FLAC 等装不进去，必须转 AAC（见设计文档 §5.4）。
    /// </summary>
    public bool CanCopyAudioToMp4 =>
        AudioCodec is "aac" or "mp3" or "ac3" or "eac3" or "alac" or "opus";
}
