using MediaLibrary.Core.Models;

namespace MediaLibrary.Core.Abstractions;

/// <summary>读取媒体元数据（ffprobe 封装）。</summary>
public interface IMediaProbe
{
    /// <summary>探测失败时返回 null（文件损坏或格式不支持）。</summary>
    Task<MediaProbe?> ProbeAsync(string path, CancellationToken ct = default);
}

/// <summary>生成缩略图。</summary>
public interface IThumbnailer
{
    /// <summary>
    /// 为素材生成缩略图。
    /// </summary>
    /// <param name="sourcePath">源文件绝对路径（转码前用源，转码后用库内文件）。</param>
    /// <param name="kind">素材种类，决定抽帧策略。</param>
    /// <param name="outputPath">缩略图输出绝对路径。</param>
    /// <param name="durationMs">
    /// 已知的媒体时长。视频抽帧位置取决于它（避开片头黑场）；
    /// 传 null 则会从 0 秒附近抽帧。
    /// </param>
    /// <returns>成功返回 true；失败返回 false（界面显示占位图）。</returns>
    Task<bool> CreateAsync(
        string sourcePath,
        AssetKind kind,
        string outputPath,
        long? durationMs = null,
        CancellationToken ct = default);
}

/// <summary>外部播放器/资源管理器交互。</summary>
public interface IShellService
{
    /// <summary>用系统默认程序打开文件；系统无关联程序时返回 false。</summary>
    bool OpenFile(string path);

    /// <summary>弹出 Windows"选择打开方式"对话框（默认程序缺失时）。</summary>
    void OpenWithDialog(string path);

    /// <summary>在资源管理器中定位并选中文件。</summary>
    void RevealInExplorer(string path);

    /// <summary>把文件移入回收站（不是永久删除）。</summary>
    bool MoveToRecycleBin(string path);
}
