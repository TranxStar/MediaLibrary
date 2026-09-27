using MediaLibrary.Core.Abstractions;

namespace MediaLibrary.Media;

/// <summary>
/// 视频 → H.265 转码（设计文档 §5.4）。
///
/// 优先用 RTX 显卡的 <c>hevc_nvenc</c> 硬件编码；不可用时自动回退 <c>libx265</c>（CPU，约慢 10 倍）。
///
/// ⚠️ 阶段0 实测教训：NVENC 的可用性同时取决于 <b>ffmpeg 版本</b>和<b>显卡驱动版本</b>。
/// 例如 ffmpeg 9.0.2 要求驱动 ≥610.00，而本机驱动是 596.36 → 直接失败。
/// 所以打包的是 ffmpeg 8.1.3。若将来升级 ffmpeg，<b>必须重新跑一次这里探测</b>，
/// 否则会静默退化成 CPU 编码（慢 10 倍但不报错）。
/// </summary>
public sealed class VideoTranscoder : IVideoTranscoder
{
    /// <summary>H.265 恒定质量参数。≈ 视觉无损。</summary>
    private const string QualityValue = "24";

    private volatile bool _hardwareAvailable;
    private string _activeEncoder = CpuEncoder;

    private const string HardwareEncoder = "hevc_nvenc";
    private const string CpuEncoder = "libx265";

    public bool IsHardwareAccelerationAvailable => _hardwareAvailable;

    public string ActiveEncoder => _activeEncoder;

    /// <summary>编码器的人类可读描述，用于界面提示。</summary>
    public string EncoderDescription => _hardwareAvailable
        ? "NVENC 硬件加速（快）"
        : "libx265 CPU 编码（慢，约 10 倍耗时）";

    /// <summary>探测硬件编码可用性。启动时调用一次即可。</summary>
    public async Task ProbeHardwareAsync(CancellationToken ct = default)
    {
        var ffmpeg = FfmpegLocator.FfmpegPath;
        if (ffmpeg is null)
        {
            _hardwareAvailable = false;
            _activeEncoder = CpuEncoder;
            return;
        }

        try
        {
            // 用最小的输入跑一次真实编码，只验证编码器能否打开
            var result = await ProcessRunner.RunAsync(ffmpeg, [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", "color=c=black:s=256x144:r=25:d=1",
                "-frames:v", "3",
                "-c:v", HardwareEncoder,
                "-f", "null", "-",
            ], ct).ConfigureAwait(false);

            _hardwareAvailable = result.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _hardwareAvailable = false;
        }

        _activeEncoder = _hardwareAvailable ? HardwareEncoder : CpuEncoder;
    }

    public async Task<ProcessOutcome> TranscodeAsync(
        ProcessRequest request,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var ffmpeg = FfmpegLocator.FfmpegPath;
        if (ffmpeg is null) return ProcessOutcome.Fail("未找到 ffmpeg");

        if (!File.Exists(request.SourcePath))
            return ProcessOutcome.Fail("源文件不存在");

        CleanupTemp(request.TempPath);

        var parser = new FfmpegProgress(request.Probe?.DurationMs ?? 0, progress);
        var result = await ProcessRunner
            .RunStreamingAsync(ffmpeg, BuildArguments(request), parser.Feed, ct)
            .ConfigureAwait(false);

        if (!result.Success)
            return ProcessOutcome.Fail(Summarize(result));

        if (!File.Exists(request.TempPath) || new FileInfo(request.TempPath).Length == 0)
        {
            CleanupTemp(request.TempPath);
            return ProcessOutcome.Fail("转码进程正常退出，但没有产生输出文件");
        }

        progress?.Report(1.0);
        return ProcessOutcome.Ok;
    }

    /// <summary>构建 ffmpeg 参数。抽成 internal 便于单测校验。</summary>
    internal List<string> BuildArguments(ProcessRequest request)
    {
        var probe = request.Probe;
        var isHdr = (probe?.IsHdr ?? false) && !request.ForceSdr;

        var args = new List<string>
        {
            "-hide_banner", "-y", "-nostdin",
            "-i", request.SourcePath,

            // 首条视频轨 + 全部音轨（没有音轨也不报错）
            "-map", "0:v:0",
            "-map", "0:a?",
        };

        // ── 视频编码 ──────────────────────────────────────────
        if (_hardwareAvailable)
        {
            args.AddRange([
                "-c:v", HardwareEncoder,
                "-preset", "p5",        // p1(最快) ~ p7(最慢)，p5 是画质/速度平衡点
                "-tune", "hq",
                "-rc", "vbr",
                "-cq", QualityValue,    // 恒定质量模式
                "-b:v", "0",
            ]);
        }
        else
        {
            args.AddRange([
                "-c:v", CpuEncoder,
                "-crf", QualityValue,
                "-preset", "medium",
                "-x265-params", "log-level=error",
            ]);
        }

        // ── HDR / 10-bit：保留而非压平（设计文档 §5.4）──────────
        if (isHdr)
        {
            args.AddRange(["-profile:v", "main10", "-pix_fmt", "p010le"]);
        }
        else
        {
            args.AddRange(["-profile:v", "main", "-pix_fmt", "yuv420p"]);
        }

        // ★ 没有这个标记，部分软件（QuickTime / 部分剪辑软件）播不了 HEVC-MP4
        args.AddRange(["-tag:v", "hvc1"]);

        // ── 音频 ──────────────────────────────────────────────
        if (probe is null || probe.CanCopyAudioToMp4)
        {
            // 源音频已经是 MP4 兼容格式 → 直接复制，零损失零耗时
            args.AddRange(["-c:a", "copy"]);
        }
        else
        {
            args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        }

        // moov 前置：拖动进度条响应更快
        args.AddRange(["-movflags", "+faststart"]);

        // 机器可读进度输出到 stdout
        args.AddRange(["-progress", "pipe:1", "-nostats"]);

        args.Add(request.TempPath);
        return args;
    }

    private static string Summarize(ProcessResult result)
    {
        var tail = result.Tail(4);

        // 把最常见的失败原因翻译成人话
        if (tail.Contains("nvenc", StringComparison.OrdinalIgnoreCase) &&
            tail.Contains("driver", StringComparison.OrdinalIgnoreCase))
            return $"显卡驱动不支持硬件编码，已尝试回退：\n{tail}";

        if (tail.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            return "源文件在转码过程中被移动或删除";

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
