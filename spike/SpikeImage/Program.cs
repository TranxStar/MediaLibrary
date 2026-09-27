using System.Diagnostics;
using System.Text;
using ImageMagick;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

Console.OutputEncoding = Encoding.UTF8;

var samplesDir = @"C:\Users\StarsFunction\MediaLibrary\spike\testmedia";
var work = Path.Combine(Path.GetTempPath(), "SpikeImage");
System.IO.Directory.CreateDirectory(work);

Console.WriteLine(new string('=', 76));
Console.WriteLine("  照片管线方案对比：Magick.NET  vs  ffmpeg");
Console.WriteLine(new string('=', 76));
Console.WriteLine();
Console.WriteLine($"ImageMagick 版本: {MagickNET.Version}");
Console.WriteLine($"支持格式总数    : {MagickNET.SupportedFormats.Count()}");
Console.WriteLine();

// ══════════════════════════════════════════════════════════════
// 1. HEIC 解码：Magick.NET 能否读真实 HEIC
// ══════════════════════════════════════════════════════════════
Console.WriteLine("--- 1. Magick.NET 读取真实 HEIC 样张 ---");

foreach (var heic in System.IO.Directory.GetFiles(samplesDir, "*.heic"))
{
    var name = Path.GetFileName(heic);
    var srcSize = new FileInfo(heic).Length;

    try
    {
        using (var collection = new MagickImageCollection(heic))
        {
            Console.WriteLine($"  {name}  源 {srcSize / 1024.0:F0} KB  帧数={collection.Count}");
            foreach (var f in collection)
                Console.WriteLine($"      帧 {f.Width}x{f.Height} 格式={f.Format}");
        }

        var sw = Stopwatch.StartNew();
        using var img = new MagickImage(heic);
        sw.Stop();
        var decodeMs = sw.ElapsedMilliseconds;

        var exif = img.GetExifProfile();
        var orient = exif?.GetValue(ExifTag.Orientation)?.Value;
        var taken = exif?.GetValue(ExifTag.DateTimeOriginal)?.Value;
        var model = exif?.GetValue(ExifTag.Model)?.Value;

        Console.WriteLine($"      Magick 解码: {img.Width}x{img.Height} 耗时={decodeMs}ms");
        Console.WriteLine($"      EXIF: Orientation={orient?.ToString() ?? "(无)"} " +
                          $"DateTimeOriginal={taken ?? "(无)"} Model={model ?? "(无)"}");

        sw.Restart();
        img.AutoOrient();
        sw.Stop();
        Console.WriteLine($"      AutoOrient 后: {img.Width}x{img.Height} 耗时={sw.ElapsedMilliseconds}ms");

        var png = Path.Combine(work, Path.GetFileNameWithoutExtension(heic) + "_magick.png");
        sw.Restart();
        img.Write(png, MagickFormat.Png);
        sw.Stop();
        Console.WriteLine($"      -> PNG {new FileInfo(png).Length / 1024.0:F0} KB  编码耗时={sw.ElapsedMilliseconds}ms");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"      !! 失败: {ex.GetType().Name}: {ex.Message}");
    }

    var ffPng = Path.Combine(samplesDir, "from_ffmpeg.png");
    if (File.Exists(ffPng))
        Console.WriteLine($"      (ffmpeg 对照产物 {new FileInfo(ffPng).Length / 1024.0:F0} KB)");
    Console.WriteLine();
}

// ══════════════════════════════════════════════════════════════
// 2. MetadataExtractor 读 EXIF
// ══════════════════════════════════════════════════════════════
Console.WriteLine("--- 2. MetadataExtractor 读 EXIF ---");
foreach (var heic in System.IO.Directory.GetFiles(samplesDir, "*.heic"))
{
    try
    {
        var dirs = ImageMetadataReader.ReadMetadata(heic).ToList();
        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        Console.WriteLine($"  {Path.GetFileName(heic)}: 目录数={dirs.Count} " +
                          $"Orientation={ifd0?.GetDescription(ExifDirectoryBase.TagOrientation) ?? "(无)"} " +
                          $"拍摄时间={sub?.GetDescription(ExifDirectoryBase.TagDateTimeOriginal) ?? "(无)"} " +
                          $"机型={ifd0?.GetDescription(ExifDirectoryBase.TagModel) ?? "(无)"}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {Path.GetFileName(heic)}: 失败 {ex.GetType().Name}: {ex.Message}");
    }
}
Console.WriteLine();

// ══════════════════════════════════════════════════════════════
// 3. 竖拍 EXIF 方向 + AutoOrient 正确性
// ══════════════════════════════════════════════════════════════
Console.WriteLine("--- 3. 竖拍转向：合成带 EXIF Orientation 的 JPEG ---");

