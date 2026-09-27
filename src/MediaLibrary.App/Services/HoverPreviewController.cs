using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MediaLibrary.App.ViewModels;
using MediaLibrary.App.Views;
using MediaLibrary.Core;
using MediaLibrary.Core.Models;
using MediaLibrary.Media;

namespace MediaLibrary.App.Services;

/// <summary>
/// 悬浮预览的调度中枢。
///
/// ★ 架构变更（按用户反馈）：预览从"独立系统窗口"改为<b>主窗口内的浮层</b>。
///   视频帧由 <see cref="VideoFrameRenderer"/> 写进 WriteableBitmap ——
///   不再是原生子窗口，空域问题、焦点问题、吞鼠标事件问题全部消失。
///
/// 时序：
/// <code>
/// 鼠标进入卡片 → 启动 280ms 防抖 → 到期定位面板 + 播放
///   ├─ 期间移出 / 移到别的卡片 → 取消
///   └─ 视频静音 / 音频音量 80 / 照片只显示放大图
/// 鼠标移出卡片 → 400ms 宽限期（允许移进控制条拖进度条）
///   ├─ 期间移回 → 取消隐藏
///   └─ 超时 → 隐藏 + 停止播放
/// </code>
///
/// 画面区<b>拦截鼠标</b>（不穿透）：面板像真正的悬浮窗一样挡住下面的卡片。
/// 鼠标移到面板上时，预览不会切到被盖住的文件，控制条随时可点；
/// 要预览别的卡片，把鼠标移到【没被面板盖住】的部分即可。
/// </summary>
public sealed class HoverPreviewController : IDisposable
{
    /// <summary>移出卡片后的宽限期，让鼠标能顺利移进控制条。</summary>
    public const int GraceMs = 400;

    private const double PanelWidthDip = 420;
    private const double PanelHeightDip = 280;

    /// <summary>空格键固定预览时用更大的尺寸。</summary>
    private const double PinnedWidthDip = 800;
    private const double PinnedHeightDip = 520;

    /// <summary>面板与卡片之间的间距（DIP）。</summary>
    private const double GapDip = 8;

    private readonly VlcPlayerPool _pool;
    private readonly ThumbnailImageCache _thumbCache;
    private readonly LibraryLayout _layout;
    private readonly DispatcherTimer _debounceTimer;
    private readonly DispatcherTimer _graceTimer;
    private readonly DispatcherTimer _keepAliveTimer;

    private Canvas? _overlay;
    private HoverPreviewPanel? _panel;
    private AssetItemViewModel? _pendingItem;
    private Rect _pendingRect;

    private bool _disposed;

    public HoverPreviewController(
        VlcPlayerPool pool,
        ThumbnailImageCache thumbCache,
        LibraryLayout layout,
        AppSettings settings)
    {
        _pool = pool;
        _thumbCache = thumbCache;
        _layout = layout;

        // 悬浮预览总开关与防抖时间来自设置（设置窗口可调）
        ApplyRuntimeSettings(settings);

        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background);
        _debounceTimer.Tick += OnDebounceElapsed;

