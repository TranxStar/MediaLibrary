using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using MediaLibrary.App.Services;
using MediaLibrary.Core.Models;
using MediaLibrary.Media;
// 注意 1：引入 MediaLibrary.Media 命名空间后，裸写的 `Media` 会被解析成命名空间。
// 注意 2：不能 using System.Windows.Media 之外再引入它的 MediaPlayer —— 已限定。
using VlcMedia = LibVLCSharp.Shared.Media;

namespace MediaLibrary.App.Views;

/// <summary>一次悬浮预览请求。</summary>
public sealed record HoverPreviewRequest(
    long AssetId,
    string DisplayName,
    string MediaPath,
    AssetKind Kind,
    string? PosterPath,
    int? VideoWidth = null,
    int? VideoHeight = null);

/// <summary>
/// 主窗口内的悬浮预览面板。
///
/// 与旧版（独立系统窗口）的根本区别：视频帧由 <see cref="VideoFrameRenderer"/>
/// 写进 WriteableBitmap，预览就是一张普通 WPF 图片 —— 没有原生子窗口，
/// 没有空域问题，画面区<b>拦截鼠标</b>（不穿透），被面板盖住的卡片不会被
/// 误触发，控制条随时可点。
///
/// 控制条是手工实现的刮擦条：整行可点击（点击即跳转）、按住拖动、松开落定。
/// </summary>
public partial class HoverPreviewPanel : UserControl
{
    private readonly VlcPlayerPool _pool;
    private readonly ThumbnailImageCache _thumbCache;
    private readonly VideoFrameRenderer _renderer;
    private readonly DispatcherTimer _ticker;

    private VlcMediaPlayer? _player;
    private VlcMedia? _currentMedia;
    private string? _currentPath;
    private bool _isScrubbing;
    private double _scrubFraction;
    private long _durationMs;

    /// <summary>鼠标进入控制条（用来取消隐藏倒计时）。</summary>
    public event EventHandler? PointerEntered;

    /// <summary>鼠标离开控制条。</summary>
    public event EventHandler? PointerLeft;

    public HoverPreviewPanel(VlcPlayerPool pool, ThumbnailImageCache thumbCache)
    {
        _pool = pool;
        _thumbCache = thumbCache;
        _renderer = new VideoFrameRenderer(pool.HoverPlayer);

        InitializeComponent();

        _ticker = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _ticker.Tick += OnTick;

        ControlBar.LostMouseCapture += (_, _) => EndScrub();

        // 面板整体拦截鼠标：进入即取消隐藏倒计时；离开（真正出了面板范围）才上报。
        RootGrid.MouseEnter += (_, _) => PointerEntered?.Invoke(this, EventArgs.Empty);
        RootGrid.MouseLeave += (_, _) =>
        {
            // 圆角外侧有极小缝隙（不属于任何子元素），光标划过时会短暂
            // "离开"→立刻又"进入"。用几何判断兜底：光标还在面板矩形内
            // 就不上报离开，否则会误启动隐藏倒计时。
            var pos = Mouse.GetPosition(RootGrid);
            var inside = pos.X >= 0 && pos.Y >= 0 &&
                         pos.X <= RootGrid.ActualWidth && pos.Y <= RootGrid.ActualHeight;
            if (!inside) PointerLeft?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>当前正在预览的素材 Id；没有则为 null。</summary>
    public long? CurrentAssetId { get; private set; }

    /// <summary>当前播放位置（毫秒）；无播放器时为 -1。供诊断使用。</summary>
    public long PositionMs => _player?.Time ?? -1;

    /// <summary>媒体总时长（毫秒）。</summary>
    public long DurationMs => _player?.Length ?? 0;

    /// <summary>播放器状态描述。供诊断使用。</summary>
    public string PlayerStateText => _player?.State.ToString() ?? "(无播放器)";

    /// <summary>显示/隐藏"已固定"标记。</summary>
    public void SetPinnedState(bool pinned)
        => PinBadge.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>开始（或切换）预览。</summary>
    public void ShowFor(HoverPreviewRequest request)
    {
        CurrentAssetId = request.AssetId;
        NameText.Text = request.DisplayName;

        var isAudio = request.Kind == AssetKind.Audio;
        var isVideo = request.Kind == AssetKind.Video;

        AudioBadge.Visibility = isAudio ? Visibility.Visible : Visibility.Collapsed;

        Poster.Source = LoadPoster(request.PosterPath);
        Poster.Visibility = Poster.Source is null ? Visibility.Collapsed : Visibility.Visible;

        var player = _pool.HoverPlayer;
        _player = player;

        // 视频静音（悬浮预览不该突然出声）；音频音量 80
        player.Volume = isAudio ? 80 : 0;

        if (isVideo)
        {
            // 按素材已知分辨率准备渲染目标（还没元数据时用 16:9 默认值）
            _renderer.Prepare(request.VideoWidth ?? 640, request.VideoHeight ?? 360);
            VideoImage.Source = _renderer.Bitmap;
            VideoImage.Visibility = Visibility.Visible;
        }
        else
        {
            VideoImage.Visibility = Visibility.Collapsed;
        }

        if (isVideo || isAudio)
        {
            StartPlayback(request.MediaPath);
            _ticker.Start();
        }
        else
        {
            // 照片：不播放，只显示放大图
            _ticker.Stop();
            ResetProgress();
        }
    }

    /// <summary>停止播放并复位（面板隐藏由控制器负责）。</summary>
    public void StopPlayback()
    {
        _ticker.Stop();
        EndScrub();

        try
        {
            if (_player is not null)
            {
                _player.Stop();
                _player.Volume = 0;
            }
        }
        catch (Exception)
        {
            // 停止失败不影响隐藏
        }

        CurrentAssetId = null;
        _currentPath = null;
        ResetProgress();
        DisposeMedia();
    }

    // ══════════════════════════ 刮擦条 ══════════════════════════

    private void OnScrubDown(object sender, MouseButtonEventArgs e)
    {
        _isScrubbing = true;
        ControlBar.CaptureMouse();
        UpdateScrubFromPoint(e.GetPosition(TrackArea));
    }

    private void OnScrubMove(object sender, MouseEventArgs e)
    {
        if (!_isScrubbing) return;
        UpdateScrubFromPoint(e.GetPosition(TrackArea));
    }

    private void OnScrubUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isScrubbing) return;
        UpdateScrubFromPoint(e.GetPosition(TrackArea));
        EndScrub();
    }

