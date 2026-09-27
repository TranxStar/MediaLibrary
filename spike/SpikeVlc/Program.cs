using System.Diagnostics;
using System.Text;
using ImageMagick;
using LibVLCSharp.Shared;

Console.OutputEncoding = Encoding.UTF8;

var results = new List<(string Name, bool Pass, string Detail)>();
void Report(string name, bool pass, string detail)
{
    results.Add((name, pass, detail));
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name}");
    Console.WriteLine($"         {detail}");
}
void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"  {title}");
    Console.WriteLine(new string('=', 78));
}

var mediaPath = args.FirstOrDefault(a => !a.StartsWith("--"));
var workDir = Path.Combine(Path.GetTempPath(), "SpikeVlc");
Directory.CreateDirectory(workDir);

// ══════════════════════════════════════════════════════════════════
//  验证 0.1  LibVLCSharp 3.10.1 在 net10.0-windows 下的可用性
// ══════════════════════════════════════════════════════════════════
Section("验证 0.1  LibVLCSharp 在 .NET 10 上的可用性");

LibVLC? libvlc = null;
try
{
    Report("程序集加载", true,
        $"LibVLCSharp {typeof(LibVLC).Assembly.GetName().Version} / " +
        $"运行时 {Environment.Version} / RID {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");

    var baseDir = AppContext.BaseDirectory;
    var libvlcDir = Path.Combine(baseDir, "libvlc");
    var candidates = Directory.Exists(libvlcDir)
        ? Directory.GetDirectories(libvlcDir)
        : Array.Empty<string>();
    Report("原生库落地", candidates.Length > 0,
        candidates.Length > 0
            ? $"找到: {string.Join(", ", candidates.Select(Path.GetFileName))}"
            : $"未在 {libvlcDir} 找到子目录");

    Core.Initialize();
    libvlc = new LibVLC("--no-video-title-show", "--quiet", "--vout=dummy");
    Report("Core.Initialize + LibVLC 实例化", true, $"libvlc 版本 = {libvlc.Version}");
}
catch (Exception ex)
{
    Report("Core.Initialize + LibVLC 实例化", false, $"{ex.GetType().Name}: {ex.Message}");
}

// ══════════════════════════════════════════════════════════════════
//  验证 0.1b  真实媒体解码 + 播放 + 拖动进度
// ══════════════════════════════════════════════════════════════════
Section("验证 0.1b  真实媒体解码 / 播放 / 进度拖动");

if (libvlc is not null && mediaPath is not null && File.Exists(mediaPath))
{
    try
    {
        using var media = new Media(libvlc, mediaPath, FromType.FromPath);
        var parsed = await media.Parse(MediaParseOptions.ParseLocal, 8000);
        var tracks = media.Tracks;
        var vTrack = tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);

        Report("媒体解析 (demux)", parsed == MediaParsedStatus.Done,
            $"状态={parsed} 时长={media.Duration}ms 轨道数={tracks.Length} " +
            $"视频={vTrack.Data.Video.Width}x{vTrack.Data.Video.Height}");

        using var mp = new MediaPlayer(libvlc);
        long lastTime = -1;
        var timeEvents = 0;
        mp.TimeChanged += (_, e) => { if (e.Time != lastTime) { lastTime = e.Time; timeEvents++; } };

        var sw = Stopwatch.StartNew();
        var started = mp.Play(media);
        while (mp.State != VLCState.Playing && sw.ElapsedMilliseconds < 5000)
            await Task.Delay(50);
        var startLatency = sw.ElapsedMilliseconds;

        await Task.Delay(1500);
        var advanced = mp.Time;

        Report("播放启动", mp.State is VLCState.Playing or VLCState.Buffering,
            $"Play()返回={started} 状态={mp.State} 启动延迟={startLatency}ms");
        Report("解码推进 (Time 前进)", advanced > 200,
            $"1.5 秒后 Time={advanced}ms，TimeChanged 事件 {timeEvents} 次");

        mp.Position = 0.5f;
        await Task.Delay(900);
        var afterSeek = mp.Position;
        Report("进度拖动 (Seek)", Math.Abs(afterSeek - 0.5f) < 0.15f,
            $"设置 0.50 → 实际 {afterSeek:F3} (Time={mp.Time}ms)");

        mp.Stop();
    }
    catch (Exception ex)
    {
        Report("真实媒体播放", false, $"{ex.GetType().Name}: {ex.Message}");
    }
}
else
{
    Report("真实媒体播放", false,
        mediaPath is null ? "未提供媒体文件参数" : $"文件不存在: {mediaPath}");
}

libvlc?.Dispose();

