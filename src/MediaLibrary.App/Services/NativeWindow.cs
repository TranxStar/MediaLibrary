using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MediaLibrary.App.Services;

/// <summary>
/// 悬浮预览窗口需要的 Win32 操作。
///
/// 为什么不用 WPF 的 <c>Window.Left/Top</c>：
/// 那两个属性是 DIP 单位，而 DIP→物理像素的换算在 PerMonitorV2 + 多显示器场景下
/// 取决于窗口当前所在显示器，容易出现"移动后才纠正"的跳变。
/// 阶段0 实测本机是 <b>200% DPI 缩放</b>，坐标换算必须一次到位。
/// 直接用 <c>SetWindowPos</c> 传物理像素，行为完全确定。
/// </summary>
internal static class NativeWindow
{
    private const int GWL_EXSTYLE = -20;

    private const int WS_EX_NOACTIVATE = 0x08000000;  // 显示与点击都不激活窗口
    private const int WS_EX_TOOLWINDOW = 0x00000080;  // 不出现在 Alt+Tab 与任务栏

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    /// <summary>屏幕矩形（物理像素）。</summary>
    public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

        public int ClampX(int value, int itemWidth)
            => Math.Clamp(value, Left, Math.Max(Left, Right - itemWidth));

        public int ClampY(int value, int itemHeight)
            => Math.Clamp(value, Top, Math.Max(Top, Bottom - itemHeight));

        /// <summary>与另一个矩形是否相交（用于判断缩放窗口是否落在工作区内）。</summary>
        public bool IntersectsWith(ScreenRect other)
            => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;

