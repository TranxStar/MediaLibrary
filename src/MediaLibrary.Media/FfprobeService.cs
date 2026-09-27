using System.Text.Json;
using System.Text.Json.Serialization;
using MediaLibrary.Core.Abstractions;
using MediaLibrary.Core.Models;

namespace MediaLibrary.Media;

/// <summary>
/// ffprobe 封装（设计文档 §5.4）。
///
/// 用途：入库时填充元数据（尺寸/时长/编码/HDR 判定），以及决定转码参数。
/// </summary>
public sealed class FfprobeService : IMediaProbe
{
    private readonly SemaphoreSlim _gate;

    public FfprobeService(int maxConcurrency = 4)
    {
        _gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
    }

    /// <summary>探测失败（文件损坏/格式不支持/ffprobe 缺失）返回 null。</summary>
    public async Task<MediaProbe?> ProbeAsync(string path, CancellationToken ct = default)
    {
        var ffprobe = FfmpegLocator.FfprobePath;
        if (ffprobe is null || !File.Exists(path)) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await ProcessRunner.RunAsync(ffprobe, [
                "-v", "quiet",
                "-print_format", "json",
                "-show_format",
                "-show_streams",
                path,
            ], ct).ConfigureAwait(false);

            if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput)) return null;

            return Parse(result.StandardOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static MediaProbe? Parse(string json)
    {
        ProbeRoot? root;
        try
        {
            root = JsonSerializer.Deserialize<ProbeRoot>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?.Streams is null || root.Streams.Count == 0) return null;

        var video = root.Streams.FirstOrDefault(s => s.CodecType == "video");
        var audioStreams = root.Streams.Where(s => s.CodecType == "audio").ToList();
        var audio = audioStreams.FirstOrDefault();

        var durationMs = ParseSeconds(root.Format?.Duration)
                         ?? ParseSeconds(video?.Duration)
                         ?? ParseSeconds(audio?.Duration)
                         ?? 0;

        var pixFmt = video?.PixFmt;

        return new MediaProbe
        {
            HasVideo = video is not null,
            HasAudio = audio is not null,
            AudioStreamCount = audioStreams.Count,
            DurationMs = durationMs,
            Width = video?.Width,
            Height = video?.Height,
            VideoCodec = video?.CodecName,
            AudioCodec = audio?.CodecName,
            PixFmt = pixFmt,
            BitDepth = InferBitDepth(video?.BitsPerRawSample, pixFmt),
            ColorTransfer = video?.ColorTransfer,
            Rotation = InferRotation(video),
        };
    }

    private static long? ParseSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? (long)Math.Round(seconds * 1000)
            : null;
    }

    private static int InferBitDepth(string? bitsPerRawSample, string? pixFmt)
    {
        if (int.TryParse(bitsPerRawSample, out var bits) && bits > 0) return bits;

        if (pixFmt is null) return 8;
        if (pixFmt.Contains("12", StringComparison.Ordinal)) return 12;
        if (pixFmt.Contains("10", StringComparison.Ordinal)) return 10;
        return 8;
    }

    /// <summary>
    /// 取旋转角度。较新的 ffprobe 放在 <c>side_data_list[].rotation</c>，
    /// 老版本放在 <c>tags.rotate</c>，两者都兼容。
    /// </summary>
    private static int InferRotation(ProbeStream? video)
    {
        if (video is null) return 0;

        var sideRotation = video.SideData?.FirstOrDefault(d => d.Rotation is not null)?.Rotation;
        var degrees = sideRotation
                      ?? (video.Tags is not null && video.Tags.TryGetValue("rotate", out var raw)
                          && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                              ? parsed
                              : null);

        if (degrees is null) return 0;

        var normalized = ((int)Math.Round(degrees.Value) % 360 + 360) % 360;
        return normalized;
    }

    // ── JSON DTO ──────────────────────────────────────────────

    private sealed class ProbeRoot
    {
        [JsonPropertyName("streams")] public List<ProbeStream>? Streams { get; set; }
        [JsonPropertyName("format")] public ProbeFormat? Format { get; set; }
    }

    private sealed class ProbeStream
    {
        [JsonPropertyName("codec_type")] public string? CodecType { get; set; }
        [JsonPropertyName("codec_name")] public string? CodecName { get; set; }
        [JsonPropertyName("width")] public int? Width { get; set; }
        [JsonPropertyName("height")] public int? Height { get; set; }
        [JsonPropertyName("pix_fmt")] public string? PixFmt { get; set; }
        [JsonPropertyName("bits_per_raw_sample")] public string? BitsPerRawSample { get; set; }
        [JsonPropertyName("color_transfer")] public string? ColorTransfer { get; set; }
        [JsonPropertyName("duration")] public string? Duration { get; set; }
        [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }
        [JsonPropertyName("side_data_list")] public List<ProbeSideData>? SideData { get; set; }
    }

    private sealed class ProbeSideData
    {
        [JsonPropertyName("rotation")] public double? Rotation { get; set; }
    }

    private sealed class ProbeFormat
    {
        [JsonPropertyName("duration")] public string? Duration { get; set; }
        [JsonPropertyName("format_name")] public string? FormatName { get; set; }
    }
}