// ══════════════════════════════════════════════════════════════════
//  验证 0.4  Magick.NET 的 HEIC 能力（ffmpeg 做不到的部分）
// ══════════════════════════════════════════════════════════════════
Section("验证 0.4  Magick.NET：格式支持 + HEIC + EXIF + 自动转向");

try
{
    Report("Magick.NET 加载", true,
        $"ImageMagick {MagickNET.Version} / 支持格式数 {MagickNET.SupportedFormats.Count()}");

    var heicFormats = MagickNET.SupportedFormats
        .Where(f => f.Format.ToString().Contains("HEI", StringComparison.OrdinalIgnoreCase)
                 || f.Format.ToString().Contains("AVIF", StringComparison.OrdinalIgnoreCase))
        .Select(f => $"{f.Format}({(f.SupportsReading ? "R" : "-")}{(f.SupportsWriting ? "W" : "-")})")
        .ToList();
    Report("HEIC/HEIF 格式支持", heicFormats.Count > 0,
        heicFormats.Count > 0 ? string.Join(", ", heicFormats) : "未报告任何 HEIC/HEIF 格式");
}
catch (Exception ex)
{
    Report("Magick.NET 加载", false, $"{ex.GetType().Name}: {ex.Message}");
}

try
{
    var jpgPath = Path.Combine(workDir, "spike_source.jpg");
    var pngPath = Path.Combine(workDir, "spike_out.png");
    var heicPath = Path.Combine(workDir, "spike_out.heic");

    using (var img = new MagickImage(MagickColors.CornflowerBlue, 4000, 3000))
    {
        img.Settings.SetDefine(MagickFormat.Jpeg, "quality", "92");
        var profile = new ExifProfile();
        profile.SetValue(ExifTag.DateTimeOriginal, "2026:06:12 09:30:00");
        profile.SetValue(ExifTag.Model, "SpikeCamera");
        profile.SetValue(ExifTag.Orientation, (ushort)6);
        img.SetProfile(profile);
        img.Write(jpgPath);
    }

    var jpgSize = new FileInfo(jpgPath).Length;

    using (var img = new MagickImage(jpgPath))
    {
        var exif = img.GetExifProfile();
        var readOrientation = exif?.GetValue(ExifTag.Orientation)?.Value;
        var beforeW = img.Width; var beforeH = img.Height;

        img.AutoOrient();
        var afterW = img.Width; var afterH = img.Height;

        img.Write(pngPath, MagickFormat.Png);
        var pngSize = new FileInfo(pngPath).Length;

        Report("EXIF 读取", readOrientation is not null,
            $"Orientation={readOrientation} Model={exif?.GetValue(ExifTag.Model)?.Value} " +
            $"DateTimeOriginal={exif?.GetValue(ExifTag.DateTimeOriginal)?.Value}");

        Report("AutoOrient 竖拍转向", beforeW != afterW || beforeH != afterH,
            $"旋转前 {beforeW}x{beforeH} → 旋转后 {afterW}x{afterH}");

        Report("PNG 编码", pngSize > 0,
            $"JPEG {jpgSize / 1024.0 / 1024.0:F2} MB → PNG {pngSize / 1024.0 / 1024.0:F2} MB " +
            $"(膨胀 {(double)pngSize / jpgSize:F2} 倍)");
    }

    var canWriteHeic = MagickNET.SupportedFormats
        .Any(f => f.Format.ToString().Contains("HEI", StringComparison.OrdinalIgnoreCase) && f.SupportsWriting);
    if (canWriteHeic)
    {
        using (var img = new MagickImage(jpgPath)) { img.Write(heicPath, MagickFormat.Heic); }
        using (var img = new MagickImage(heicPath))
        {
            Report("HEIC 读+写往返", img.Width == 4000,
                $"写出 {new FileInfo(heicPath).Length / 1024.0:F0} KB，再读回 {img.Width}x{img.Height} OK");
        }
    }
    else
    {
        Report("HEIC 写能力", false, "Magick.NET 不支持写 HEIC（读能力需真实 HEIC 样张验证）");
    }
}
catch (Exception ex)
{
    Report("Magick.NET 图片管线", false, $"{ex.GetType().Name}: {ex.Message}");
}

Section("阶段 0 验证汇总（VLC + Magick 部分）");
var passed = results.Count(r => r.Pass);
Console.WriteLine($"  通过 {passed} / {results.Count}");
foreach (var (n, _, d) in results.Where(r => !r.Pass))
    Console.WriteLine($"    x {n} - {d}");
Console.WriteLine();

return results.Count(r => !r.Pass);
