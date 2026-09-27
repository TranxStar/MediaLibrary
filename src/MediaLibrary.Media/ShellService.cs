using System.Diagnostics;
using System.Runtime.InteropServices;
using MediaLibrary.Core.Abstractions;

namespace MediaLibrary.Media;

/// <summary>与 Windows 外壳交互：打开文件、在资源管理器中定位、移入回收站。</summary>
public sealed class ShellService : IShellService
{
    /// <summary>
    /// 用系统默认程序打开（.mp4 → 播放器，.png → 看图工具）。
    /// 返回是否成功 —— 系统没有该格式的关联程序时返回 false
    /// （调用方可以转而弹出"选择打开方式"对话框，FLAC 等格式常见此情况）。
    /// </summary>
    public bool OpenFile(string path)
    {
        if (!File.Exists(path)) return false;

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>弹出 Windows"你要如何打开这个文件？"对话框（默认程序缺失时用）。</summary>
    public void OpenWithDialog(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe")
            {
                Arguments = $"shell32.dll,OpenAs_RunDLL \"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 对话框都弹不出来就只能算了
        }
    }

    /// <summary>在资源管理器中打开所在目录并选中该文件。</summary>
    public void RevealInExplorer(string path)
    {
        if (!File.Exists(path)) return;

        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// 移入回收站（设计文档 §6.3：Delete 键不永久删除）。
    /// 走 shell 的 SHFileOperation，带 FOF_ALLOWUNDO 才有回收站语义。
    /// </summary>
    public bool MoveToRecycleBin(string path)
    {
        if (!File.Exists(path)) return false;

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // pFrom 必须以双 null 结尾
            pFrom = path + '\0' + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };

        try
        {
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
