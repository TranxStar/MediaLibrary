using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaLibrary.App.Services;
using MediaLibrary.Core;
using MediaLibrary.Core.Models;

namespace MediaLibrary.App.ViewModels;

/// <summary>网格里的一张素材卡片。</summary>
public partial class AssetItemViewModel : ObservableObject
{
    private readonly LibraryLayout _layout;
    private readonly ThumbnailImageCache _thumbCache;
    private int _thumbnailRequested;

    public AssetItemViewModel(Asset asset, LibraryLayout layout, ThumbnailImageCache thumbCache)
    {
        Asset = asset;
        _layout = layout;
        _thumbCache = thumbCache;
        _status = asset.Status;
        _errorMessage = asset.ErrorMessage;

        // 库内文件若已有缩略图缓存，直接命中（同步查缓存，不触发解码）
        if (!string.IsNullOrEmpty(asset.ThumbPath))
            _thumbnail = _thumbCache.TryGet(layout.ThumbAbsolutePath(asset.ThumbPath));
    }

    public Asset Asset { get; }

    public long Id => Asset.Id;
    public string FileName => Asset.FileName;
    public string DisplayName => Asset.DisplayName;
    public AssetKind Kind => Asset.Kind;

    /// <summary>库内文件的绝对路径。</summary>
    public string AbsolutePath => _layout.ToAbsolute(Asset.LibraryPath);

    public string DurationText => Asset.DurationText;
    public bool HasDuration => !string.IsNullOrEmpty(DurationText);

    /// <summary>占位图上显示的扩展名标签，如 <c>MP4</c> / <c>PNG</c>。</summary>
    public string ExtensionLabel => Asset.Extension.TrimStart('.').ToUpperInvariant();

    /// <summary>右下角种类角标。照片不显示（缩略图已经说明了）。</summary>
    public string KindGlyph => Kind switch
    {
        AssetKind.Video => "▶",
        AssetKind.Audio => "♪",
        _ => "",
    };

    public string FileSizeText => FormatBytes(Asset.SizeBytes);

    public string ResolutionText
        => Asset.Width is { } w && Asset.Height is { } h ? $"{w}×{h}" : "";

    // ══════════════════════════ 状态（可观察）══════════════════════

    [ObservableProperty]
    private AssetStatus _status;

    /// <summary>处理进度，0.0 ~ 1.0。</summary>
    [ObservableProperty]
    private double _progressFraction;

    /// <summary>进度百分比文本，如 <c>37%</c>。</summary>
    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string? _errorMessage;

