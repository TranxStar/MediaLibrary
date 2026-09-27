using MediaLibrary.Core.Models;

namespace MediaLibrary.Core.Abstractions;

/// <summary>
/// 媒体处理请求。
///
/// <b>处理器只负责把结果写到 <see cref="TempPath"/>，不碰最终路径。</b>
/// 原子改名（temp → 最终路径）由调用方统一完成，
/// 这样无论处理器怎么失败，库里都不会出现半截文件。
/// </summary>
/// <param name="SourcePath">源文件绝对路径（转码期间绝不被修改）。</param>
/// <param name="TempPath">
/// 临时输出绝对路径。<b>扩展名必须是目标格式</b>（如 <c>xxx.tmp.mp4</c>），
/// 因为 ffmpeg 靠扩展名决定输出容器格式。
/// </param>
/// <param name="Probe">源文件的探测结果，用于决定转码参数与并发策略。</param>
/// <param name="ForceSdr">强制把 HDR 压成 SDR（默认 false，保留 10-bit）。</param>
public sealed record ProcessRequest(
    string SourcePath,
    string TempPath,
    MediaProbe? Probe,
    bool ForceSdr = false);

/// <summary>处理结果。</summary>
public sealed record ProcessOutcome(bool Success, string? ErrorMessage = null)
{
    public static readonly ProcessOutcome Ok = new(true);

    public static ProcessOutcome Fail(string message) => new(false, message);
}

/// <summary>视频 → H.265 转码器。</summary>
public interface IVideoTranscoder
{
    /// <summary>硬件编码（NVENC）当前是否可用。启动时探测一次并缓存。</summary>
    bool IsHardwareAccelerationAvailable { get; }

    /// <summary>实际使用的编码器名，用于界面提示与日志。</summary>
    string ActiveEncoder { get; }

    /// <summary>探测硬件编码可用性。启动时调用一次。</summary>
    Task ProbeHardwareAsync(CancellationToken ct = default);

    /// <summary><paramref name="progress"/> 取值范围 0.0 ~ 1.0。</summary>
    Task<ProcessOutcome> TranscodeAsync(
        ProcessRequest request, IProgress<double>? progress, CancellationToken ct = default);
}

/// <summary>照片 → PNG 转换器。</summary>
public interface IImageConverter
{
    Task<ProcessOutcome> ConvertToPngAsync(
        ProcessRequest request, IProgress<double>? progress, CancellationToken ct = default);
}

/// <summary>音频等无需转码的文件，直接字节复制。</summary>
public interface IFileCopier
{
    Task<ProcessOutcome> CopyAsync(
        ProcessRequest request, IProgress<double>? progress, CancellationToken ct = default);
}
