namespace MediaLibrary.Core.Models;

/// <summary>素材种类。决定入库时走哪条处理管线。</summary>
public enum AssetKind
{
    /// <summary>视频 → 转 H.265</summary>
    Video,

    /// <summary>照片 → 转 PNG</summary>
    Photo,

    /// <summary>音频 → 仅复制，不转码</summary>
    Audio,
}

/// <summary>素材状态机（见设计文档 §4.3）。</summary>
public enum AssetStatus
{
    /// <summary>已入库排队，尚未开始处理。</summary>
    Pending,

    /// <summary>正在转码/转换。</summary>
    Processing,

    /// <summary>处理完成，可用。</summary>
    Ready,

    /// <summary>处理失败，可重试。</summary>
    Failed,

    /// <summary>库内文件丢失（被外部删除/移动）。</summary>
    Missing,
}

/// <summary>后台任务类型。</summary>
public enum JobType
{
    TranscodeVideo,
    ConvertPhoto,
    CopyAudio,
    Thumbnail,
}

/// <summary>后台任务状态。</summary>
public enum JobStatus
{
    Queued,
    Running,
    Paused,
    Done,
    Failed,
    Canceled,
}
