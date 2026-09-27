using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace MediaLibrary.App.Services;

/// <summary>
/// 把 MediaPlayer 的视频帧渲染进一张 <see cref="WriteableBitmap"/>。
///
/// 这是"预览不再是一个独立系统窗口"的关键：LibVLC 的 <c>SetVideoCallbacks</c>
/// 让解码器把每一帧的像素<b>直接交给我们</b>（不往任何窗口输出），
/// 我们再写进普通 WPF 位图 —— 于是预览就是主窗口内的普通控件：
/// 没有原生子窗口、没有空域问题、没有焦点问题、画面区可以点击穿透。
///
/// 代价：帧以 BGRA 进入内存（约 640×360，每帧 ~900KB），
/// 预览窗口就那么大，完全无感；解码仍可用硬件加速。
/// </summary>
public sealed class VideoFrameRenderer : IDisposable
{
    /// <summary>预览分辨率上限。够看清画面即可，没必要拖全尺寸帧进内存。</summary>
    public const int MaxPreviewWidth = 640;
    public const int MaxPreviewHeight = 360;

    private readonly VlcMediaPlayer _player;

    private WriteableBitmap? _bitmap;
    private int _width;
    private int _height;
    private int _stride;
    private bool _callbacksAttached;
    private bool _disposed;

    public VideoFrameRenderer(VlcMediaPlayer player)
    {
        _player = player;
        _width = MaxPreviewWidth;
        _height = MaxPreviewHeight;
        _stride = _width * 4;
        _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
    }

    /// <summary>当前渲染目标位图。绑定到 Image.Source 即可。</summary>
    public WriteableBitmap? Bitmap => _bitmap;

    /// <summary>
    /// 按媒体的实际分辨率（保比例）准备渲染目标，并挂上回调。
    /// 必须在 <c>Play()</c> 之前调用。
    /// </summary>
    public void Prepare(int videoWidth, int videoHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var (w, h) = FitInto(videoWidth, videoHeight, MaxPreviewWidth, MaxPreviewHeight);

        if (!_callbacksAttached)
        {
            // 只需挂一次；之后的媒体通过 SetVideoFormat 调整格式
            _player.SetVideoCallbacks(OnLock, OnUnlock, OnDisplay);
            _callbacksAttached = true;
        }

        if (w == _width && h == _height) return;

        _width = w;
        _height = h;
        _stride = w * 4;
        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
    }

    /// <summary>
    /// lock：VLC 向我们要一块帧缓冲。分配一块原生内存并把指针写回 planes[0]。
    /// 返回值作为 picture 标识，display 时会原样传回来。
    /// </summary>
    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        if (_disposed) return IntPtr.Zero;

        var buffer = Marshal.AllocHGlobal(_stride * _height);
        Marshal.WriteIntPtr(planes, buffer);
        return buffer;
    }

    private static void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        // 帧已就绪，无需处理
    }

    /// <summary>
    /// display：帧就绪。把缓冲交给 UI 线程写进位图，写完释放。
    /// 注意顺序保证：VLC 在 display 返回后不会再碰这块内存，
    /// 所以"异步写 + 写完释放"是安全的。
    /// </summary>
    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_disposed || picture == IntPtr.Zero) return;

        var app = Application.Current;
        var bitmap = _bitmap;

        if (app is null || bitmap is null)
        {
            Marshal.FreeHGlobal(picture);
            return;
        }

        app.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                bitmap.WritePixels(
                    new Int32Rect(0, 0, _width, _height),
                    picture,
                    _stride * _height,
                    _stride);
            }
            catch (Exception)
            {
                // 位图尺寸刚切换时的竞态帧，丢掉即可
            }
            finally
            {
                Marshal.FreeHGlobal(picture);
            }
        }, DispatcherPriority.Render);
    }

    /// <summary>在设定范围内等比缩放。</summary>
    internal static (int Width, int Height) FitInto(int videoWidth, int videoHeight, int maxWidth, int maxHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0) return (maxWidth, maxHeight);

        var scale = Math.Min(maxWidth / (double)videoWidth, maxHeight / (double)videoHeight);
        var width = Math.Max(2, (int)Math.Round(videoWidth * scale));
        var height = Math.Max(2, (int)Math.Round(videoHeight * scale));
        return (width, height);
    }

    public void Dispose()
    {
        _disposed = true;

        // 播放器停止后回调自然不再触发；这里只做标记，避免 Stop 后仍写位图
        _bitmap = null;
    }
}