    partial void OnStatusChanged(AssetStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(ShowProgressBar));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(TooltipText));
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(TooltipText));

    public bool IsPending => Status == AssetStatus.Pending;
    public bool IsProcessing => Status == AssetStatus.Processing;
    public bool IsMissing => Status == AssetStatus.Missing;
    public bool IsFailed => Status == AssetStatus.Failed;
    public bool IsReady => Status == AssetStatus.Ready;

    /// <summary>排队中或处理中 → 卡片底部显示进度条。</summary>
    public bool ShowProgressBar => IsPending || IsProcessing;

    /// <summary>还没有缩略图且不在处理中 → 显示占位（扩展名或失败标记）。</summary>
    public bool ShowPlaceholder => Thumbnail is null;

    public string StatusText => Status switch
    {
        AssetStatus.Pending => "排队中",
        AssetStatus.Processing => "处理中",
        AssetStatus.Failed => "失败",
        AssetStatus.Missing => "文件丢失",
        _ => "",
    };

    public string TooltipText
    {
        get
        {
            var lines = new List<string> { FileName };

            if (Status != AssetStatus.Ready) lines.Add($"状态：{StatusText}");
            if (!string.IsNullOrEmpty(ResolutionText)) lines.Add($"分辨率：{ResolutionText}");
            if (HasDuration) lines.Add($"时长：{DurationText}");
            if (!string.IsNullOrEmpty(Asset.VideoCodec)) lines.Add($"视频编码：{Asset.VideoCodec}");
            if (!string.IsNullOrEmpty(Asset.AudioCodec)) lines.Add($"音频编码：{Asset.AudioCodec}");
            if (Asset.IsHdr) lines.Add("HDR / 10-bit");
            lines.Add($"大小：{FileSizeText}");
            if (!string.IsNullOrEmpty(ErrorMessage)) lines.Add($"错误：{ErrorMessage}");
            if (!string.IsNullOrEmpty(Asset.SourcePath)) lines.Add($"来源：{Asset.SourcePath}");

            return string.Join('\n', lines);
        }
    }

    // ══════════════════════════ 缩略图 ══════════════════════════

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private bool _isLoadingThumbnail;

    /// <summary>
    /// 请求加载缩略图。幂等 —— 卡片滚出再滚回来时不会重复解码。
    /// 由卡片的 Loaded 事件触发（配合虚拟化，天然做到"可见才加载"）。
    /// </summary>
    public async void RequestThumbnail()
    {
        if (Thumbnail is not null) return;
        if (string.IsNullOrEmpty(Asset.ThumbPath)) return;

        // 0 → 1，保证只跑一次
        if (Interlocked.CompareExchange(ref _thumbnailRequested, 1, 0) != 0) return;

        IsLoadingThumbnail = true;
        try
        {
            var path = _layout.ThumbAbsolutePath(Asset.ThumbPath);
            var image = await _thumbCache.GetOrLoadAsync(path).ConfigureAwait(true);
            Thumbnail = image;
            OnPropertyChanged(nameof(ShowPlaceholder));
        }
        catch (Exception)
        {
            // 缩略图加载失败只影响观感，不打断界面
        }
        finally
        {
            IsLoadingThumbnail = false;
        }
    }

    /// <summary>缩略图刚生成好时由外部调用。</summary>
    public void ReloadThumbnail()
    {
        Interlocked.Exchange(ref _thumbnailRequested, 0);
        if (!string.IsNullOrEmpty(Asset.ThumbPath))
            _thumbCache.Invalidate(_layout.ThumbAbsolutePath(Asset.ThumbPath));
        RequestThumbnail();
    }

    // ══════════════════════════ 外部更新 ══════════════════════════

    /// <summary>应用来自任务执行器的进度回调。</summary>
    public void ApplyJobProgress(JobProgress update)
    {
        ProgressFraction = update.Fraction;

        // 处理中且进度极小 → 显示"处理中"而不是 0%
        ProgressText = update.Fraction > 0.005
            ? $"{update.Fraction * 100:F0}%"
            : "";

        ErrorMessage = update.ErrorMessage;

        Status = update.Status switch
        {
            JobStatus.Running => AssetStatus.Processing,
            JobStatus.Queued => AssetStatus.Pending,
            JobStatus.Done => AssetStatus.Ready,
            JobStatus.Failed => AssetStatus.Failed,
            _ => Status,
        };
    }

    /// <summary>任务完成后从数据库重新读取状态（拿到最终时长、尺寸等）。</summary>
    public void RefreshFromAsset(Asset asset)
    {
        Asset.Width = asset.Width;
        Asset.Height = asset.Height;
        Asset.DurationMs = asset.DurationMs;
        Asset.VideoCodec = asset.VideoCodec;
        Asset.AudioCodec = asset.AudioCodec;
        Asset.PixFmt = asset.PixFmt;
        Asset.IsHdr = asset.IsHdr;
        Asset.Rotation = asset.Rotation;
        Asset.SizeBytes = asset.SizeBytes;
        Asset.ThumbPath = asset.ThumbPath;
        Asset.ErrorMessage = asset.ErrorMessage;

        Status = asset.Status;
        ErrorMessage = asset.ErrorMessage;
        ProgressFraction = asset.Status == AssetStatus.Ready ? 1 : 0;
        ProgressText = "";

        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(HasDuration));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(TooltipText));

        if (!string.IsNullOrEmpty(asset.ThumbPath)) ReloadThumbnail();
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var index = 0;
        double value = bytes;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return index == 0 ? $"{value:F0} {units[index]}" : $"{value:F1} {units[index]}";
    }
}
