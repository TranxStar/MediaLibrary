using MediaLibrary.Core.Abstractions;
using MediaLibrary.Core.Models;

namespace MediaLibrary.Media;

/// <summary>
/// 缩略图生成（设计文档 §5.7）。
///
/// <list type="bullet">
///   <item><b>视频</b>：抽取 <c>min(时长 10%, 10 秒)</c> 处的一帧 —— 避开片头黑场，比固定抽第 1 帧有意义</item>
///   <item><b>照片</b>：直接缩放（ffmpeg 会自动应用 EXIF 方向，见阶段0报告 §5）</item>
///   <item><b>音频</b>：生成波形图 —— 对剪辑而言比专辑封面有用</item>
/// </list>
///
/// 输出统一为 320x180 内等比缩放（不裁切），由界面做信箱式填充，避免照片被切掉内容。
/// </summary>
public sealed class ThumbnailService : IThumbnailer
{
    public const int ThumbWidth = 320;
    public const int ThumbHeight = 180;

    /// <summary>音频波形只取前 10 分钟，避免长录音把缩略图队列卡住。</summary>
    private const int WaveformMaxSeconds = 600;

    private static readonly TimeSpan PerItemTimeout = TimeSpan.FromSeconds(45);

    private readonly SemaphoreSlim _gate;

    public ThumbnailService(int maxConcurrency = 4)
    {
        _gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
    }

    public async Task<bool> CreateAsync(
        string sourcePath,
        AssetKind kind,
        string outputPath,
        long? durationMs = null,
        CancellationToken ct = default)
    {
        // 缓存命中：同一素材重复出现时零成本复用
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0) return true;

        if (!FfmpegLocator.IsAvailable || !File.Exists(sourcePath)) return false;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PerItemTimeout);

            return kind switch
            {
                AssetKind.Video => await CreateVideoThumbAsync(sourcePath, outputPath, durationMs, timeout.Token)
                    .ConfigureAwait(false),
                AssetKind.Photo => await CreatePhotoThumbAsync(sourcePath, outputPath, timeout.Token)
                    .ConfigureAwait(false),
                AssetKind.Audio => await CreateWaveformAsync(sourcePath, outputPath, timeout.Token)
                    .ConfigureAwait(false),
                _ => false,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;   // 单项超时
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 视频 ──────────────────────────────────────────────────

    private static async Task<bool> CreateVideoThumbAsync(
        string source, string output, long? durationMs, CancellationToken ct)
    {
        var seek = ComputeSeekSeconds(durationMs);

        // 先按算出的位置抽帧（-ss 放在 -i 前 = 关键帧级快速定位，快几十倍）
        if (await ExtractFrameAsync(source, output, seek, ct).ConfigureAwait(false)) return true;

        // 兜底：位置可能超出文件长度，退回第 0 秒再试一次
        return seek > 0 && await ExtractFrameAsync(source, output, 0, ct).ConfigureAwait(false);
    }

    private static async Task<bool> ExtractFrameAsync(
        string source, string output, double seekSeconds, CancellationToken ct)
    {
        var temp = MakeTempPath(output);

        try
        {
            var result = await ProcessRunner.RunAsync(FfmpegLocator.FfmpegPath!, [
                "-hide_banner", "-y", "-nostdin",
                "-ss", seekSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                "-i", source,
                "-frames:v", "1",
                "-vf", $"scale={ThumbWidth}:{ThumbHeight}:force_original_aspect_ratio=decrease",
                "-q:v", "3",
                "-update", "1",
                temp,
            ], ct).ConfigureAwait(false);

            return Commit(result, temp, output);
        }
        catch (OperationCanceledException)
        {
            Cleanup(temp);
            throw;
        }
    }

    /// <summary>
    /// 从已知时长推算抽帧位置：时长的 10%，最多 10 秒。
    /// 时长未知时退到 1 秒。
    /// </summary>
    internal static double ComputeSeekSeconds(long? durationMs)
    {
        if (durationMs is not { } ms || ms <= 0) return 1.0;

        var seconds = ms / 1000.0;
        var seek = Math.Min(seconds * 0.1, 10.0);

        // 很短的片子直接从头抽
        return seek < 0.2 ? 0 : seek;
    }

    // ── 照片 ──────────────────────────────────────────────────

    private static async Task<bool> CreatePhotoThumbAsync(string source, string output, CancellationToken ct)
    {
        var temp = MakeTempPath(output);

        try
        {
            var result = await ProcessRunner.RunAsync(FfmpegLocator.FfmpegPath!, [
                "-hide_banner", "-y", "-nostdin",
                "-i", source,
                // HEIC 是多图容器（主图 + 缩略图子图），必须显式取第一路，否则行为不确定
                "-map", "0:v:0",
                "-frames:v", "1",
                "-vf", $"scale={ThumbWidth}:{ThumbHeight}:force_original_aspect_ratio=decrease",
                "-q:v", "3",
                "-update", "1",
                temp,
            ], ct).ConfigureAwait(false);

            return Commit(result, temp, output);
        }
        catch (OperationCanceledException)
        {
            Cleanup(temp);
            throw;
        }
    }

    // ── 音频波形 ──────────────────────────────────────────────

    private static async Task<bool> CreateWaveformAsync(string source, string output, CancellationToken ct)
    {
        var temp = MakeTempPath(output);

        try
        {
            var result = await ProcessRunner.RunAsync(FfmpegLocator.FfmpegPath!, [
                "-hide_banner", "-y", "-nostdin",
                "-t", WaveformMaxSeconds.ToString(),
                "-i", source,
                "-filter_complex", $"showwavespic=s={ThumbWidth}x{ThumbHeight}:colors=0x4A9EFF",
                "-frames:v", "1",
                "-update", "1",
                temp,
            ], ct).ConfigureAwait(false);

            return Commit(result, temp, output);
        }
        catch (OperationCanceledException)
        {
            Cleanup(temp);
            throw;
        }
    }

    // ── 落盘工具 ──────────────────────────────────────────────

    /// <summary>
    /// 临时文件名保留 <c>.jpg</c> 结尾 —— ffmpeg 靠扩展名判断输出格式。
    /// 与目标同目录，保证 File.Move 是原子的（同卷）。
    /// </summary>
    private static string MakeTempPath(string output)
        => Path.Combine(
            Path.GetDirectoryName(output)!,
            $"{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(output)}");

    private static bool Commit(ProcessResult result, string temp, string output)
    {
        try
        {
            if (!result.Success || !File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                Cleanup(temp);
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Move(temp, output, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            Cleanup(temp);
            return false;
        }
    }

    private static void Cleanup(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