        public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
    }

    /// <summary>
    /// 取当前鼠标位置的屏幕物理坐标。
    ///
    /// 为什么需要它：原生视频子窗口会吞掉鼠标消息（空域问题的另一面），
    /// WPF 的 MouseMove 在视频区域收不到事件。要判断"鼠标是不是飘到了预览窗上"，
    /// 只能主动去问系统光标在哪。
    /// </summary>
    public static bool TryGetCursorPosition(out int x, out int y)
    {
        if (GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    /// <summary>
    /// 让窗口永不抢焦点。
    ///
    /// ⚠️ 阶段0 实测：只设 <c>ShowActivated="False"</c> 是<b>不够</b>的 ——
    /// 日志里仍能看到 <c>OnActivated</c> 被触发，即鼠标划过素材库会打断你正在打的字。
    /// 必须加上 <c>WS_EX_NOACTIVATE</c>。
    /// </summary>
    public static void MakeNonActivating(Window window)
    {
        var handle = GetHandle(window);
        if (handle == IntPtr.Zero) return;

        try
        {
            var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
            var updated = style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;

            if (updated != style)
                SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(updated));
        }
        catch (Exception)
        {
            // 设置失败只是可能抢焦点，不影响功能
        }
    }

    /// <summary>用物理像素设置窗口位置与大小（不激活、不改变 Z 序）。</summary>
    public static void SetPhysicalBounds(Window window, int x, int y, int width, int height)
    {
        var handle = GetHandle(window);
        if (handle == IntPtr.Zero) return;

        SetWindowPos(handle, IntPtr.Zero, x, y, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>取包含指定物理点的显示器工作区（已排除任务栏）。</summary>
    public static ScreenRect GetMonitorWorkArea(int physicalX, int physicalY)
    {
        var point = new POINT { X = physicalX, Y = physicalY };
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return new ScreenRect(
                info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        }

        // 兜底：用主屏
        return new ScreenRect(0, 0,
            (int)SystemParameters.PrimaryScreenWidth,
            (int)SystemParameters.PrimaryScreenHeight);
    }

    /// <summary>确保窗口句柄已创建（不显示窗口）。</summary>
    public static IntPtr GetHandle(Window window)
    {
        try
        {
            return new WindowInteropHelper(window).EnsureHandle();
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>取窗口的屏幕矩形（物理像素）。</summary>
    public static bool TryGetWindowRect(IntPtr handle, out ScreenRect rect)
    {
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var r))
        {
            rect = new ScreenRect(r.Left, r.Top, r.Right, r.Bottom);
            return true;
        }

        rect = default;
        return false;
    }

    /// <summary>
    /// 当前前台窗口句柄。
    /// 用于验证"悬浮预览不抢焦点"：预览弹出后，前台窗口必须仍是主窗口。
    /// </summary>
    public static IntPtr GetForegroundWindow() => GetForegroundWindowNative();

    /// <summary>把窗口置前（仅用于诊断截图时确保主窗口可见）。</summary>
    public static void BringToFront(IntPtr handle)
    {
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
    }

    /// <summary>
    /// 取窗口所在显示器的 DPI 缩放（1.0 = 100%，2.0 = 200%）。
    ///
    /// 不用 <c>SystemParameters.WorkArea</c>：它在高 DPI 下的单位容易让人判断错，
    /// 而 <c>GetDpiForWindow</c> 返回的是确实的数值。
    /// </summary>
    public static double GetDpiScaleForWindow(IntPtr handle)
    {
        try
        {
            if (handle == IntPtr.Zero) return 1.0;

            var dpi = GetDpiForWindow(handle);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch (Exception)
        {
            return 1.0;
        }
    }

    /// <summary>把光标移到指定屏幕物理坐标。仅供诊断使用（测真实鼠标路径）。</summary>
    public static bool SetCursorPosition(int x, int y)
    {
        try
        {
            return SetCursorPos(x, y);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 模拟一次真实的"按下-拖动-松开"（绝对屏幕坐标，走 mouse_event）。
    /// 这是唯一能验证刮擦条真实拖动的方式 —— 程序内部调用测不出鼠标捕获问题。
    ///
    /// ⚠️ 该方法连续注入并 Thread.Sleep，会阻塞 UI 线程（DispatcherTimer 不运行），
    /// 只适合"松手后一次性见效"的场景。需要"按住保持"（如框选拖出边界的自动滚动）
    /// 请用 <see cref="SimulateMouseDown"/> / <see cref="SimulateMouseMove"/> /
    /// <see cref="SimulateMouseUp"/> 分步配合 await 调用。
    /// </summary>
    public static void SimulateMouseDrag(int fromX, int fromY, int toX, int toY,
        int stepDelayMs = 18, int steps = 10)
    {
        SimulateMouseDown(fromX, fromY);

        for (var i = 1; i <= steps; i++)
        {
            var x = fromX + (toX - fromX) * i / steps;
            var y = fromY + (toY - fromY) * i / steps;
            MoveCursorTo(x, y);
            Thread.Sleep(stepDelayMs);
        }

        SimulateMouseUp();
    }

    /// <summary>按下左键（先把光标移到指定屏幕坐标）。调用方用 await 留出 UI 空闲时间。</summary>
    public static void SimulateMouseDown(int x, int y)
    {
        MoveCursorTo(x, y);
        Thread.Sleep(40);
        mouse_event(MOUSE_EVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
    }

    /// <summary>移动光标（按住期间用）。</summary>
    public static void SimulateMouseMove(int x, int y) => MoveCursorTo(x, y);

    /// <summary>松开左键。</summary>
    public static void SimulateMouseUp()
    {
        mouse_event(MOUSE_EVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(80);
    }

    private static void MoveCursorTo(int x, int y)
    {
        var screenW = GetSystemMetrics(SM_CXSCREEN);
        var screenH = GetSystemMetrics(SM_CYSCREEN);
        if (screenW <= 0) screenW = 2560;
        if (screenH <= 0) screenH = 1600;

        var nx = (uint)(x * 65535 / screenW);
        var ny = (uint)(y * 65535 / screenH);
        mouse_event(MOUSE_EVENTF_MOVE | MOUSE_EVENTF_ABSOLUTE, nx, ny, 0, UIntPtr.Zero);
    }

    // ── P/Invoke ──────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint MOUSE_EVENTF_MOVE = 0x0001;
    private const uint MOUSE_EVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSE_EVENTF_LEFTUP = 0x0004;
    private const uint MOUSE_EVENTF_ABSOLUTE = 0x8000;
}
