using System.Text;

namespace MediaLibrary.Core.Naming;

/// <summary>
/// 命名引擎（设计文档 §5.2）。
/// 规则：<c>{最内层文件夹名}_{序号}.{ext}</c>，序号在每个文件夹内独立。
/// </summary>
public static class NamingEngine
{
    /// <summary>序号最小位数。超过 999 自动扩位。</summary>
    public const int MinDigits = 3;

    /// <summary>文件夹名最大长度。</summary>
    public const int MaxFolderNameLength = 60;

    /// <summary>Windows 文件名非法字符。</summary>
    private static readonly char[] IllegalChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Windows 保留设备名（不区分大小写，含带扩展名的形式）。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>格式化序号：&lt;1000 用 3 位，之后自动扩位。</summary>
    public static string FormatIndex(int index)
    {
        if (index < 0) index = 0;
        var digits = MinDigits;
        var probe = 1000;
        while (index >= probe) { digits++; probe *= 10; }
        return index.ToString($"D{digits}");
    }

    /// <summary>拼装库内文件名，如 <c>开幕式_001.mp4</c>。</summary>
    public static string BuildFileName(string folderName, int index, string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var safeFolder = SanitizeFolderName(folderName);
        return $"{safeFolder}_{FormatIndex(index)}{ext.ToLowerInvariant()}";
    }

    /// <summary>
    /// 清洗文件夹名：替换非法字符、去尾部点与空格、避开保留设备名、限长。
    /// </summary>
    public static string SanitizeFolderName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return LibraryPathHelper.FallbackFolder;

        var sb = new StringBuilder(rawName.Length);
        foreach (var ch in rawName)
        {
            if (char.IsControl(ch)) continue;
            sb.Append(Array.IndexOf(IllegalChars, ch) >= 0 ? '_' : ch);
        }

        var name = sb.ToString().Trim();

        // Windows 不允许名字以点或空格结尾
        name = name.TrimEnd('.', ' ');

        if (name.Length > MaxFolderNameLength)
            name = name[..MaxFolderNameLength].TrimEnd('.', ' ');

        if (name.Length == 0) return LibraryPathHelper.FallbackFolder;

        // 保留设备名加前缀规避（CON → _CON）
        var stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(stem)) name = "_" + name;

        return name;
    }

    /// <summary>清洗整段文件名（用于重命名功能）。</summary>
    public static string SanitizeFileName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return "未命名";
        var stem = SanitizeFolderName(Path.GetFileNameWithoutExtension(rawName));
        var ext = Path.GetExtension(rawName);
        return stem + ext;
    }

    /// <summary>取 FolderKey 的最内层目录名。这是文件名的前缀来源。</summary>
    public static string InnermostFolderName(string folderKey)
    {
        if (string.IsNullOrEmpty(folderKey)) return LibraryPathHelper.FallbackFolder;
        var idx = folderKey.LastIndexOf('/');
        return idx < 0 ? folderKey : folderKey[(idx + 1)..];
    }
}
