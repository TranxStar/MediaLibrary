using System.Buffers;
using MediaLibrary.Core.Abstractions;

namespace MediaLibrary.Media;

/// <summary>
/// 照片 → PNG（设计文档 §5.5）。
///
/// 三个关键参数（均有阶段0 实测支撑）：
/// <list type="bullet">
///   <item><c>-map 0:v:0</c> —— HEIC 是多图容器（主图 + 缩略图子图），必须显式取第一路</item>
///   <item><c>-pix_fmt rgb24</c> —— 输出 8-bit PNG</item>
///   <item><c>-pred mixed -compression_level 6</c> —— 实测最优，比默认小 9% 且比 Magick.NET 快 1.7 倍</item>
/// </list>
///
/// EXIF 方向由 ffmpeg 自动应用（实测 1600x900 → 900x1600），无需手写 transpose。
/// </summary>
public sealed class ImageConverter : IImageConverter
{
    public async Task<ProcessOutcome> ConvertToPngAsync(
        ProcessRequest request,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var ffmpeg = FfmpegLocator.FfmpegPath;
        if (ffmpeg is null) return ProcessOutcome.Fail("未找到 ffmpeg");

        if (!File.Exists(request.SourcePath))
            return ProcessOutcome.Fail("源文件不存在");

        CleanupTemp(request.TempPath);

        // 单张图片没有"进度"可言，直接给 0 和 1，界面显示为不确定态即可
        progress?.Report(0);

        var result = await ProcessRunner.RunAsync(ffmpeg, [
            "-hide_banner", "-y", "-nostdin",
            "-i", request.SourcePath,
            "-map", "0:v:0",
            "-frames:v", "1",
            "-pix_fmt", "rgb24",
            "-pred", "mixed",
            "-compression_level", "6",
            "-update", "1",
            request.TempPath,
        ], ct).ConfigureAwait(false);

        if (!result.Success)
        {
            CleanupTemp(request.TempPath);
            return ProcessOutcome.Fail(Summarize(result));
        }

        if (!File.Exists(request.TempPath) || new FileInfo(request.TempPath).Length == 0)
        {
            CleanupTemp(request.TempPath);
            return ProcessOutcome.Fail("转换进程正常退出，但没有产生输出文件");
        }

        progress?.Report(1.0);
        return ProcessOutcome.Ok;
    }

    private static string Summarize(ProcessResult result)
    {
        var tail = result.Tail(4);

        if (tail.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase) ||
            tail.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase))
            return $"文件已损坏或格式无法识别：\n{tail}";

        if (tail.Contains("No space left", StringComparison.OrdinalIgnoreCase))
            return "磁盘空间不足";

        return tail;
    }

    private static void CleanupTemp(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// 音频等无需转码的文件：直接字节复制（设计文档 §5.6）。
///
/// 走纯托管流复制而不是 ffmpeg，理由是：不重新编码、不丢元数据、不依赖外部进程、
/// 而且能给出准确的字节级进度。
/// </summary>
public sealed class FileCopier : IFileCopier
{
    private const int BufferSize = 1024 * 1024;

    public async Task<ProcessOutcome> CopyAsync(
        ProcessRequest request,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        if (!File.Exists(request.SourcePath))
            return ProcessOutcome.Fail("源文件不存在");

        CleanupTemp(request.TempPath);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            await using var source = new FileStream(
                request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using var destination = new FileStream(
                request.TempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var total = source.Length;
            long copied = 0;
            var lastPercent = -1;

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false);
                if (read <= 0) break;

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;

                if (total <= 0 || progress is null) continue;

                var percent = (int)(copied * 100 / total);
                if (percent == lastPercent) continue;

                lastPercent = percent;
                progress.Report((double)copied / total);
            }

            await destination.FlushAsync(ct).ConfigureAwait(false);

            if (copied == 0)
            {
                CleanupTemp(request.TempPath);
                return ProcessOutcome.Fail("源文件为空");
            }

            progress?.Report(1.0);
            return ProcessOutcome.Ok;
        }
        catch (OperationCanceledException)
        {
            CleanupTemp(request.TempPath);
            throw;
        }
        catch (Exception ex)
        {
            CleanupTemp(request.TempPath);
            return ProcessOutcome.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CleanupTemp(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
