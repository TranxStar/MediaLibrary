using System.Diagnostics;
using System.IO;
using System.Text;

namespace MediaLibrary.App.Services;

/// <summary>
/// 启动阶段计时日志。
///
/// 写到 <c>%TEMP%\MediaLibrary\startup.log</c>，用于客观度量"冷启动到首屏"耗时。
/// 出问题时把文件内容贴出来就能定位卡在哪一步。
/// </summary>
public static class DiagnosticsLog
{
    private static readonly string LogDirectory =
        Path.Combine(Path.GetTempPath(), "MediaLibrary");

    private static readonly string LogPath = Path.Combine(LogDirectory, "startup.log");

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Lock Gate = new();

    public static string FilePath => LogPath;

    /// <summary>记录一个带相对时间戳的里程碑。</summary>
    public static void Mark(string message)
    {
        var line = $"[+{Clock.ElapsedMilliseconds,6} ms]  {DateTime.Now:HH:mm:ss.fff}  {message}";

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception)
            {
                // 日志失败绝不能影响程序运行
            }
        }

        Debug.WriteLine(line);
    }

    /// <summary>开始一次新的启动记录（清空上次内容）。</summary>
    public static void BeginSession()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.WriteAllText(LogPath,
                    $"════════════════════════════════════════════════════════{Environment.NewLine}" +
                    $"  素材库启动日志  {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                    $"  环境: {Environment.Version} / {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}" +
                    $"{Environment.NewLine}" +
                    $"════════════════════════════════════════════════════════{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        Clock.Restart();
    }

    /// <summary>记录异常。</summary>
    public static void Error(string context, Exception ex)
        => Mark($"✗ {context}: {ex.GetType().Name}: {ex.Message}");
}
