using MediaLibrary.Core.Abstractions;
using MediaLibrary.Core.Models;
using MediaLibrary.Media;

namespace MediaLibrary.Core.Tests;

public class FfmpegProgressTests
{
    [Theory]
    [InlineData("00:00:00.000000", 0)]
    [InlineData("00:00:04.920000", 4920)]
    [InlineData("00:01:30.500000", 90500)]
    [InlineData("01:00:00.000000", 3600000)]
    [InlineData("02:30:45", 9045000)]
    public void 解析时间戳(string value, long expected)
    {
        Assert.True(FfmpegProgress.TryParseTimestamp(value, out var ms));
        Assert.Equal(expected, ms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("00:00")]
    [InlineData("xx:yy:zz")]
    public void 非法时间戳返回false(string value)
        => Assert.False(FfmpegProgress.TryParseTimestamp(value, out _));

    [Fact]
    public void 按总时长换算进度()
    {
        var collector = new CollectingProgress();
        var parser = new FfmpegProgress(totalMs: 10_000, collector);

        parser.Feed("frame=25");
        parser.Feed("out_time=00:00:02.500000");
        parser.Feed("out_time=00:00:05.000000");
        parser.Feed("out_time=00:00:09.000000");
        parser.Feed("progress=end");

        Assert.NotEmpty(collector.Values);

        // 最后一个值必须是 1.0（progress=end）
        Assert.Equal(1.0, collector.Values[^1], 3);

        // 中间值单调不减
        for (var i = 1; i < collector.Values.Count; i++)
            Assert.True(collector.Values[i] >= collector.Values[i - 1]);
    }

    [Fact]
    public void 总时长未知时只回报时间不报比例()
    {
        var collector = new CollectingProgress();
        var times = new List<long>();
        var parser = new FfmpegProgress(totalMs: 0, collector, times.Add);

        parser.Feed("out_time=00:00:03.000000");
        parser.Feed("out_time=00:00:06.000000");

        Assert.Equal(new long[] { 3000, 6000 }, times);
        Assert.Empty(collector.Values);   // 没有总时长就不该报比例
        Assert.Equal(6000, parser.CurrentMs);
    }

    [Fact]
    public void 百分比相同时不重复上报()
    {
        // 100ms 粒度在同 1% 内 —— 不应该每次都回调
        var collector = new CollectingProgress();
        var parser = new FfmpegProgress(totalMs: 1_000_000, collector);

        for (var i = 0; i < 50; i++)
            parser.Feed($"out_time=00:00:00.{i:D3}000");

        Assert.True(collector.Values.Count <= 2,
            $"同一百分比内上报了 {collector.Values.Count} 次，应被节流");
    }

    [Fact]
    public void 忽略非进度行()
    {
        var collector = new CollectingProgress();
        var parser = new FfmpegProgress(10_000, collector);

        parser.Feed("frame=1");
        parser.Feed("fps=25.0");
        parser.Feed("bitrate=1234.5kbits/s");
        parser.Feed("not a progress line");
        parser.Feed("");

        Assert.Empty(collector.Values);
    }

    private sealed class CollectingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }
}

public class VideoTranscoderArgumentTests
{
    private static ProcessRequest MakeRequest(MediaProbe? probe, bool forceSdr = false)
        => new(@"C:\src\in.mov", @"C:\lib\.tmp\x.tmp.mp4", probe, forceSdr);

    [Fact]
    public void 包含关键的HEVC标签与快速定位参数()
    {
        var transcoder = new VideoTranscoder();
        var args = transcoder.BuildArguments(MakeRequest(new MediaProbe
        {
            HasVideo = true, HasAudio = true, AudioCodec = "aac", Width = 1920, Height = 1080,
        }));

        var line = string.Join(' ', args);

        // 缺了 hvc1 标记，部分剪辑软件播不了 HEVC-MP4
        Assert.Contains("-tag:v hvc1", line);
        // moov 前置，拖动进度条更快
        Assert.Contains("-movflags +faststart", line);
        // 进度输出到 stdout
        Assert.Contains("-progress pipe:1", line);
        // 只取首条视频轨 + 可选音轨
        Assert.Contains("-map 0:v:0", line);
        Assert.Contains("-map 0:a?", line);
    }

