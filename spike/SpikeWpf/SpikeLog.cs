using System.IO;
using System.Text;

namespace SpikeWpf;

/// <summary>把验证过程写进日志文件，便于无人值守运行后回读结果。</summary>
public static class SpikeLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"=== SpikeWpf 日志 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n", Encoding.UTF8);
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (Gate)
        {
            if (_path is not null) File.AppendAllText(_path, line + "\n", Encoding.UTF8);
        }
    }
}