    private void EndScrub()
    {
        if (!_isScrubbing) return;
        _isScrubbing = false;

        try
        {
            if (ControlBar.IsMouseCaptured) ControlBar.ReleaseMouseCapture();
        }
        catch (Exception)
        {
            // 捕获可能已被系统释放
        }

        CommitSeek();
    }

    private void UpdateScrubFromPoint(Point point)
    {
        var width = TrackArea.ActualWidth;
        if (width <= 0) return;

        _scrubFraction = Math.Clamp(point.X / width, 0, 1);
        UpdateScrubVisual();
    }

    private void UpdateScrubVisual()
    {
        var width = TrackArea.ActualWidth;
        if (width <= 0) return;

        FillBar.Width = _scrubFraction * width;
        ThumbDot.Margin = new Thickness(_scrubFraction * width - ThumbDot.Width / 2, 0, 0, 0);

        if (_durationMs > 0)
            TimeText.Text = $"{Format((long)(_scrubFraction * _durationMs))} / {Format(_durationMs)}";
    }

    private void CommitSeek()
    {
        try
        {
            if (_player is not null) _player.Position = (float)_scrubFraction;
        }
        catch (Exception)
        {
            // 播放器可能已停止
        }
    }

    /// <summary>诊断用：模拟一次刮擦（更新视觉并提交 seek）。</summary>
    public void DiagnosticSeek(double fraction)
    {
        _isScrubbing = true;
        _scrubFraction = Math.Clamp(fraction, 0, 1);
        UpdateScrubVisual();
        _isScrubbing = false;
        CommitSeek();
    }

    // ══════════════════════════ 内部 ══════════════════════════

    private void StartPlayback(string mediaPath)
    {
        var player = _player;
        if (player is null) return;

        // 同一个文件且已经在播 → 不打断
        if (string.Equals(_currentPath, mediaPath, StringComparison.OrdinalIgnoreCase))
        {
            if (player.State is not (VLCState.Playing or VLCState.Buffering))
            {
                try { player.Play(); } catch (Exception) { }
            }
            return;
        }

        DisposeMedia();
        _currentPath = mediaPath;

        try
        {
            _currentMedia = new VlcMedia(_pool.LibVLC, mediaPath, FromType.FromPath);
            player.Play(_currentMedia);
        }
        catch (Exception)
        {
            // 打不开的文件就只显示封面
        }
    }

    private ImageSource? LoadPoster(string? posterPath)
    {
        if (string.IsNullOrEmpty(posterPath) || !File.Exists(posterPath)) return null;

        var cached = _thumbCache.TryGet(posterPath);
        if (cached is not null) return cached;

        _ = LoadPosterAsync(posterPath, CurrentAssetId);
        return null;
    }

    private async Task LoadPosterAsync(string path, long? assetId)
    {
        try
        {
            var image = await _thumbCache.GetOrLoadAsync(path).ConfigureAwait(true);

            if (image is not null && assetId == CurrentAssetId)
            {
                Poster.Source = image;
                Poster.Visibility = Visibility.Visible;
            }
        }
        catch (Exception)
        {
            // 解码失败就用黑底
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var player = _player;
        if (player is null) return;

        if (player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
        {
            _ticker.Stop();
            return;
        }

        _durationMs = player.Length;

        // 拖动中不回写，否则进度条会和用户的手抢
        if (_isScrubbing) return;

        var length = player.Length;
        if (length <= 0) return;

        var fraction = player.Position;
        if (fraction is >= 0 and <= 1)
        {
            _scrubFraction = fraction;

            var width = TrackArea.ActualWidth;
            if (width > 0)
            {
                FillBar.Width = fraction * width;
                ThumbDot.Margin = new Thickness(fraction * width - ThumbDot.Width / 2, 0, 0, 0);
            }
        }

        TimeText.Text = $"{Format(player.Time)} / {Format(length)}";
    }

    private void ResetProgress()
    {
        _scrubFraction = 0;
        _durationMs = 0;
        FillBar.Width = 0;
        ThumbDot.Margin = new Thickness(-ThumbDot.Width / 2, 0, 0, 0);
        TimeText.Text = "00:00 / 00:00";
    }

    private static string Format(long milliseconds)
    {
        if (milliseconds < 0) milliseconds = 0;
        var t = TimeSpan.FromMilliseconds(milliseconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    private void DisposeMedia()
    {
        var media = _currentMedia;
        _currentMedia = null;

        try { media?.Dispose(); }
        catch (Exception) { }
    }
}