    [Fact]
    public void 音频已兼容时直接复制不重编码()
    {
        var transcoder = new VideoTranscoder();
        var args = transcoder.BuildArguments(MakeRequest(new MediaProbe
        {
            HasVideo = true, HasAudio = true, AudioCodec = "aac",
        }));

        Assert.Contains("-c:a copy", string.Join(' ', args));
    }

    [Fact]
    public void 音频不兼容时转AAC()
    {
        // PCM 装不进 MP4，必须转码
        var transcoder = new VideoTranscoder();
        var args = transcoder.BuildArguments(MakeRequest(new MediaProbe
        {
            HasVideo = true, HasAudio = true, AudioCodec = "pcm_s16le",
        }));

        var line = string.Join(' ', args);
        Assert.Contains("-c:a aac", line);
        Assert.DoesNotContain("-c:a copy", line);
    }

    [Fact]
    public void HDR素材保留10bit不被压平()
    {
        var transcoder = new VideoTranscoder();
        var args = transcoder.BuildArguments(MakeRequest(new MediaProbe
        {
            HasVideo = true, BitDepth = 10, ColorTransfer = "smpte2084", PixFmt = "yuv420p10le",
        }));

        var line = string.Join(' ', args);
        Assert.Contains("-profile:v main10", line);
        Assert.Contains("-pix_fmt p010le", line);
    }

    [Fact]
    public void 强制SDR时压成8bit()
    {
        var transcoder = new VideoTranscoder();
        var args = transcoder.BuildArguments(
            MakeRequest(new MediaProbe { HasVideo = true, BitDepth = 10 }, forceSdr: true));

        var line = string.Join(' ', args);
        Assert.Contains("-pix_fmt yuv420p", line);
        Assert.DoesNotContain("main10", line);
    }

    [Fact]
    public void SDR素材用mainprofile()
    {
        var transcoder = new VideoTranscoder();
        var args = transcoder.BuildArguments(MakeRequest(new MediaProbe
        {
            HasVideo = true, BitDepth = 8, PixFmt = "yuv420p",
        }));

        var line = string.Join(' ', args);
        Assert.Contains("-profile:v main", line);
        Assert.Contains("-pix_fmt yuv420p", line);
    }

    [Fact]
    public void 输出路径排在最后()
    {
        var transcoder = new VideoTranscoder();
        var request = MakeRequest(new MediaProbe { HasVideo = true });
        var args = transcoder.BuildArguments(request);

        Assert.Equal(request.TempPath, args[^1]);
    }
}

public class MediaProbeTests
{
    [Theory]
    [InlineData("aac", true)]
    [InlineData("mp3", true)]
    [InlineData("ac3", true)]
    [InlineData("pcm_s16le", false)]
    [InlineData("flac", false)]
    public void MP4容器能否直接装下音频编码(string codec, bool expected)
        => Assert.Equal(expected, new MediaProbe { AudioCodec = codec }.CanCopyAudioToMp4);

    [Fact]
    public void 十倍位深的源判定为HDR()
        => Assert.True(new MediaProbe { BitDepth = 10 }.IsHdr);

    [Theory]
    [InlineData("smpte2084")]      // PQ
    [InlineData("arib-std-b67")]   // HLG
    public void HDR传递特性判定为HDR(string transfer)
        => Assert.True(new MediaProbe { BitDepth = 10, ColorTransfer = transfer }.IsHdr);

    [Fact]
    public void 普通8bitSDR不算HDR()
        => Assert.False(new MediaProbe { BitDepth = 8, ColorTransfer = "bt709" }.IsHdr);
}
