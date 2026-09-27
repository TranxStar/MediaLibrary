using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaLibrary.App.Services;
using MediaLibrary.App.Views;
using MediaLibrary.Core;
using MediaLibrary.Core.Abstractions;
using MediaLibrary.Core.Models;
using MediaLibrary.Core.Naming;
using MediaLibrary.Data.Repositories;
using MediaLibrary.Data.Services;
using Microsoft.VisualBasic.FileIO;

namespace MediaLibrary.App.ViewModels;

/// <summary>
/// 主界面 ViewModel。
///
/// 启动流程（设计文档 §7 的性能目标）：
/// 先把窗口显示出来，再后台扫描 → 建树 → 加载首个目录 → 串流缩略图 → 续跑未完成任务。
/// 绝不阻塞 UI 线程等待扫描完成。
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly LibraryLayout _layout;
    private readonly AssetRepository _assets;
    private readonly JobRepository _jobs;
    private readonly LibrarySyncService _syncService;
    private readonly AssetEnrichmentService _enrichment;
    private readonly ImportService _importService;
    private readonly ImportJobRunner _jobRunner;
    private readonly IVideoTranscoder _transcoder;
    private readonly ThumbnailImageCache _thumbCache;
    private readonly IShellService _shell;
    private readonly AppSettings _settings;
    private readonly JobQueueService _queue;
    private readonly NamingCounterRepository _counters;

    private readonly Dictionary<long, AssetItemViewModel> _itemsById = [];
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private bool _jobsRunning;

    public MainViewModel(
        LibraryLayout layout,
        AssetRepository assets,
        JobRepository jobs,
        LibrarySyncService syncService,
        AssetEnrichmentService enrichment,
        ImportService importService,
        ImportJobRunner jobRunner,
        IVideoTranscoder transcoder,
        ThumbnailImageCache thumbCache,
        IShellService shell,
        AppSettings settings,
        JobQueueService queue,
        NamingCounterRepository counters)
    {
        _layout = layout;
        _assets = assets;
        _jobs = jobs;
        _syncService = syncService;
        _enrichment = enrichment;
        _importService = importService;
        _jobRunner = jobRunner;
        _transcoder = transcoder;
        _thumbCache = thumbCache;
        _shell = shell;
        _settings = settings;
        _queue = queue;
        _counters = counters;

        // 用户点"继续"后，续跑排队任务
        _queue.PauseStateChanged += () =>
        {
            if (!_queue.IsPaused) _ = RunJobsAsync();
        };

        ThumbnailSize = settings.ThumbnailSize;
        LibraryRootText = layout.Root;
        EncoderText = _transcoder.IsHardwareAccelerationAvailable
            ? "NVENC 硬件加速"
            : "CPU 编码";
    }

    // ══════════════════════════ 集合 ══════════════════════════

    public ObservableCollection<FolderNodeViewModel> FolderTree { get; } = [];
    public ObservableCollection<AssetItemViewModel> Assets { get; } = [];

    /// <summary>当前选中的素材（与 ListBox.SelectedItems 同步）。</summary>
    public ObservableCollection<AssetItemViewModel> SelectedAssets { get; } = [];

    /// <summary>应用设置（右键菜单可见性等 XAML 绑定用）。</summary>
    public AppSettings Settings => _settings;

    /// <summary>当前实际使用的库根目录（诊断用；可能被环境变量临时覆盖）。</summary>
    public string LibraryRootPath => _layout.Root;

    /// <summary>在资源管理器中打开某个文件夹（文件夹树右键）。</summary>
    public void RevealFolder(string folderKey)
    {
        try
        {
            var path = _layout.FolderAbsolutePath(folderKey);
            Directory.CreateDirectory(path);
            _shell.RevealInExplorer(path);
            StatusText = $"已在资源管理器中打开：{path}";
        }
        catch (Exception ex)
        {
            StatusText = $"打开文件夹失败：{ex.Message}";
        }
    }

    // ══════════════════════════ 状态 ══════════════════════════

    [ObservableProperty] private FolderNodeViewModel? _selectedFolder;
    [ObservableProperty] private string _statusText = "正在启动…";
    [ObservableProperty] private string _folderTitle = "";
    [ObservableProperty] private string _statsText = "";
    [ObservableProperty] private string _libraryRootText = "";
    [ObservableProperty] private string _encoderText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEnriching;
    [ObservableProperty] private bool _hasAssets;
    [ObservableProperty] private double _thumbnailSize = 200;
    [ObservableProperty] private bool _isLowOnSpace;
    [ObservableProperty] private bool _isDragOver;

    /// <summary>是否正在处理导入任务（转码/转换）。</summary>
    [ObservableProperty] private bool _isJobRunning;

    /// <summary>任务条文本，如 <c>转码中 3/47</c>。</summary>
    [ObservableProperty] private string _jobStatusText = "";

    /// <summary>任务条进度，0.0 ~ 1.0。</summary>
    [ObservableProperty] private double _jobProgressFraction;

    /// <summary>选中的素材数量（选择工具条）。</summary>
    [ObservableProperty] private int _selectionCount;

    /// <summary>是否有选中素材（选择工具条可见性）。</summary>
    public bool HasSelection => SelectionCount > 0;

    partial void OnSelectionCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));

    /// <summary>搜索框文本（按文件名过滤当前目录）。</summary>
    [ObservableProperty] private string _searchText = "";

    /// <summary>排序方式：0 名称 / 1 大小 / 2 时长。</summary>
    [ObservableProperty] private int _sortMode;

    private CancellationTokenSource? _searchDebounceCts;

    partial void OnSearchTextChanged(string value)
    {
        // 防抖 250ms：每敲一个字母重新查库没必要
        _searchDebounceCts?.Cancel();
        _searchDebounceCts = new CancellationTokenSource();
        var ct = _searchDebounceCts.Token;
        var folderKey = SelectedFolder?.FolderKey;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, ct);
                if (ct.IsCancellationRequested || folderKey is null) return;

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => LoadAssetsAsync(folderKey));
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);
    }

    partial void OnSortModeChanged(int value)
    {
        if (SelectedFolder is { } folder)
            _ = LoadAssetsAsync(folder.FolderKey);
    }

    public bool ShowEnrichmentHint => IsEnriching;

    partial void OnIsEnrichingChanged(bool value) => OnPropertyChanged(nameof(ShowEnrichmentHint));

    partial void OnThumbnailSizeChanged(double value)
    {
        _settings.ThumbnailSize = value;
        _settings.Save();
    }

    // ══════════════════════════ 启动 ══════════════════════════

    public async Task InitializeAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            IsBusy = true;

            // ── 1. 文件系统 ↔ 数据库对账 ───────────────────────
            StatusText = "正在扫描素材库…";
            var syncProgress = new Progress<SyncProgress>(p =>
                StatusText = $"正在扫描 {p.Processed}/{p.Total}：{p.CurrentItem}");

            var syncResult = await _syncService.SyncAsync(syncProgress, ct).ConfigureAwait(true);
            DiagnosticsLog.Mark($"扫描完成 → {syncResult}");

            ct.ThrowIfCancellationRequested();

            // ── 2. 构建文件夹树 ────────────────────────────────
            StatusText = "正在整理目录…";
            await ReloadFolderTreeAsync(ct).ConfigureAwait(true);
            DiagnosticsLog.Mark($"目录树构建完成 → {FolderTree.Count} 个顶层目录");

            // ── 3. 选中第一个有内容的目录 ──────────────────────
            SelectedFolder = FindFirstWithAssets() ?? FolderTree.FirstOrDefault();

            if (SelectedFolder is null)
            {
                FolderTitle = "空素材库";
                StatusText = "素材库是空的 —— 把文件或文件夹拖进来即可开始";
                DiagnosticsLog.Mark("库为空");
            }

            await UpdateStatsAsync(ct).ConfigureAwait(true);

            // ── 4. 后台补全缩略图与元数据（不阻塞界面）─────────
            _ = RunEnrichmentAsync(ct);

            // ── 5. 续跑上次没做完的导入任务（崩溃恢复）─────────
            _ = RunJobsAsync();
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            StatusText = $"启动失败：{ex.Message}";
            DiagnosticsLog.Error("启动", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private FolderNodeViewModel? FindFirstWithAssets()
    {
        FolderNodeViewModel? best = null;
        var bestDepth = int.MaxValue;

        void Walk(FolderNodeViewModel node, int depth)
        {
            if (node.OwnAssetCount > 0 && depth < bestDepth)
            {
                best = node;
                bestDepth = depth;
            }
            foreach (var child in node.Children) Walk(child, depth + 1);
        }

        foreach (var root in FolderTree) Walk(root, 0);
        return best;
    }

    // ══════════════════════════ 导入 ══════════════════════════

    /// <summary>
    /// 导入拖入的文件/文件夹（设计文档 §5.1）。
    /// </summary>
    public async Task ImportAsync(IReadOnlyList<string> droppedPaths)
    {
        if (droppedPaths.Count == 0) return;
        if (IsBusy) return;

        var ct = _cts?.Token ?? CancellationToken.None;

        try
        {
            IsBusy = true;

            var progress = new Progress<ImportProgress>(p =>
                StatusText = p.Total > 0
                    ? $"{p.Stage} {p.Processed}/{p.Total}：{p.CurrentItem}"
                    : p.Stage);

            var result = await _importService.ImportAsync(droppedPaths, progress, ct).ConfigureAwait(true);
            DiagnosticsLog.Mark($"导入计划完成 → {result}");

            if (result.Enqueued == 0)
            {
                StatusText = result.Skipped > 0
                    ? $"没有新素材：{DescribeSkips(result.Plan.SkipSummary)}"
                    : "拖入的内容里没有可识别的素材";
                return;
            }

            StatusText = $"已入库 {result.Enqueued} 项，开始处理…";

            // 刷新树与当前目录，让占位卡片立刻出现
            await ReloadFolderTreeAsync(ct).ConfigureAwait(true);

            var target = FolderNodeViewModel.Find(FolderTree, result.PrimaryFolderKey ?? "")
                         ?? FindFirstWithAssets()
                         ?? FolderTree.FirstOrDefault();

            if (target is not null) SelectedFolder = target;
            else if (SelectedFolder is not null) await LoadAssetsAsync(SelectedFolder.FolderKey);

            // 后台跑转码/转换任务
            _ = RunJobsAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"导入失败：{ex.Message}";
            DiagnosticsLog.Error("导入", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string DescribeSkips(IReadOnlyDictionary<string, int> summary)
        => summary.Count == 0
            ? "无"
            : string.Join("，", summary.Select(kv => $"{kv.Key} {kv.Value} 个"));

    /// <summary>
    /// 处理任务队列（转码 H.265 / 转换 PNG / 复制音频）。
    /// 幂等：没有排队任务时直接返回。
    /// </summary>
    public async Task RunJobsAsync()
    {
        if (_jobsRunning) return;

        var ct = _cts?.Token ?? CancellationToken.None;
        _jobsRunning = true;

        try
        {
            var counts = await _jobs.GetCountsAsync(ct).ConfigureAwait(true);
            if (counts.Queued == 0) return;

            var total = counts.Queued;
            var done = 0;

            IsJobRunning = true;
            JobProgressFraction = 0;
            JobStatusText = $"处理中 0/{total}";

            // 界面更新放主线程；写库由执行器自己节流
            var progress = new Progress<JobProgress>(p =>
            {
                if (_itemsById.TryGetValue(p.AssetId, out var item))
                    item.ApplyJobProgress(p);

                if (p.Status is JobStatus.Done or JobStatus.Failed)
                {
                    done++;
                    JobProgressFraction = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0;
                    JobStatusText = $"处理中 {done}/{total}";
                }
                else if (p.Status == JobStatus.Running)
                {
                    JobStatusText = $"处理中 {done}/{total} · {p.FileName}";
                }
            });

            var stats = await _jobRunner.RunAsync(progress, ct).ConfigureAwait(true);
            DiagnosticsLog.Mark($"任务队列完成 → {stats}（编码器：{_transcoder.ActiveEncoder}）");

            JobStatusText = stats.Failed > 0
                ? $"处理完成：成功 {stats.Succeeded}，失败 {stats.Failed}"
                : $"处理完成：{stats.Succeeded} 项";

            StatusText = $"已完成 {stats.Succeeded} 项" +
                         (stats.Failed > 0 ? $"，{stats.Failed} 项失败（可右键重试）" : "");

            // 拿回最终元数据（时长/分辨率/大小），并刷新缩略图
            await RefreshCurrentFolderAsync(ct).ConfigureAwait(true);
            await UpdateStatsAsync(ct).ConfigureAwait(true);
            await ReloadFolderTreeAsync(ct).ConfigureAwait(true);

            // 新落盘的文件还没有缩略图，交给补全服务
            _ = RunEnrichmentAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"任务执行失败：{ex.Message}";
            DiagnosticsLog.Error("任务执行", ex);
        }
        finally
        {
            IsJobRunning = false;
            _jobsRunning = false;
        }
    }

    private async Task RefreshCurrentFolderAsync(CancellationToken ct)
    {
        if (SelectedFolder is null) return;

        try
        {
            var folderKey = SelectedFolder.FolderKey;
            var list = await _assets.GetByFolderAsync(folderKey, ct).ConfigureAwait(true);

            // 先按 Id 更新已有卡片（保留缩略图和解码缓存）
            var byId = list.ToDictionary(a => a.Id);
            foreach (var (id, item) in _itemsById)
            {
                if (byId.TryGetValue(id, out var fresh)) item.RefreshFromAsset(fresh);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Refresh] {ex.Message}");
        }
    }

    // ══════════════════════════ 目录树 ══════════════════════════

    private async Task ReloadFolderTreeAsync(CancellationToken ct)
    {
        var summaries = await _assets.GetFolderSummariesAsync(ct).ConfigureAwait(true);
        var selectedKey = SelectedFolder?.FolderKey;

        FolderTree.Clear();
        foreach (var node in FolderNodeViewModel.BuildTree(summaries))
            FolderTree.Add(node);

        // 磁盘上真实存在、但还没有素材的目录也要显示 —— 右键新建的空文件夹立即可见
        try
        {
            var dirs = Directory.GetDirectories(_layout.Root, "*", System.IO.SearchOption.AllDirectories)
                .Where(d => (new DirectoryInfo(d).Attributes & FileAttributes.Hidden) == 0)
                .Select(d => _layout.ToRelative(d))
                .Where(r => r is not null)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var dir in dirs)
            {
                if (dir is not null)
                    FolderNodeViewModel.EnsureFolderNode(FolderTree, dir);
            }
        }
        catch (Exception)
        {
            // 目录枚举失败不阻止界面
        }

        if (selectedKey is not null)
            SelectedFolder = FolderNodeViewModel.Find(FolderTree, selectedKey);

        if (FolderTree.Count == 1)
            FolderTree[0].IsExpanded = true;
    }

    partial void OnSelectedFolderChanged(FolderNodeViewModel? value)
    {
        foreach (var node in EnumerateAll(FolderTree))
            node.IsSelected = ReferenceEquals(node, value);

        if (value is null)
        {
            // 库被清空（所有文件夹删除）后，标题不能残留最后一个文件夹的名字
            FolderTitle = "素材库";
            Assets.Clear();
            _itemsById.Clear();
            HasAssets = false;
            return;
        }

        FolderTitle = string.IsNullOrEmpty(value.FolderKey) ? "库根目录" : value.FolderKey;
        _ = LoadAssetsAsync(value.FolderKey);
    }

    private static IEnumerable<FolderNodeViewModel> EnumerateAll(IEnumerable<FolderNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateAll(node.Children)) yield return child;
        }
    }

    // ══════════════════════════ 素材加载 ══════════════════════════

    private async Task LoadAssetsAsync(string folderKey)
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        try
        {
            var list = await _assets.GetByFolderAsync(folderKey, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            if (SelectedFolder?.FolderKey != folderKey) return;

            // 搜索过滤（文件名包含，忽略大小写）
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                list = list
                    .Where(a => a.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // 排序
            list = SortMode switch
            {
                1 => list.OrderByDescending(a => a.SizeBytes).ToList(),          // 大小
                2 => list.OrderByDescending(a => a.DurationMs ?? 0).ToList(),   // 时长
                _ => list.OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase).ToList(), // 名称
            };

            Assets.Clear();
            _itemsById.Clear();

            foreach (var asset in list)
            {
                var item = new AssetItemViewModel(asset, _layout, _thumbCache);
                Assets.Add(item);
                _itemsById[asset.Id] = item;
            }

            HasAssets = Assets.Count > 0;
            var label = string.IsNullOrEmpty(folderKey) ? "库根目录" : folderKey;
            StatusText = HasAssets
                ? $"{label} · {Assets.Count} 个素材"
                : $"{label} · 此目录暂无素材";

            DiagnosticsLog.Mark($"目录「{folderKey}」加载完成 → {Assets.Count} 个素材（首屏可用）");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
    }

    // ══════════════════════════ 后台补全 ══════════════════════════

    private async Task RunEnrichmentAsync(CancellationToken ct)
    {
        try
        {
            var pending = await _enrichment.GetPendingCountAsync(ct).ConfigureAwait(true);
            if (pending == 0) return;

            IsEnriching = true;

            var done = 0;
            var progress = new Progress<EnrichProgress>(p =>
            {
                done++;

                if (p.Succeeded && p.ThumbName is not null &&
                    _itemsById.TryGetValue(p.AssetId, out var item))
                {
                    item.Asset.ThumbPath = p.ThumbName;
                    item.ReloadThumbnail();
                }

                if (done % 5 == 0) StatusText = $"正在生成缩略图… {done}/{pending}";
            });

            var stats = await _enrichment.EnrichAllAsync(progress, ct).ConfigureAwait(true);
            DiagnosticsLog.Mark($"缩略图补全 → {stats}");

            StatusText = $"缩略图生成完成（成功 {stats.Succeeded} / 失败 {stats.Failed}）";
            await UpdateStatsAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Enrich] 失败: {ex}");
        }
        finally
        {
            IsEnriching = false;
        }
    }

    // ══════════════════════════ 统计 ══════════════════════════

    private async Task UpdateStatsAsync(CancellationToken ct)
    {
        try
        {
            var stats = await _assets.GetStatsAsync(ct).ConfigureAwait(true);
            var free = _layout.GetFreeSpaceBytes();

            StatsText = string.Join(" · ",
                $"视频 {AssetItemViewModel.FormatBytes(stats.VideoBytes)}",
                $"照片 {AssetItemViewModel.FormatBytes(stats.PhotoBytes)}",
                $"音频 {AssetItemViewModel.FormatBytes(stats.AudioBytes)}",
                $"剩余 {AssetItemViewModel.FormatBytes(free)}");

            IsLowOnSpace = free > 0 && free < _settings.SpaceWarningGb * 1024L * 1024 * 1024;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Stats] 失败: {ex.Message}");
        }
    }

    // ══════════════════════════ 命令 ══════════════════════════

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            StatusText = "正在重新扫描…";

            var syncProgress = new Progress<SyncProgress>(p =>
                StatusText = $"正在扫描 {p.Processed}/{p.Total}：{p.CurrentItem}");

            var result = await _syncService.SyncAsync(syncProgress, _cts?.Token ?? default)
                .ConfigureAwait(true);

            await ReloadFolderTreeAsync(_cts?.Token ?? default).ConfigureAwait(true);

            if (SelectedFolder is not null)
                await LoadAssetsAsync(SelectedFolder.FolderKey).ConfigureAwait(true);

            await UpdateStatsAsync(_cts?.Token ?? default).ConfigureAwait(true);

            StatusText = result.Added > 0 || result.Missing > 0
                ? $"扫描完成：新增 {result.Added}，丢失 {result.Missing}"
                : "扫描完成：没有变化";

            _ = RunEnrichmentAsync(_cts?.Token ?? default);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>双击卡片：用系统默认程序打开库内文件（设计文档附录 B 问题 4）。</summary>
    [RelayCommand]
    private void OpenAsset(AssetItemViewModel? item)
    {
        if (item is null) return;

        try
        {
            if (_shell.OpenFile(item.AbsolutePath)) return;

            // 系统没有该格式的关联程序（FLAC 等常见）→ 弹"选择打开方式"
            _shell.OpenWithDialog(item.AbsolutePath);
            StatusText = $"系统没有打开 {item.Asset.Extension} 的默认程序 —— 请选择打开方式";
        }
        catch (Exception ex)
        {
            StatusText = $"打开失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void RevealAsset(AssetItemViewModel? item)
    {
        if (item is null) return;

        try
        {
            _shell.RevealInExplorer(item.AbsolutePath);
        }
        catch (Exception ex)
        {
            StatusText = $"定位失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenCurrentFolder()
    {
        try
        {
            var path = _layout.FolderAbsolutePath(SelectedFolder?.FolderKey ?? "");
            Directory.CreateDirectory(path);
            // 直接定位目录本身（带 "\." 尾巴时 explorer /select 可能不响应 —— 用户实测"点不了"）
            _shell.RevealInExplorer(path);
            StatusText = $"已在资源管理器中打开：{path}";
        }
        catch (Exception ex)
        {
            StatusText = $"打开目录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyPath(AssetItemViewModel? item)
    {
        if (item is null) return;

        try
        {
            System.Windows.Clipboard.SetText(item.AbsolutePath);
            StatusText = "路径已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>为当前目录重新生成缩略图。</summary>
    [RelayCommand]
    private async Task RegenerateThumbnailsAsync()
    {
        if (Assets.Count == 0) return;

        try
        {
            var ids = Assets.Select(a => a.Id).ToList();
            await _assets.ResetEnrichmentAsync(ids).ConfigureAwait(true);

            foreach (var item in Assets)
            {
                item.Asset.ThumbPath = null;
                item.Asset.EnrichAttempts = 0;
                item.Thumbnail = null;
            }

            _thumbCache.Clear();
            _ = RunEnrichmentAsync(_cts?.Token ?? default);
        }
        catch (Exception ex)
        {
            StatusText = $"重建缩略图失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 诊断/修复：全库重新补全缩略图（同步等待完成）。
    /// 返回（素材总数, 修复前缺图数, 成功, 失败）。
    /// </summary>
    public async Task<(int Total, int MissingBefore, int Succeeded, int Failed)> RepairThumbnailsAsync()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        var all = await _assets.GetAllAsync(ct).ConfigureAwait(true);
        var missing = all.Count(a => string.IsNullOrEmpty(a.ThumbPath));

        // 重置全部补全状态，强制重生成
        await _assets.ResetEnrichmentAsync(all.Select(a => a.Id).ToList(), ct).ConfigureAwait(true);

        foreach (var item in _itemsById.Values)
        {
            item.Asset.ThumbPath = null;
            item.Asset.EnrichAttempts = 0;
            item.Thumbnail = null;
        }

        _thumbCache.Clear();

        IsEnriching = true;
        try
        {
            var stats = await _enrichment.EnrichAllAsync(null, ct).ConfigureAwait(true);
            DiagnosticsLog.Mark($"缩略图补全 → {stats}");
            return (all.Count, missing, stats.Succeeded, stats.Failed);
        }
        finally
        {
            IsEnriching = false;
        }
    }

    /// <summary>缩略图缓存（诊断验证"卡片缩略图真实可加载"用）。</summary>
    public ThumbnailImageCache ThumbnailCache => _thumbCache;

    /// <summary>ThumbPath（相对文件名）→ 绝对路径（诊断用）。</summary>
    public string ThumbAbsolutePathFor(string thumbPath) => _layout.ThumbAbsolutePath(thumbPath);

    /// <summary>从数据库取任意一个带缩略图的素材的 ThumbPath（诊断验证用）。</summary>
    public async Task<string?> GetAnyThumbPathAsync()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        var all = await _assets.GetAllAsync(ct).ConfigureAwait(true);
        return all.FirstOrDefault(a => !string.IsNullOrEmpty(a.ThumbPath))?.ThumbPath;
    }

    // ══════════════════════════ 多选与批量操作（阶段 4）══════════════════════════

    /// <summary>
    /// 新建文件夹（右键菜单）：创建物理目录 + 刷新树并选中。
    /// 返回（是否成功, 消息）。
    /// </summary>
    public async Task<(bool Ok, string Message)> CreateFolderAsync(string? parentKey, string name)
    {
        var clean = NamingEngine.SanitizeFolderName(name);
        if (string.IsNullOrWhiteSpace(clean))
            return (false, "文件夹名无效（为空或全是非法字符）");

        var key = string.IsNullOrEmpty(parentKey)
            ? clean
            : $"{parentKey}/{clean}";

        var path = _layout.FolderAbsolutePath(key);
        if (Directory.Exists(path))
            return (false, $"「{clean}」已经存在");

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            return (false, $"创建失败：{ex.Message}");
        }

        var ct = _cts?.Token ?? CancellationToken.None;
        await ReloadFolderTreeAsync(ct).ConfigureAwait(true);

        SelectedFolder = FolderNodeViewModel.Find(FolderTree, key);
        StatusText = $"已创建文件夹「{clean}」";
        return (true, clean);
    }

    /// <summary>
    /// 重命名文件夹：移动物理目录 + 把库内所有受影响素材的
    /// FolderKey/LibraryPath 前缀改写 + 重播种命名序号。
    /// </summary>
    public async Task<(bool Ok, string Message)> RenameFolderAsync(string folderKey, string newName)
    {
        if (string.IsNullOrEmpty(folderKey))
            return (false, "不能重命名库根目录");

        var clean = NamingEngine.SanitizeFolderName(newName);
        if (string.IsNullOrWhiteSpace(clean))
            return (false, "文件夹名无效（为空或全是非法字符）");

        var slash = folderKey.LastIndexOf(LibraryPathHelper.Separator);
        var parentKey = slash > 0 ? folderKey[..slash] : "";
        var newKey = string.IsNullOrEmpty(parentKey)
            ? clean
            : $"{parentKey}{LibraryPathHelper.Separator}{clean}";

        if (string.Equals(newKey, folderKey, StringComparison.OrdinalIgnoreCase))
            return (true, "名称未变化");

        var oldPath = _layout.FolderAbsolutePath(folderKey);
        var newPath = _layout.FolderAbsolutePath(newKey);

        if (!Directory.Exists(oldPath))
            return (false, "文件夹不存在（可能已被外部删除）");

        if (Directory.Exists(newPath))
            return (false, $"「{clean}」已经存在");

        try
        {
            Directory.Move(oldPath, newPath);
        }
        catch (Exception ex)
        {
            return (false, $"重命名失败：{ex.Message}");
        }

        var ct = _cts?.Token ?? CancellationToken.None;
        await _assets.RenameFolderAsync(folderKey, newKey, ct).ConfigureAwait(true);

        // 命名序号按文件夹记录 —— 文件夹改名后必须重播种，否则将来导入会重号
        await _counters.ReseedAllAsync(ct).ConfigureAwait(true);

        await ReloadFolderTreeAsync(ct).ConfigureAwait(true);
        SelectedFolder = FolderNodeViewModel.Find(FolderTree, newKey);

        StatusText = $"文件夹已重命名为「{clean}」";
        return (true, clean);
    }

    /// <summary>删除文件夹：库内文件移入回收站（可还原）+ 记录删除 + 目录清理。</summary>
    public async Task<(bool Ok, string Message)> DeleteFolderAsync(string folderKey)
    {
        if (string.IsNullOrEmpty(folderKey))
            return (false, "不能删除库根目录");

        var ct = _cts?.Token ?? CancellationToken.None;
        var ids = await _assets.GetIdsByFolderPrefixAsync(folderKey, ct).ConfigureAwait(true);

        var deleted = 0;
        var failed = 0;

        foreach (var id in ids)
        {
            try
            {
                var asset = await _assets.GetByIdAsync(id, ct).ConfigureAwait(true);
                if (asset is null) continue;

                var path = _layout.ToAbsolute(asset.LibraryPath);
                if (File.Exists(path))
                    FileSystem.DeleteFile(
                        path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

                await _assets.DeleteAsync(id, ct).ConfigureAwait(true);
                await _queue.DeleteForAssetsAsync([id], ct).ConfigureAwait(true);

                _itemsById.Remove(id);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                DiagnosticsLog.Error($"删除文件夹素材 {id}", ex);
            }
        }

        // 删除已清空的目录树（残留的空子目录一并清掉）
        try
        {
            var dir = _layout.FolderAbsolutePath(folderKey);
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch (Exception)
        {
            // 目录删不干净（有外部文件）就留着
        }

        await CleanOrphanThumbnailsAsync(ct).ConfigureAwait(true);
        await ReloadFolderTreeAsync(ct).ConfigureAwait(true);

        // 被删的文件夹正是当前选中 → 回到库根
        if (SelectedFolder is null || SelectedFolder.FolderKey == folderKey ||
            SelectedFolder.FolderKey.StartsWith(folderKey + "/", StringComparison.OrdinalIgnoreCase))
        {
            SelectedFolder = FolderNodeViewModel.Find(FolderTree, "")
                            ?? FolderTree.FirstOrDefault();
        }
        else
        {
            SelectedFolder = FolderNodeViewModel.Find(FolderTree, SelectedFolder.FolderKey);
        }

        await UpdateStatsAsync(ct).ConfigureAwait(true);

        StatusText = failed > 0
            ? $"已删除文件夹（{deleted} 个素材入回收站，{failed} 个失败）"
            : $"已删除文件夹（{deleted} 个素材在回收站里，可还原）";
        return (true, StatusText);
    }

    /// <summary>把 ListBox 的选中项同步进 SelectedAssets（由 SelectionChanged 驱动）。</summary>
    public void SyncSelection(IEnumerable<AssetItemViewModel> items)
    {
        var set = items.ToHashSet();

        for (var i = SelectedAssets.Count - 1; i >= 0; i--)
        {
            if (!set.Contains(SelectedAssets[i])) SelectedAssets.RemoveAt(i);
        }

        foreach (var item in set)
        {
            if (!SelectedAssets.Contains(item)) SelectedAssets.Add(item);
        }

        SelectionCount = SelectedAssets.Count;
    }

    public void ClearSelection() => SyncSelection([]);

    /// <summary>批量重命名：模板 + 连续序号（设计文档阶段 4）。</summary>
    [RelayCommand]
    private async Task RenameSelectedAsync()
    {
        var items = SelectedAssets.OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        if (items.Count == 0) return;

        var dialog = new RenameDialogWindow(
            NamingEngine.InnermostFolderName(items[0].Asset.FolderKey))
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Pattern)) return;

        await ApplyRenameAsync(items, dialog.Pattern).ConfigureAwait(true);
    }

    /// <summary>
    /// 按模板对一组素材执行重命名（对话框之后的核心逻辑，诊断模式直接调用）。
    /// </summary>
    public async Task ApplyRenameAsync(IReadOnlyList<AssetItemViewModel> items, string pattern)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        var renamed = 0;
        var failed = 0;

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var item = items[i];
                var extension = Path.GetExtension(item.FileName);

                var baseName = pattern
                    .Replace("{序号:D3}", (i + 1).ToString("D3"))
                    .Replace("{序号}", (i + 1).ToString())
                    .Replace("{原名}", Path.GetFileNameWithoutExtension(item.FileName))
                    .Replace("{原文件夹}", NamingEngine.InnermostFolderName(item.Asset.FolderKey));

                var candidate = NamingEngine.SanitizeFileName(baseName) + extension;

                // 与库内已有文件撞名时自动加后缀（_2、_3…）
                var finalName = candidate;
                var counter = 2;
                while (File.Exists(Path.Combine(_layout.FolderAbsolutePath(item.Asset.FolderKey), finalName)))
                {
                    finalName = $"{Path.GetFileNameWithoutExtension(candidate)}_{counter}{extension}";
                    counter++;
                }

                if (string.Equals(finalName, item.FileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var oldPath = _layout.ToAbsolute(item.Asset.LibraryPath);
                var newRelative = Path.Combine(
                    item.Asset.FolderKey, finalName).Replace('\\', '/');
                var newPath = _layout.ToAbsolute(newRelative);

                // 文件可能正被预览/缩略图补全短暂占用（VLC/ffmpeg 句柄），
                // 瞬时锁文件重试几次而不是直接失败
                Exception? lastMoveError = null;
                var moved = false;

                for (var attempt = 0; attempt < 5 && !moved; attempt++)
                {
                    try
                    {
                        File.Move(oldPath, newPath);
                        moved = true;
                    }
                    catch (IOException ex) when (attempt < 4)
                    {
                        lastMoveError = ex;
                        await Task.Delay(250, ct).ConfigureAwait(true);
                    }
                }

                if (!moved)
                    throw lastMoveError ?? new IOException("文件被占用，重命名失败");

                await _assets.RenameAsync(item.Id, finalName, newRelative, ct).ConfigureAwait(true);

                item.Asset.FileName = finalName;
                item.Asset.LibraryPath = newRelative;
                renamed++;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            failed++;
            DiagnosticsLog.Error("批量重命名", ex);
        }

        if (renamed > 0 && SelectedFolder is not null)
        {
            await ReloadFolderTreeAsync(ct).ConfigureAwait(true);
            await LoadAssetsAsync(SelectedFolder.FolderKey).ConfigureAwait(true);
        }

        StatusText = failed > 0
            ? $"重命名完成：成功 {renamed}，失败 {failed}（见日志）"
            : $"已重命名 {renamed} 个素材";
    }

    /// <summary>批量重新转码：把选中素材重新排队处理（覆盖库内文件）。</summary>
    [RelayCommand]
    private async Task RetranscodeSelectedAsync()
    {
        var items = SelectedAssets.ToList();
        if (items.Count == 0) return;

        var ct = _cts?.Token ?? CancellationToken.None;
        var enqueued = 0;
        var skipped = 0;

        try
        {
            foreach (var item in items)
            {
                // 已在队列里的不重复排队
                var existing = await _jobs.GetByAssetAsync(item.Id, ct).ConfigureAwait(true);
                if (existing.Any(j => j.Status is JobStatus.Queued or JobStatus.Running or JobStatus.Paused))
                {
                    skipped++;
                    continue;
                }

                await _assets.UpdateStatusAsync(item.Id, AssetStatus.Pending, null, ct)
                    .ConfigureAwait(true);
                await _queue.EnqueueAsync(item.Id, ImportService.JobTypeFor(item.Kind), priority: 1, ct)
                    .ConfigureAwait(true);
                enqueued++;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"重新转码失败：{ex.Message}";
            DiagnosticsLog.Error("重新转码", ex);
            return;
        }

        StatusText = skipped > 0
            ? $"已重新排队 {enqueued} 项，跳过已在队列中的 {skipped} 项"
            : $"已重新排队 {enqueued} 项，开始处理…";

        if (enqueued > 0) _ = RunJobsAsync();
    }

    /// <summary>删除到回收站：文件进回收站可还原，库内记录删除。</summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var items = SelectedAssets.ToList();
        if (items.Count == 0) return;

        var choice = System.Windows.MessageBox.Show(
            $"把 {items.Count} 个素材移入回收站？\n\n" +
            "文件会进入回收站（可还原），但素材库里的记录会被删除。\n\n" +
            "提示：如果这些文件是转码生成的，重新转码即可再生。",
            "素材库 — 删除", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (choice != System.Windows.MessageBoxResult.Yes) return;

        await DeleteAssetsCoreAsync(items).ConfigureAwait(true);
    }

    /// <summary>删除核心逻辑（无确认框；批量操作诊断直接调用）。</summary>
    public async Task DeleteAssetsCoreAsync(IReadOnlyList<AssetItemViewModel> items)
    {
        if (items.Count == 0) return;

        var ct = _cts?.Token ?? CancellationToken.None;
        var deleted = 0;
        var failed = 0;

        foreach (var item in items)
        {
            try
            {
                var path = item.AbsolutePath;

                if (File.Exists(path))
                    FileSystem.DeleteFile(
                        path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

                await _assets.DeleteAsync(item.Id, ct).ConfigureAwait(true);
                await _queue.DeleteForAssetsAsync([item.Id], ct).ConfigureAwait(true);

                Assets.Remove(item);
                _itemsById.Remove(item.Id);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                DiagnosticsLog.Error($"删除素材 {item.FileName}", ex);
            }
        }

        await CleanOrphanThumbnailsAsync(ct).ConfigureAwait(true);
        ClearSelection();

        if (SelectedFolder is not null)
            await ReloadFolderTreeAsync(ct).ConfigureAwait(true);

        HasAssets = Assets.Count > 0;
        StatusText = failed > 0
            ? $"已删除 {deleted} 项，{failed} 项失败（见日志）"
            : $"已删除 {deleted} 项 —— 文件在回收站里，可以还原";
        await UpdateStatsAsync(ct).ConfigureAwait(true);
    }

    /// <summary>删除后清理不再被任何素材引用的缩略图文件。</summary>
    private async Task CleanOrphanThumbnailsAsync(CancellationToken ct)
    {
        try
        {
            var hashes = await _assets.GetAllHashesAsync(ct).ConfigureAwait(true);

            var thumbDir = _layout.ThumbDir;
            if (!Directory.Exists(thumbDir)) return;

            foreach (var file in Directory.GetFiles(thumbDir, "*.jpg"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!hashes.Contains(name))
                {
                    try { File.Delete(file); }
                    catch (Exception) { }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("清理缩略图", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch (Exception)
        {
            // 忽略
        }

        GC.SuppressFinalize(this);
    }
}