var jpg = Path.Combine(work, "orient_test.jpg");
try
{
    using (var img = new MagickImage(MagickColors.Orange, 1600, 900))
    {
        img.Orientation = OrientationType.RightTop;   // 6 = 顺时针 90 度
        img.Write(jpg, MagickFormat.Jpeg);
    }

    using (var probe = new MagickImage(jpg))
    {
        var o = probe.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value;
        Console.WriteLine($"  写出的 JPEG: {probe.Width}x{probe.Height} " +
                          $"EXIF Orientation={o?.ToString() ?? "(无)"} (期望 RightTop/6)");
    }

    var dirs = ImageMetadataReader.ReadMetadata(jpg).ToList();
    var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
    Console.WriteLine($"  MetadataExtractor 读到: Orientation=" +
                      $"{ifd0?.GetDescription(ExifDirectoryBase.TagOrientation) ?? "(无)"}");

    using (var img = new MagickImage(jpg))
    {
        var before = $"{img.Width}x{img.Height}";
        img.AutoOrient();
        var after = $"{img.Width}x{img.Height}";
        var rotated = img.Width != 1600;
        Console.WriteLine($"  AutoOrient: {before} -> {after}  " +
                          (rotated ? "PASS 竖拍已转正" : "FAIL 未转向"));
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  !! 失败: {ex.GetType().Name}: {ex.Message}");
}
Console.WriteLine();

// ══════════════════════════════════════════════════════════════
// 4. PNG 体积与耗时
// ══════════════════════════════════════════════════════════════
Console.WriteLine("--- 4. 4000x3000 照片 -> PNG 体积/耗时（Magick.NET）---");
var bigJpg = Path.Combine(work, "big_photo.jpg");
try
{
    using (var img = new MagickImage(MagickColors.CadetBlue, 4000, 3000))
    {
        img.AddNoise(NoiseType.Gaussian, 8);
        img.Settings.SetDefine(MagickFormat.Jpeg, "quality", "92");
        img.Write(bigJpg, MagickFormat.Jpeg);
    }
    var jpgSize = new FileInfo(bigJpg).Length;

    foreach (var level in new[] { 3, 6, 9 })
    {
        using var img = new MagickImage(bigJpg);
        img.Settings.SetDefine(MagickFormat.Png, "compression-level", level.ToString());
        var outPath = Path.Combine(work, $"png_l{level}.png");
        var sw = Stopwatch.StartNew();
        img.Write(outPath, MagickFormat.Png);
        sw.Stop();
        var pngSize = new FileInfo(outPath).Length;
        Console.WriteLine($"  compression-level={level}: PNG {pngSize / 1024.0 / 1024.0:F2} MB " +
                          $"(JPEG {jpgSize / 1024.0 / 1024.0:F2} MB, 膨胀 {(double)pngSize / jpgSize:F2}x) " +
                          $"耗时={sw.ElapsedMilliseconds}ms");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  !! 失败: {ex.GetType().Name}: {ex.Message}");
}

// ══════════════════════════════════════════════════════════════
// 5. 手工注入 EXIF APP1 段，得到确定带 Orientation=6 的 JPEG，
//    再验证 AutoOrient 是否真的把竖拍转正。
//    （Magick.NET 用 API 写 Orientation 没生效，故走字节级注入）
// ══════════════════════════════════════════════════════════════
Console.WriteLine("--- 5. 字节级注入 EXIF Orientation=6 -> 验证 AutoOrient ---");

static void InjectExifOrientation(string jpgPath, string outPath, ushort orientation)
{
    var jpeg = File.ReadAllBytes(jpgPath);

    var app1 = new List<byte>();
    app1.AddRange(new byte[] { 0x45, 0x78, 0x69, 0x66, 0x00, 0x00 });              // "Exif\0\0"
    app1.AddRange(new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00 });  // TIFF 头 (小端)
    app1.AddRange(new byte[] { 0x01, 0x00 });                                      // IFD0 条目数 = 1
    // 标签 0x0112 (Orientation)，类型 3 (SHORT)，数量 1，值
    app1.AddRange(new byte[] { 0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,
                               (byte)(orientation & 0xFF), (byte)(orientation >> 8), 0x00, 0x00 });
    app1.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });                          // 下一个 IFD = 0

    var segLen = app1.Count + 2;
    var seg = new List<byte> { 0xFF, 0xE1, (byte)(segLen >> 8), (byte)(segLen & 0xFF) };
    seg.AddRange(app1);

    var result = new List<byte>();
    result.AddRange(jpeg.Take(2));    // SOI
    result.AddRange(seg);             // APP1 插在 SOI 之后
    result.AddRange(jpeg.Skip(2));
    File.WriteAllBytes(outPath, result.ToArray());
}

try
{
    var plain = Path.Combine(work, "plain_1600x900.jpg");
    using (var img = new MagickImage(MagickColors.Orange, 1600, 900))
        img.Write(plain, MagickFormat.Jpeg);

    var oriented = Path.Combine(work, "oriented_6.jpg");
    InjectExifOrientation(plain, oriented, 6);
    Console.WriteLine($"  注入完成: {new FileInfo(oriented).Length / 1024.0:F0} KB");

    // A. MetadataExtractor 交叉验证 EXIF 是否真的可读
    var dirs = ImageMetadataReader.ReadMetadata(oriented).ToList();
    var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
    Console.WriteLine($"  MetadataExtractor: Orientation=" +
                      $"{ifd0?.GetDescription(ExifDirectoryBase.TagOrientation) ?? "(读不到)"}");

    // B. Magick.NET 读取 + AutoOrient
    using (var img = new MagickImage(oriented))
    {
        var o = img.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value;
        var before = $"{img.Width}x{img.Height}";
        Console.WriteLine($"  Magick.NET 读到 Orientation={o?.ToString() ?? "(读不到)"}  原始 {before}");

        img.AutoOrient();
        var after = $"{img.Width}x{img.Height}";
        var ok = img.Width == 900 && img.Height == 1600;
        Console.WriteLine($"  AutoOrient 结果: {before} -> {after}  " +
                          (ok ? "PASS 竖拍已正确转正" : "FAIL 未按 EXIF 转向"));
    }
}
catch (Exception ex)
{
    Console.WriteLine($"  !! 失败: {ex.GetType().Name}: {ex.Message}");
}
Console.WriteLine();

Console.WriteLine();
Console.WriteLine(new string('=', 76));
Console.WriteLine("  对比测试结束");
Console.WriteLine(new string('=', 76));
return 0;