        _graceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(GraceMs),
        };
        _graceTimer.Tick += OnGraceElapsed;

        _keepAliveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _keepAliveTimer.Tick += OnKeepAliveTick;
    }

    /// <summary>把设置应用到运行时（启动时 + 设置窗口关闭后）。</summary>
    public void ApplyRuntimeSettings(AppSettings settings)
    {
        IsEnabled = settings.HoverPreviewEnabled;

        var debounce = Math.Clamp(settings.HoverPreviewDelayMs, 100, 1200);
        if (_debounceTimer is not null)
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(debounce);
    }

    /// <summary>总开关。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 诊断模式开关：关闭保活轮询（诊断要确定性，不能依赖真实光标位置，
    /// 更不能为此去移动用户的鼠标 —— 用户正按着鼠标时会被拽走）。
    /// </summary>
    public bool KeepAliveDisabled { get; set; }

    /// <summary>是否已被空格键固定。固定后不自动隐藏、不自动切换。</summary>
    public bool IsPinned { get; private set; }

    /// <summary>面板是否可见。</summary>
    public bool IsVisible => _panel?.Visibility == Visibility.Visible;

    /// <summary>当前预览的素材 Id。</summary>
    public long? ShownAssetId => _panel?.CurrentAssetId;

    /// <summary>面板在主窗口内的位置（DIP，相对覆盖层）。供诊断使用。</summary>
    public (double X, double Y, double Width, double Height)? PanelBounds
    {
        get
        {
            if (_panel?.Visibility != Visibility.Visible) return null;
            return (Canvas.GetLeft(_panel), Canvas.GetTop(_panel),
                    _panel.ActualWidth, _panel.ActualHeight);
        }
    }

    /// <summary>面板是否真正挂载在主窗口里（诊断"不再是独立窗口"用）。</summary>
    public bool PanelIsInMainWindow => _panel?.Parent is not null;

    /// <summary>播放状态快照。供诊断使用。</summary>
    public (long PositionMs, long DurationMs, string State) GetPlaybackState()
        => _panel is null
            ? (-1, 0, "(无面板)")
            : (_panel.PositionMs, _panel.DurationMs, _panel.PlayerStateText);

    /// <summary>诊断用：模拟一次进度条刮擦（提交到播放器）。</summary>
    public void PerformDiagnosticSeek(double fraction) => _panel?.DiagnosticSeek(fraction);

    // ══════════════════════════ 初始化 ══════════════════════════

    /// <summary>
    /// 把预览面板挂到主窗口的覆盖层上。
    /// 面板只创建一次，之后反复 Show/Hide（Visibility 切换，零开销）。
    /// </summary>
    public void SetOverlay(Canvas overlay)
    {
        if (_disposed || overlay is null) return;

        _overlay = overlay;

        _panel = new HoverPreviewPanel(_pool, _thumbCache);
        _panel.Visibility = Visibility.Collapsed;
        _panel.Width = PanelWidthDip;
        _panel.Height = PanelHeightDip;

        // 移进控制条 → 取消隐藏（拖进度条用）
        _panel.PointerEntered += (_, _) => KeepAlive();
        _panel.PointerLeft += (_, _) => RequestHide();

        overlay.Children.Add(_panel);
        Panel.SetZIndex(_panel, 1000);

        DiagnosticsLog.Mark("[悬浮预览] 面板已挂载（主窗口内浮层，非独立窗口）");
    }

    /// <summary>
    /// 预热。面板是窗口内控件，没有旧版"首次 Show 要 1.7 秒"的开销；
    /// 这里只做一次布局，确认渲染管线就绪。LibVLC 预热由 App 负责。
    /// </summary>
    public void Preload()
    {
        if (_overlay is null || _panel is null) return;

        _panel.Measure(new Size(PanelWidthDip, PanelHeightDip));
        _panel.Arrange(new Rect(0, 0, PanelWidthDip, PanelHeightDip));
        DiagnosticsLog.Mark("[悬浮预览] 面板预热完成");
    }

    // ══════════════════════════ 外部调用 ══════════════════════════

    /// <summary>鼠标进入某张卡片。程序化触发可传 <c>respectCursor: false</c>。</summary>
    public void RequestShow(AssetItemViewModel item, FrameworkElement tile, bool respectCursor = true)
    {
        if (_disposed || !IsEnabled || _overlay is null) return;

        // ★ 面板可见且光标在面板范围内 → 忽略卡片传来的悬停事件。
        //   画面区已改为拦截鼠标，正常路径下被盖住的卡片根本收不到 MouseEnter；
        //   但圆角外侧有极小缝隙会穿透，这里再加一道保险：
        //   只要光标在面板上，预览就绝不切到"鼠标下面的文件"。
        //   （用户实测：穿透导致鼠标一移到面板上，预览就在两个文件之间来回切换）
        if (respectCursor &&
            _panel?.Visibility == Visibility.Visible && IsCursorInsidePanel())
        {
            DiagnosticsLog.Mark($"[悬浮预览] 忽略悬停（光标在面板上）：{item.FileName}");
            return;
        }

        // ★ 固定状态下悬停【别的】卡片 → 视为"我想看这张"，自动取消固定。
        //   否则按过一次空格后，预览会钉死在中间不动，而用户看不出原因。
        if (IsPinned)
        {
            if (_panel?.CurrentAssetId == item.Id) return;

            IsPinned = false;
            DiagnosticsLog.Mark("[悬浮预览] 悬停到其他卡片 → 自动取消固定");
        }

        // 已经在预览同一张卡片 → 取消任何待执行的隐藏
        if (_panel?.CurrentAssetId == item.Id && _panel.Visibility == Visibility.Visible)
        {
            _graceTimer.Stop();
            return;
        }

        var rect = GetTileRectIn(tile, _overlay);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            DiagnosticsLog.Mark($"[悬浮预览] 显示被跳过：卡片矩形无效 {rect.Width:F0}x{rect.Height:F0}（{item.FileName}）");
            return;
        }

        _pendingItem = item;
        _pendingRect = rect;

        _graceTimer.Stop();
        _debounceTimer.Stop();

        // ★ 面板已经可见 → 立即切换（不等防抖）。
        //   否则快速划过一排卡片时，每次 RequestHide 都会把防抖清零，
        //   预览永远停在第一张。防抖只用于【从无到有】的首次弹出。
        if (_panel?.Visibility == Visibility.Visible)
        {
            ShowPendingNow();
            return;
        }

        _debounceTimer.Start();
    }

    /// <summary>鼠标离开卡片。</summary>
    public void RequestHide()
    {
        if (_disposed || IsPinned) return;

        // 还没弹出来就移走了 —— 什么都不做（这正是防抖的意义）
        _debounceTimer.Stop();
        _pendingItem = null;

        if (_panel?.Visibility != Visibility.Visible) return;

        _graceTimer.Stop();
        _graceTimer.Start();
    }

    /// <summary>鼠标进入了控制条（拖进度条），取消隐藏。</summary>
    public void KeepAlive()
    {
        _graceTimer.Stop();
    }

    /// <summary>立即强制隐藏（滚动、移动窗口、开始导入、Esc）。会同时取消固定。</summary>
    public void HideNow(string reason = "未指明")
    {
        var wasVisible = _panel?.Visibility == Visibility.Visible;

        _debounceTimer.Stop();
        _graceTimer.Stop();
        _pendingItem = null;
        IsPinned = false;

        if (wasVisible) DiagnosticsLog.Mark($"[悬浮预览] 隐藏（原因：{reason}）");

        HidePanel();
    }

    /// <summary>空格键：把选中素材固定成放大预览（再按一次取消）。</summary>
    public void TogglePin(AssetItemViewModel? item, FrameworkElement? tile)
    {
        if (_disposed || _overlay is null) return;

        if (IsPinned)
        {
            IsPinned = false;
            HideNow("取消固定");
            return;
        }

        if (item is null || tile is null) return;

        var rect = GetTileRectIn(tile, _overlay);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        IsPinned = true;
        _pendingItem = item;
        _pendingRect = rect;

        // 固定预览要立刻出来，不走防抖
        ShowPendingNow();
    }

    // ══════════════════════════ 内部 ══════════════════════════

    private void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        ShowPendingNow();
    }

    private void ShowPendingNow()
    {
        var item = _pendingItem;
        if (item is null)
        {
            DiagnosticsLog.Mark("[悬浮预览] 显示被跳过：待显示素材已被取消");
            return;
        }

        if (!IsEnabled || _overlay is null || _panel is null) return;

        var path = item.AbsolutePath;
        if (!File.Exists(path))
        {
            DiagnosticsLog.Mark($"[悬浮预览] 显示被跳过：文件不存在 {path}");
            return;
        }

        var panelWidth = IsPinned ? PinnedWidthDip : PanelWidthDip;
        var panelHeight = IsPinned ? PinnedHeightDip : PanelHeightDip;

        var (x, y) = IsPinned
            ? CenterInOverlay(panelWidth, panelHeight)
            : ComputePosition(_pendingRect, panelWidth, panelHeight,
                              _overlay.ActualWidth, _overlay.ActualHeight);

        _panel.Width = panelWidth;
        _panel.Height = panelHeight;
        Canvas.SetLeft(_panel, x);
        Canvas.SetTop(_panel, y);
        _panel.SetPinnedState(IsPinned);

        _panel.ShowFor(new HoverPreviewRequest(
            item.Id,
            item.FileName,
            path,
            item.Kind,
            ResolvePosterPath(item),
            item.Asset.Width,
            item.Asset.Height));

        _panel.Visibility = Visibility.Visible;

        // 保活轮询只在面板可见且未固定时运行
        if (!IsPinned && !_keepAliveTimer.IsEnabled)
            _keepAliveTimer.Start();

        DiagnosticsLog.Mark(
            $"[悬浮预览] {item.FileName} 卡片=({_pendingRect.X:F0},{_pendingRect.Y:F0}) " +
            $"{_pendingRect.Width:F0}x{_pendingRect.Height:F0} → " +
            $"面板=({x:F0},{y:F0}) {panelWidth:F0}x{panelHeight:F0}" +
            (IsPinned ? " [已固定·居中]" : ""));

        _pendingItem = null;
    }

    /// <summary>
    /// 保活轮询（50ms，仅在面板可见时运行）。
    ///
    /// 双保险：面板已拦截鼠标（进入会触发 KeepAlive），但有两种边缘情况
    /// 没有任何事件能取消隐藏倒计时 —— ① 光标从面板圆角缝隙钻出去又没进
    /// 别的卡片；② 面板刚显示时鼠标已经在面板上。这里只要检测到光标在
    /// 面板范围内，就取消隐藏倒计时。
    /// </summary>
    private void OnKeepAliveTick(object? sender, EventArgs e)
    {
        if (KeepAliveDisabled ||
            _panel?.Visibility != Visibility.Visible || IsPinned || _overlay is null)
        {
            _keepAliveTimer.Stop();
            return;
        }

        if (IsCursorInsidePanel()) _graceTimer.Stop();
    }

    /// <summary>光标当前是否在面板矩形内（屏幕物理坐标 → 覆盖层 DIP 坐标换算）。</summary>
    private bool IsCursorInsidePanel()
    {
        if (_overlay is null || _panel is null || _panel.Visibility != Visibility.Visible)
            return false;

        try
        {
            if (!NativeWindow.TryGetCursorPosition(out var cursorX, out var cursorY))
                return false;

            // 屏幕物理坐标 → 覆盖层 DIP 坐标（WPF 内部会按 DPI 正确换算）
            var point = _overlay.PointFromScreen(new Point(cursorX, cursorY));

            var panelX = Canvas.GetLeft(_panel);
            var panelY = Canvas.GetTop(_panel);

            return point.X >= panelX && point.Y >= panelY &&
                   point.X <= panelX + _panel.ActualWidth &&
                   point.Y <= panelY + _panel.ActualHeight;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnGraceElapsed(object? sender, EventArgs e)
    {
        _graceTimer.Stop();

        if (IsPinned) return;

        if (_panel?.Visibility == Visibility.Visible)
            DiagnosticsLog.Mark("[悬浮预览] 隐藏（原因：宽限期到期）");

        HidePanel();
    }

    private void HidePanel()
    {
        _keepAliveTimer.Stop();

        if (_panel is null) return;

        try
        {
            _panel.Visibility = Visibility.Collapsed;
            _panel.StopPlayback();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("悬浮预览隐藏", ex);
        }
    }

    private (double X, double Y) CenterInOverlay(double width, double height)
        => _overlay is null
            ? (0, 0)
            : ((_overlay.ActualWidth - width) / 2, (_overlay.ActualHeight - height) / 2);

    /// <summary>音频用波形图、照片用自身缩略图做封面；视频不需要（有真实画面）。</summary>
    private string? ResolvePosterPath(AssetItemViewModel item)
    {
        if (string.IsNullOrEmpty(item.Asset.ThumbPath)) return null;
        return _layout.ThumbAbsolutePath(item.Asset.ThumbPath);
    }

    /// <summary>把卡片在覆盖层坐标系里的矩形取出来（全部是 DIP，无物理像素换算）。</summary>
    private static Rect GetTileRectIn(FrameworkElement tile, FrameworkElement relativeTo)
    {
        var topLeft = tile.TranslatePoint(new Point(0, 0), relativeTo);
        var bottomRight = tile.TranslatePoint(new Point(tile.ActualWidth, tile.ActualHeight), relativeTo);
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>
    /// 就近定位（DIP，相对覆盖层）：
    ///   1. 卡片右侧（优先，不挡从上往下的浏览方向）
    ///   2. 卡片左侧
    ///   3. 卡片下方
    ///   4. 卡片上方
    /// 最后统一钳制在覆盖层范围内。
    /// </summary>
    private static (double X, double Y) ComputePosition(
        Rect tile, double width, double height, double areaWidth, double areaHeight)
    {
        var alignedY = Clamp(tile.Top, 0, areaHeight - height);
        var centeredX = Clamp(tile.Left + tile.Width / 2 - width / 2, 0, areaWidth - width);

        // 1. 右侧
        var rightX = tile.Right + GapDip;
        if (rightX + width <= areaWidth)
            return (rightX, alignedY);

        // 2. 左侧
        var leftX = tile.Left - width - GapDip;
        if (leftX >= 0)
            return (leftX, alignedY);

        // 3. 下方
        var belowY = tile.Bottom + GapDip;
        if (belowY + height <= areaHeight)
            return (centeredX, belowY);

        // 4. 上方
        var aboveY = tile.Top - height - GapDip;
        if (aboveY >= 0)
            return (centeredX, aboveY);

        // 5. 兜底：就地钳制
        return (centeredX, alignedY);
    }

    private static double Clamp(double value, double min, double max)
        => Math.Clamp(value, min, Math.Max(min, max));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _debounceTimer.Stop();
            _graceTimer.Stop();
            _keepAliveTimer.Stop();
            _debounceTimer.Tick -= OnDebounceElapsed;
            _graceTimer.Tick -= OnGraceElapsed;
            _keepAliveTimer.Tick -= OnKeepAliveTick;

            if (_overlay is not null && _panel is not null)
                _overlay.Children.Remove(_panel);

            _panel = null;
            _overlay = null;
        }
        catch (Exception)
        {
            // 退出阶段忽略
        }

        GC.SuppressFinalize(this);
    }
}
