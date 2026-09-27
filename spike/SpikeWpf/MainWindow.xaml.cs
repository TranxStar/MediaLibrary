using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Gdi = System.Drawing;
using GdiImaging = System.Drawing.Imaging;

namespace SpikeWpf;

public partial class MainWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly List<(string Name, bool Pass, string Detail)> _results = new();
    private LibVLC? _libvlc;
    private MediaPlayer? _mainPlayer;
    private MediaPlayer? _hoverPlayer;
    private HoverWindow? _hoverWindow;
    private string _outDir = "";
    private string _media = "";

    public MainWindow() => InitializeComponent();

    private void Report(string name, bool pass, string detail)
    {
        _results.Add((name, pass, detail));
        SpikeLog.Write($"{(pass ? "PASS" : "FAIL")} | {name} | {detail}");
        Status($"[{(pass ? "PASS" : "FAIL")}] {name}");
    }

    private void Status(string t)
    {
        StatusText.Text = t;
        SpikeLog.Write("STATUS: " + t);
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        var argv = Environment.GetCommandLineArgs();
        _media = argv.Length > 1 ? argv[1] : "";
        _outDir = argv.Length > 2 ? argv[2] : Path.Combine(Path.GetTempPath(), "SpikeWpfOut");
        Directory.CreateDirectory(_outDir);
        SpikeLog.Init(Path.Combine(_outDir, "spike_wpf.log"));

        try { await RunSpikeAsync(); }
        catch (Exception ex)
        {
            Report("整体执行", false, $"{ex.GetType().Name}: {ex.Message}");
            SpikeLog.Write("EXCEPTION: " + ex);
        }
        finally
        {
            WriteSummary();
            await Task.Delay(300);
            Application.Current.Shutdown();
        }
    }

    private async Task RunSpikeAsync()
    {
        // ── 环境信息（DPI 缩放会直接影响截图坐标系） ──────────
        // 注意：不能 using System.Windows.Media，否则它的 MediaPlayer 会和
        // LibVLCSharp.Shared.MediaPlayer 冲突（CS0104）。故此处全限定。
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var winRect = RectOfWindow();
        SpikeLog.Write($"DPI 缩放={dpi.DpiScaleX:F2}x{dpi.DpiScaleY:F2} 主窗口物理矩形={winRect}");
        Report("DPI / 坐标系", true,
            $"缩放 {dpi.DpiScaleX * 100:F0}%  主窗口 {winRect.Width}x{winRect.Height} 物理像素  " +
            $"(窗口 XAML={Width}x{Height} DIP)");

        // ── 0.1 LibVLC + VideoView ────────────────────────────
        Status("初始化 LibVLC");
        Core.Initialize();
        _libvlc = new LibVLC("--no-video-title-show", "--quiet", "--file-caching=300");
        Report("LibVLC 初始化", true, $"libvlc {_libvlc.Version} / .NET {Environment.Version}");

        _mainPlayer = new MediaPlayer(_libvlc);
        MainVideo.MediaPlayer = _mainPlayer;
        Report("WPF VideoView 绑定 MediaPlayer", true,
            "赋值成功 —— LibVLCSharp.WPF 在 net10.0-windows 下可用");

        if (!File.Exists(_media)) { Report("测试媒体", false, $"不存在: {_media}"); return; }

        var hwnd = new WindowInteropHelper(this).Handle;
        SetForegroundWindow(hwnd);
        await Task.Delay(600);

        var probeRect = RectOf(OverlayProbe);
        SpikeLog.Write($"探针物理矩形={probeRect}");

        // ══════════════════════════════════════════════════════
        //  对照组：视频尚未加载时，红色探针必须可见
        //  —— 这一步用来证明"截图 + 坐标系"这套测量方法本身是有效的
        // ══════════════════════════════════════════════════════
        Status("对照组：无视频时探针应可见");
        using (var ctrl = CaptureRegion(probeRect))
        {
            ctrl.Save(Path.Combine(_outDir, "02a_control_no_video.png"), GdiImaging.ImageFormat.Png);
            var (r, g, b) = MeanColor(ctrl);
            var isRed = r > 140 && g < 110 && b < 110;
            Report("对照组 · 无视频时探针可见(红色)", isRed,
                $"探针区域平均 RGB=({r:F0},{g:F0},{b:F0}) " +
                (isRed ? "→ 红色，测量方法有效" : "→ 不是红色！测量方法本身可能有问题"));
        }

        // ── 播放 ──────────────────────────────────────────────
        Status("开始播放");
        using var media = new Media(_libvlc, _media, FromType.FromPath);
        var sw = Stopwatch.StartNew();
        _mainPlayer.Play(media);
        while (_mainPlayer.State != VLCState.Playing && sw.ElapsedMilliseconds < 10000)
            await Task.Delay(40);
        Report("播放启动", _mainPlayer.State == VLCState.Playing,
            $"状态={_mainPlayer.State} 启动延迟={sw.ElapsedMilliseconds}ms 时长={_mainPlayer.Length}ms");

        await Task.Delay(2000);
        var t1 = _mainPlayer.Time;
        await Task.Delay(1000);
        var t2 = _mainPlayer.Time;
        Report("解码推进 (Time 前进)", t2 > t1 + 400, $"Time {t1}ms → {t2}ms");

        await Task.Delay(600);
        using (var full = CaptureRegion(RectOfWindow()))
            full.Save(Path.Combine(_outDir, "01_playing.png"), GdiImaging.ImageFormat.Png);

        // 视频区域采样（避开探针：取左上角内侧）
        var va = RectOf(VideoArea);
        var cornerRect = new Gdi.Rectangle(va.Left + 12, va.Top + 12,
            Math.Max(40, va.Width / 5), Math.Max(40, va.Height / 4));
        using (var corner = CaptureRegion(cornerRect))
        {
            var (mean, std) = Analyze(corner);
            Report("视频渲染到屏幕 (有画面内容)", std > 8,
                $"左上角区域 亮度均值={mean:F1} 标准差={std:F1} (纯色画面应接近 0)");
        }

        // ══════════════════════════════════════════════════════
        //  0.5 空域实测：播放中，探针区域是否被视频盖住？
        //  对照组已证明探针本身是可见的红色；
        //  若现在该区域不再是红色，说明视频确实盖住了 WPF 元素。
        // ══════════════════════════════════════════════════════
        Status("空域实测：视频是否盖住 WPF 元素");
        using (var withVideo = CaptureRegion(probeRect))
        {
            withVideo.Save(Path.Combine(_outDir, "02b_probe_with_video.png"), GdiImaging.ImageFormat.Png);
            var (r, g, b) = MeanColor(withVideo);
            var stillRed = r > 140 && g < 110 && b < 110;
            Report("空域：视频盖住了 WPF 元素？", !stillRed,
                stillRed
                    ? $"探针仍为红色 RGB=({r:F0},{g:F0},{b:F0}) → 视频没有盖住它，无空域问题"
                    : $"探针已不可见，平均 RGB=({r:F0},{g:F0},{b:F0}) → " +
                      "空域问题确认成立：视频区必须直角，UI 不能叠加在视频上");
        }

        // 二次信号：暂停后收起探针，看屏幕像素是否有变化
        _mainPlayer.Pause();
        await Task.Delay(800);
        var capOn = CaptureRegion(probeRect);
        OverlayProbe.Visibility = Visibility.Collapsed;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(500);
        using (var capOff = CaptureRegion(probeRect))
        {
            var d = Diff(capOn, capOff);
            SpikeLog.Write($"收起探针差分: 平均差={d.MeanDiff:F1} 变化像素={d.ChangedPct:F1}%");
            Report("空域 · 二次信号 (收起探针是否影响像素)", d.ChangedPct > 20,
                d.ChangedPct > 20
                    ? $"有影响 {d.ChangedPct:F1}% → 探针可叠加在视频上"
                    : $"无影响 {d.ChangedPct:F1}% → 再次印证视频盖住了 WPF 元素");
        }
        capOn.Dispose();
        OverlayProbe.Visibility = Visibility.Visible;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(300);
        _mainPlayer.Play();

        // ══════════════════════════════════════════════════════
        //  0.2 独立无边框悬浮窗口
        // ══════════════════════════════════════════════════════
        Status("悬浮预览窗口测试");
        _hoverPlayer = new MediaPlayer(_libvlc);
        using var hoverMedia = new Media(_libvlc, _media, FromType.FromPath);
        _hoverWindow = new HoverWindow();
        _hoverWindow.Video.MediaPlayer = _hoverPlayer;
        _hoverWindow.SetLabel(Path.GetFileName(_media));
        Report("无边框窗口 + VideoView 构造", true,
            "WindowStyle=None / ShowActivated=False / AllowsTransparency=False");

        _hoverPlayer.Play(hoverMedia);
        var hsw = Stopwatch.StartNew();
        while (_hoverPlayer.State != VLCState.Playing && hsw.ElapsedMilliseconds < 15000)
            await Task.Delay(40);

        // 先显示一次，拿到有效的 HWND 矩形。
        // 未显示过的窗口 GetWindowRect 返回空矩形（(0,0,0,0)），
        // 会导致后续所有截图都退化成 1x1 像素、差分恒为 0。
        _hoverWindow.Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(500);
        var hoverRect = HoverRectPhysical();
        _hoverWindow.Hide();
        await Task.Delay(600);
        SpikeLog.Write($"悬浮窗物理矩形={hoverRect}");

        if (hoverRect.Width <= 8 || hoverRect.Height <= 8)
        {
            Report("悬浮窗口矩形获取", false,
                $"GetWindowRect 返回无效矩形 {hoverRect} —— 后续截图测试无意义");
        }

        var baseline = CaptureRegion(hoverRect);          // 显示前基线
        baseline.Save(Path.Combine(_outDir, "03a_hover_baseline.png"), GdiImaging.ImageFormat.Png);

        var showSw = Stopwatch.StartNew();
        _hoverWindow.Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        showSw.Stop();
        await Task.Delay(1800);

        var shown = CaptureRegion(hoverRect);
        shown.Save(Path.Combine(_outDir, "03b_hover_shown.png"), GdiImaging.ImageFormat.Png);

        var dShow = Diff(baseline, shown);
        Report("悬浮窗口显示 (相对基线有变化)", dShow.ChangedPct > 15,
            $"Show()耗时={showSw.ElapsedMilliseconds}ms 与基线差分={dShow.ChangedPct:F1}% " +
            $"IsVisible={_hoverWindow.IsVisible}");

        var (hm, hs) = Analyze(shown);
        Report("悬浮窗口内视频渲染", hs > 8, $"亮度均值={hm:F1} 标准差={hs:F1}");

        var hideSw = Stopwatch.StartNew();
        _hoverWindow.Hide();
        hideSw.Stop();
        await Task.Delay(600);

        using (var after = CaptureRegion(hoverRect))
        {
            after.Save(Path.Combine(_outDir, "03c_hover_hidden.png"), GdiImaging.ImageFormat.Png);
            var dBack = Diff(baseline, after);
            Report("悬浮窗口 Hide 完全还原", dBack.ChangedPct < 8,
                $"Hide()耗时={hideSw.ElapsedMilliseconds}ms 与基线差分={dBack.ChangedPct:F1}% " +
                "(应接近 0，说明窗口真正消失且无残留)");
        }
        baseline.Dispose();
        shown.Dispose();

        Status("验证完成");
        _hoverPlayer.Stop();
        _mainPlayer.Stop();
    }

    // ══════════════════════════ 工具方法 ══════════════════════════

    /// <summary>
    /// 元素在屏幕上的物理像素矩形。
    /// 注意：PointToScreen 返回物理像素，而 ActualWidth/Height 是 DIP，
    /// 非 100% 缩放下直接混用会把尺寸算错 —— 必须用两个角点分别转换。
    /// </summary>
    private static Gdi.Rectangle RectOf(FrameworkElement el)
    {
        var tl = el.PointToScreen(new System.Windows.Point(0, 0));
        var br = el.PointToScreen(new System.Windows.Point(el.ActualWidth, el.ActualHeight));
        return Gdi.Rectangle.FromLTRB((int)Math.Round(tl.X), (int)Math.Round(tl.Y),
                                      (int)Math.Round(br.X), (int)Math.Round(br.Y));
    }

    private Gdi.Rectangle RectOfWindow() => RectOfWindowHwnd(new WindowInteropHelper(this).Handle);

    /// <summary>按 HoverWindow 的 XAML Left/Top/Width/Height（DIP）换算物理矩形。</summary>
    private Gdi.Rectangle HoverRectPhysical()
    {
        if (_hoverWindow is null) return Gdi.Rectangle.Empty;
        return RectOfWindowHwnd(new WindowInteropHelper(_hoverWindow).Handle);
    }

    private static Gdi.Rectangle RectOfWindowHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return Gdi.Rectangle.Empty;
        return Gdi.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static Gdi.Bitmap CaptureRegion(Gdi.Rectangle r)
    {
        if (r.Width <= 0 || r.Height <= 0) r = new Gdi.Rectangle(Math.Max(0, r.Left), Math.Max(0, r.Top), 1, 1);
        var bmp = new Gdi.Bitmap(r.Width, r.Height, GdiImaging.PixelFormat.Format32bppArgb);
        using var g = Gdi.Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Gdi.Size(r.Width, r.Height), Gdi.CopyPixelOperation.SourceCopy);
        return bmp;
    }

    private static (double R, double G, double B) MeanColor(Gdi.Bitmap bmp)
    {
        double r = 0, g = 0, b = 0; long n = 0;
        for (int y = 0; y < bmp.Height; y += 2)
            for (int x = 0; x < bmp.Width; x += 2)
            {
                var c = bmp.GetPixel(x, y);
                r += c.R; g += c.G; b += c.B; n++;
            }
        return n == 0 ? (0, 0, 0) : (r / n, g / n, b / n);
    }

    private static (double Mean, double Std) Analyze(Gdi.Bitmap bmp)
    {
        double sum = 0, sumSq = 0; long n = 0;
        for (int y = 0; y < bmp.Height; y += 2)
            for (int x = 0; x < bmp.Width; x += 2)
            {
                var c = bmp.GetPixel(x, y);
                double luma = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                sum += luma; sumSq += luma * luma; n++;
            }
        if (n == 0) return (0, 0);
        var mean = sum / n;
        return (mean, Math.Sqrt(Math.Max(0, sumSq / n - mean * mean)));
    }

    private static (double MeanDiff, int MaxDiff, double ChangedPct) Diff(Gdi.Bitmap a, Gdi.Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return (255, 255, 100);
        double sum = 0; int max = 0; long changed = 0, n = 0;
        for (int y = 0; y < a.Height; y += 2)
            for (int x = 0; x < a.Width; x += 2)
            {
                var ca = a.GetPixel(x, y);
                var cb = b.GetPixel(x, y);
                int d = Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B);
                sum += d; n++;
                if (d > max) max = d;
                if (d > 30) changed++;
            }
        return (n == 0 ? 0 : sum / n, max, n == 0 ? 0 : 100.0 * changed / n);
    }

    private void WriteSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SpikeWpf 阶段0验证结果 ===");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"媒体: {_media}");
        sb.AppendLine();
        foreach (var (n, p, d) in _results)
        {
            sb.AppendLine($"[{(p ? "PASS" : "FAIL")}] {n}");
            sb.AppendLine($"        {d}");
        }
        sb.AppendLine();
        sb.AppendLine($"通过 {_results.Count(r => r.Pass)} / {_results.Count}");
        var text = sb.ToString();
        File.WriteAllText(Path.Combine(_outDir, "spike_wpf_result.txt"), text, Encoding.UTF8);
        SpikeLog.Write("=== 汇总 ===");
        SpikeLog.Write(text);
    }
}
