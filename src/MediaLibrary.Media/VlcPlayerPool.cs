using LibVLCSharp.Shared;

namespace MediaLibrary.Media;

/// <summary>
/// LibVLC 实例与播放器的复用池（设计文档 §5.8）。
///
/// 为什么要复用：
/// <list type="bullet">
///   <item>创建 <see cref="LibVLC"/> 实例有约 100ms 开销 —— 全局只创建一次</item>
///   <item><see cref="MediaPlayer"/> 也复用，避免每次悬停都走一遍初始化</item>
///   <item>全局只有一个悬停播放器，<b>物理上不可能同时播放多个</b>，
///         天然解决"鼠标划过一排卡片会同时出声"的问题</item>
/// </list>
/// </summary>
public sealed class VlcPlayerPool : IDisposable
{
    private readonly Lock _gate = new();

    private LibVLC? _libvlc;
    private MediaPlayer? _hoverPlayer;
    private MediaPlayer? _fullPlayer;
    private bool _disposed;

    /// <summary>LibVLC 实例。首次访问时才真正创建。</summary>
    public LibVLC LibVLC
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _libvlc ??= Create();
            }
        }
    }

    /// <summary>悬停预览专用播放器。全程复用，不销毁。</summary>
    public MediaPlayer HoverPlayer
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _hoverPlayer ??= new MediaPlayer(LibVLC);
            }
        }
    }

    /// <summary>空格键放大预览专用播放器（与悬停互不干扰）。</summary>
    public MediaPlayer FullPlayer
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _fullPlayer ??= new MediaPlayer(LibVLC);
            }
        }
    }

    /// <summary>LibVLC 是否已创建（用于判断要不要显示"预热中"）。</summary>
    public bool IsInitialized
    {
        get { lock (_gate) return _libvlc is not null; }
    }

    public string? Version
    {
        get { lock (_gate) return _libvlc?.Version; }
    }

    /// <summary>
    /// 提前创建 LibVLC 与播放器。放在 App 启动时后台调用，
    /// 这样第一次悬停不会卡在初始化上（设计要求悬停 ≤400ms 出画面）。
    /// </summary>
    public void WarmUp()
    {
        try
        {
            _ = LibVLC;
            _ = HoverPlayer;
        }
        catch (Exception)
        {
            // 预热失败不影响主流程，真正用到时会再试
        }
    }

    private static LibVLC Create()
    {
        // 必须全限定：本项目里 `Core` 会被解析成 MediaLibrary.Core 命名空间
        LibVLCSharp.Shared.Core.Initialize();

        return new LibVLC(
            // 不显示文件名浮层
            "--no-video-title-show",
            // 抑制控制台刷屏
            "--quiet",
            // 本地文件缓存调小 → 启动更快、seek 更跟手
            "--file-caching=300",
            "--network-caching=0",
            // 硬件解码优先
            "--avcodec-hw=any",
            // 单实例（避免多实例互相干扰）
            "--no-one-instance"
        );
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            TryDispose(_hoverPlayer);
            TryDispose(_fullPlayer);
            TryDispose(_libvlc);

            _hoverPlayer = null;
            _fullPlayer = null;
            _libvlc = null;
        }
    }

    private static void TryDispose(IDisposable? disposable)
    {
        try { disposable?.Dispose(); }
        catch (Exception) { /* 退出阶段忽略 */ }
    }
}
