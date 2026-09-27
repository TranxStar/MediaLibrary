using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaLibrary.App.Services;

/// <summary>
/// 应用级配置，存在 <c>%APPDATA%\MediaLibrary\settings.json</c>。
/// 与库内的 Settings 表区分：这里存的是"这台机器怎么打开库"，库内表存的是"这个库怎么处理素材"。
/// </summary>
public sealed class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaLibrary");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>素材库根目录。首次启动由用户选择（设计文档附录 B 问题 2）。</summary>
    public string? LibraryRoot { get; set; }

    /// <summary>最近打开过的库（供切换）。</summary>
    public List<string> RecentLibraries { get; set; } = [];

    /// <summary>缩略图卡片宽度（DIP）。</summary>
    public double ThumbnailSize { get; set; } = 200;

    /// <summary>剩余空间低于该值时告警（GB）。</summary>
    public int SpaceWarningGb { get; set; } = 10;

    /// <summary>悬浮预览总开关。</summary>
    public bool HoverPreviewEnabled { get; set; } = true;

    /// <summary>悬停多久才弹出预览（毫秒）。</summary>
    public int HoverPreviewDelayMs { get; set; } = 280;

    /// <summary>右键菜单：打开。</summary>
    public bool MenuOpen { get; set; } = true;

    /// <summary>右键菜单：在资源管理器中显示。</summary>
    public bool MenuReveal { get; set; } = true;

    /// <summary>右键菜单：重新转码。</summary>
    public bool MenuRetranscode { get; set; } = true;

    /// <summary>右键菜单：重命名。</summary>
    public bool MenuRename { get; set; } = true;

    /// <summary>右键菜单：删除到回收站。</summary>
    public bool MenuDelete { get; set; } = true;

    /// <summary>右键菜单：复制文件路径。</summary>
    public bool MenuCopyPath { get; set; } = true;

    /// <summary>窗口尺寸与位置。</summary>
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception)
        {
            // 配置损坏不应阻止启动
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // 保存失败不影响使用
        }
    }

    /// <summary>记住一个库路径。</summary>
    public void RememberLibrary(string root)
    {
        LibraryRoot = root;

        RecentLibraries.RemoveAll(p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase));
        RecentLibraries.Insert(0, root);

        if (RecentLibraries.Count > 10)
            RecentLibraries.RemoveRange(10, RecentLibraries.Count - 10);
    }
}
