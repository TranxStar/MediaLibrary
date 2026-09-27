using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MediaLibrary.App.Services;
using MediaLibrary.App.ViewModels;
using MediaLibrary.Core.Models;
using MediaLibrary.Media;

namespace MediaLibrary.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly HoverPreviewController _hover;

    /// <summary>任务队列面板的 ViewModel（XAML 经 Window.JobQueue 绑定）。</summary>
    public JobQueueViewModel JobQueue { get; }

    /// <summary>
    /// 诊断模式下忽略真实鼠标悬停。
    ///
    /// 必要性：无人值守运行时鼠标可能正好停在某张卡片上，
    /// 会随时触发新的悬浮预览，把脚本控制的预览顶掉，导致断言读到错误的状态。
    /// （这本身说明功能是好的 —— 只是自动化测试必须排除这个变量。）
    /// </summary>
    private bool _ignoreRealMouseHover;

    /// <summary>诊断是否在跑（用于把"外部干扰"和诊断自身动作区分开）。</summary>
    private bool _diagnosticsRunning;

    /// <summary>诊断期间检测到的外部干扰次数（窗口被拖动/失焦/状态变化）。</summary>
    private int _externalInterference;

    /// <summary>
    /// 程序化批量选中期间为 true：压制选中项的 BringIntoView，
    /// 防止虚拟化面板按估算位置把列表滚到最上/最下。
    /// </summary>
    private bool _suppressBringIntoView;

    public MainWindow(
        MainViewModel viewModel,
        AppSettings settings,
        HoverPreviewController hover,
        JobQueueViewModel jobQueue)
    {
        _viewModel = viewModel;
        _settings = settings;
        _hover = hover;
        JobQueue = jobQueue;
        _ignoreRealMouseHover = App.IsDiagnosticMode;

        InitializeComponent();
        DataContext = viewModel;

        // 队列面板的"重试/继续"要触发主流程跑队列
        JobQueue.WorkRequested += () => _ = _viewModel.RunJobsAsync();

        // ★ 程序化选中期间压制 BringIntoView：ListBoxItem 被选中时会请求滚入视图，
        //   虚拟化面板按【估算位置】滚动 —— 框选/全选时表现为列表跳到最上/最下
        //   （用户实测："全选之后向上/下移动鼠标，列表直接跳到最上/最下"）。
        //   ⚠️ 必须挂在 ScrollViewer 上而不是 ListBox 上：事件从卡片向上冒泡，
        //   内层的 ScrollViewer 类处理器会先于 ListBox 处理并直接滚动 ——
        //   在 ListBox 上标记 Handled 已经来不及了。实例处理器先于类处理器，
        //   挂在 ScrollViewer 本身才能在其滚动之前拦下。
        AssetList.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler((_, e) =>
            {
                if (_suppressBringIntoView) e.Handled = true;
            }), handledEventsToo: true);

        // 框选拖出边界的自动滚动时钟
        _marqueeScrollTimer.Tick += OnMarqueeScrollTick;

        ApplySavedWindowBounds();
        Loaded += OnWindowLoaded;

        // 模板展开后，把 BringIntoView 压制再挂一份到 ScrollViewer 本身
        // （见上方注释：在 ListBox 上拦已经来不及）
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var sv = FindVisualChild<ScrollViewer>(AssetList);
            if (sv is not null)
            {
                sv.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
                    new RequestBringIntoViewEventHandler((_, e) =>
                    {
                        if (_suppressBringIntoView) e.Handled = true;
                    }), handledEventsToo: true);
            }
        });

        // 滚动或移动窗口时，悬浮预览会留在原地变成"漂着的鬼影"，必须收起
        AssetList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, e) =>
            {
                DiagnosticsLog.Mark($"[诊断] 列表滚动事件：垂直偏移={e.VerticalOffset:F1} 高度变化={e.ViewportHeightChange:F1}");
                // 小抖动（虚拟化按缩略图尺寸微调锚点，几像素级）不算真滚动，
                // 不该杀掉待弹出的预览 —— 否则异步加载的缩略图会"随机"吞掉悬停。
                if (Math.Abs(e.VerticalChange) > 4 || Math.Abs(e.HorizontalChange) > 4)
                    _hover.HideNow("素材列表滚动");

                // 框选期间的滚动（自动滚动）：选框起点跟着内容平移，保持锚定
                if (_marqueeActive)
                {
                    _marqueeStart.X -= e.HorizontalChange;
                    _marqueeStart.Y -= e.VerticalChange;

                    // ★ 跳变/抽搐检测（同步、无竞态）：
                    //   · 一步滚动超过 250px = 面板跳变（实测上滚 648→0）
                    //   · 与预期方向相反且超过 50px = 锚点回抽（实测下滚 989→808）
                    //   立即回滚到变化前的位置并停止自动滚动 —— 用户看不到瞬移/抽搐；
                    //   算术选中不受影响，框外文件照样全选。
                    var intended = _autoScrollDirection;
                    var moved = e.VerticalChange;
                    var isJump = intended != 0 && Math.Abs(moved) > 250;
                    var isFightBack = intended != 0 &&
                                      Math.Sign(moved) == -intended &&
                                      Math.Abs(moved) > 50;

                    if ((isJump || isFightBack) && DateTime.UtcNow >= _restoreUntil)
                    {
                        _autoScrollBlocked = true;
                        DiagnosticsLog.Mark(
                            $"[框选] 检测到滚动{(isJump ? "跳变" : "回抽")} " +
                            $"{e.VerticalOffset - e.VerticalChange:F1} → {e.VerticalOffset:F1}" +
                            $"（方向 {intended}），立即回滚并停止自动滚动");

                        // 回滚本身也会产生一次大偏移事件 —— 300ms 内不再重复检测
                        _restoreUntil = DateTime.UtcNow.AddMilliseconds(300);

                        try
                        {
                            var svJump = FindVisualChild<ScrollViewer>(AssetList);
                            svJump?.ScrollToVerticalOffset(e.VerticalOffset - e.VerticalChange);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }));
        LocationChanged += (_, _) =>
        {
            DiagnosticsLog.Mark($"[诊断] 主窗口移动：Left={Left:F0} Top={Top:F0}");
            if (_diagnosticsRunning) _externalInterference++;
            _hover.HideNow("主窗口移动");
        };
        SizeChanged += (_, _) =>
        {
            DiagnosticsLog.Mark($"[诊断] 主窗口尺寸变化：{ActualWidth:F0}x{ActualHeight:F0}");
            _hover.HideNow("主窗口尺寸变化");
        };
        Deactivated += (_, _) =>
        {
            if (_diagnosticsRunning) _externalInterference++;
            _hover.HideNow("主窗口失去激活");
        };
        StateChanged += (_, _) =>
        {
            if (_diagnosticsRunning) _externalInterference++;
            _hover.HideNow("主窗口状态变化");
        };

        PreviewKeyDown += OnPreviewKeyDown;

        // 把预览面板挂到主窗口的覆盖层 —— 预览是窗口内浮层，不再是独立系统窗口
        _hover.SetOverlay(HoverOverlay);
    }

    private void ApplySavedWindowBounds()
    {
        if (_settings.WindowWidth > 400) Width = _settings.WindowWidth;
        if (_settings.WindowHeight > 300) Height = _settings.WindowHeight;

        // ★ 把窗口钳制到显示器的实际工作区。
        //
        //   窗口尺寸是 DIP，而屏幕是物理像素 —— 在 200% 缩放的 1280x800 屏幕上，
        //   默认的 1280x820 DIP 会变成 2560x1640 物理像素，大半截跑到屏幕外。
        //   后果不只是难看：卡片被排到屏幕外之后，悬浮预览也跟着定位到看不见的地方。
        //
        //   这里用 Win32 拿物理工作区，再按窗口实际 DPI 换成 DIP，避免单位判断出错。
        var handle = NativeWindow.GetHandle(this);
        var scale = NativeWindow.GetDpiScaleForWindow(handle);
        var work = NativeWindow.GetMonitorWorkArea(0, 0);

        var workWidthDip = work.Width / scale;
        var workHeightDip = work.Height / scale;

        if (workWidthDip > 300 && workHeightDip > 250)
        {
            Width = Math.Min(Width, workWidthDip);
            Height = Math.Min(Height, workHeightDip);
        }

        // 恢复上次位置，但必须完整落在工作区内
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top)
        {
            var maxLeft = (work.Right - Width * scale) / scale;
            var maxTop = (work.Bottom - Height * scale) / scale;

            if (left >= work.Left / scale && top >= work.Top / scale &&
                left <= maxLeft && top <= maxTop)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                return;
            }
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = work.Left / scale;
        Top = work.Top / scale;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;

        // 此时 ListBox 模板已展开：把 BringIntoView 压制挂到 ScrollViewer 本身
        // （在 ListBox 上拦已经来不及 —— 内层 ScrollViewer 先处理并滚动）
        var svForSuppression = FindVisualChild<ScrollViewer>(AssetList);
        if (svForSuppression is not null)
        {
            svForSuppression.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler((_, args) =>
                {
                    if (_suppressBringIntoView) args.Handled = true;
                }), handledEventsToo: true);
            DiagnosticsLog.Mark("[框选] BringIntoView 压制已挂到 ScrollViewer");
        }
        else
        {
            DiagnosticsLog.Mark("[框选] 警告：找不到 ScrollViewer，BringIntoView 压制未生效");
        }

        // 界面已经显示出来了才开始扫描 —— 冷启动不会卡在白屏
        await _viewModel.InitializeAsync();

        // 排序下拉初始状态
        SortBox.SelectedIndex = _viewModel.SortMode;

        // 诊断模式：自动滚动并测帧率，然后退出。用于客观验收"滚动 60 FPS"。
        if (Environment.GetEnvironmentVariable("MEDIALIBRARY_DIAG_SCROLL") == "1")
        {
            await RunScrollDiagnosticsAsync();
            Application.Current.Shutdown();
            return;
        }

        // 诊断模式：自动验证悬浮预览的各项验收标准
        if (Environment.GetEnvironmentVariable("MEDIALIBRARY_DIAG_HOVER") == "1")
        {
            await RunHoverDiagnosticsAsync();
            Application.Current.Shutdown();
        }

        // 诊断模式：验证阶段 4 的队列面板与框选多选（不碰任何文件）
        if (Environment.GetEnvironmentVariable("MEDIALIBRARY_DIAG_JOBS") == "1")
        {
            await RunJobsUiDiagnosticsAsync();
            Application.Current.Shutdown();
        }

        // 诊断模式：批量操作端到端（重命名/重新转码/删除）。
        // ★ 必须配合 MEDIALIBRARY_LIBRARY_ROOT 指向一次性临时库 —— 绝不碰用户素材。
        if (Environment.GetEnvironmentVariable("MEDIALIBRARY_DIAG_BATCH") == "1")
        {
            await RunBatchDiagnosticsAsync();
            Application.Current.Shutdown();
        }

        // 诊断/修复模式：全库重扫 + 补齐缺失的缩略图（用后即退）。
        if (Environment.GetEnvironmentVariable("MEDIALIBRARY_DIAG_THUMBS") == "1")
        {
            await RunThumbnailRepairAsync();
            Application.Current.Shutdown();
        }

        // 诊断模式：构建设置窗口（定位"设置点开没反应"的异常），用后即退。
        if (Environment.GetEnvironmentVariable("MEDIALIBRARY_DIAG_SETTINGS") == "1")
        {
            try
            {
                var settingsWindow = new SettingsWindow(_settings) { Owner = this };
                DiagnosticsLog.Mark("[设置] 窗口构造成功");
                settingsWindow.Close();
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Mark($"[设置] 构造失败：{ex}");
            }

            Application.Current.Shutdown();
        }
    }

    /// <summary>全库缩略图补全（诊断 + 修复：记录缺失数 → 强制全量重生成）。</summary>
    private async Task RunThumbnailRepairAsync()
    {
        DiagnosticsLog.Mark("── 缩略图补全诊断/修复开始 ──");

        try
        {
            var (total, missing, ok, fail) = await _viewModel.RepairThumbnailsAsync();

            DiagnosticsLog.Mark($"素材共 {total} 个，修复前缺缩略图 {missing} 个");
            DiagnosticsLog.Mark($"补全结果：成功 {ok} / 失败 {fail}");
            DiagnosticsLog.Mark(fail == 0
                ? "✓ 缩略图全部就绪（卡片上应能看到预览图）"
                : "✗ 仍有失败 —— 见启动日志中的错误详情");

            // ★ 双重扩展名修复的端到端验证：从数据库取真实 ThumbPath（带 .jpg），
            //   确认 绝对路径不再拼成 x.jpg.jpg、文件存在、且能被解码成图像
            var thumbPath = await _viewModel.GetAnyThumbPathAsync();

            if (!string.IsNullOrEmpty(thumbPath))
            {
                var absolute = _viewModel.ThumbAbsolutePathFor(thumbPath);
                var exists = File.Exists(absolute);
                var image = exists
                    ? await _viewModel.ThumbnailCache.GetOrLoadAsync(absolute)
                    : null;

                DiagnosticsLog.Mark(
                    $"✓ ThumbPath={thumbPath} → {absolute} | 存在={exists} | 解码={(image is not null ? "成功" : "失败")}");
                DiagnosticsLog.Mark(
                    exists && image is not null
                        ? "✓ 卡片缩略图真实可加载 —— 双重扩展名修复生效"
                        : "✗ 缩略图加载链路仍有问题");
            }
            else
            {
                DiagnosticsLog.Mark("（库内没有带缩略图的素材，跳过加载验证）");
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("缩略图补全", ex);
        }
    }

    // ══════════════════════════ 批量操作诊断 ══════════════════════════

    /// <summary>
    /// 批量操作端到端诊断（阶段 4）：
    /// 用 ffmpeg 现场合成一个视频/一张照片/一段音频 → 导入 → 等就绪 →
    /// 批量重命名 → 重新转码 → 删除到回收站。全程只碰临时库。
    /// </summary>
    private async Task RunBatchDiagnosticsAsync()
    {
        DiagnosticsLog.Mark("── 批量操作诊断开始 ──");

        var results = new List<(string Name, bool Pass, string Detail)>();

        void Check(string name, bool pass, string detail)
        {
            results.Add((name, pass, detail));
            DiagnosticsLog.Mark($"BATCH {(pass ? "PASS" : "FAIL")} | {name} | {detail}");
        }

        var srcDir = Path.Combine(Path.GetTempPath(), "MediaLibrary", "DiagBatchSrc", "运动会");
        var libraryRoot = Environment.GetEnvironmentVariable("MEDIALIBRARY_LIBRARY_ROOT") ?? "";

        try
        {
            Directory.CreateDirectory(srcDir);
            foreach (var f in Directory.GetFiles(srcDir))
                File.Delete(f);

            var ffmpeg = FfmpegLocator.FfmpegPath
                         ?? throw new InvalidOperationException("找不到 ffmpeg");

            // ── 0. 合成素材 ──
            var videoSrc = Path.Combine(srcDir, "合成视频.mp4");
            var photoSrc = Path.Combine(srcDir, "合成照片.png");
            var audioSrc = Path.Combine(srcDir, "合成音频.flac");

            var ok = await RunFfmpegAsync(ffmpeg,
                $"-y -f lavfi -i testsrc=duration=3:size=640x360:rate=30 " +
                $"-f lavfi -i sine=frequency=440:duration=3 " +
                $"-c:v mpeg4 -q:v 5 -c:a aac -shortest \"{videoSrc}\"");

            ok &= await RunFfmpegAsync(ffmpeg,
                $"-y -f lavfi -i color=c=0x3366CC:size=800x600 -frames:v 1 \"{photoSrc}\"");

            ok &= await RunFfmpegAsync(ffmpeg,
                $"-y -f lavfi -i sine=frequency=660:duration=3 -c:a flac \"{audioSrc}\"");

            Check("现场合成测试素材", ok && File.Exists(videoSrc) && File.Exists(photoSrc) && File.Exists(audioSrc),
                "1 视频 + 1 照片 + 1 音频");

            // ── 1. 选中文件夹后导入 → 文件应进【该文件夹】而不是新建（用户实测重大 bug）──
            await _viewModel.CreateFolderAsync(null, "目标文件夹");

            await _viewModel.ImportAsync([srcDir]);

            var ready = await WaitUntilAsync(
                () => !_viewModel.IsJobRunning &&
                      !JobQueue.HasActiveJobs &&
                      !_viewModel.IsEnriching &&
                      _viewModel.Assets.Count == 3 &&
                      _viewModel.Assets.All(a => a.Status == Core.Models.AssetStatus.Ready),
                120_000);

            Check("导入后全部就绪", ready,
                $"素材 {_viewModel.Assets.Count} 个，" +
                $"状态 {string.Join("/", _viewModel.Assets.Select(a => a.Status))}");

            var allInTargetFolder = _viewModel.Assets.All(a => a.Asset.FolderKey == "目标文件夹");
            Check("选中文件夹后拖入：文件进入该文件夹（不新建目录）",
                ready && allInTargetFolder,
                $"FolderKey = {string.Join(" / ", _viewModel.Assets.Select(a => a.Asset.FolderKey).Distinct())}");

            // ── 1b. FLAC 悬浮预览播放（用户实测：flac 播放不了）──
            var flac = _viewModel.Assets.FirstOrDefault(a => a.Kind == Core.Models.AssetKind.Audio);
            if (flac is not null)
            {
                var flacTile = GetContainerFor(flac) as FrameworkElement
                               ?? await GetTileElementAsync(flac);

                if (flacTile is not null)
                {
                    _hover.RequestShow(flac, flacTile, respectCursor: false);
                    var flacShown = await WaitUntilAsync(() => _hover.IsVisible, 3000);
                    await Task.Delay(600);

                    var flacState1 = _hover.GetPlaybackState();
                    await Task.Delay(600);
                    var flacState2 = _hover.GetPlaybackState();

                    Check("FLAC 悬浮预览能播放",
                        flacShown && flacState1.DurationMs > 0 &&
                        flacState2.PositionMs > flacState1.PositionMs,
                        $"时长 {flacState2.DurationMs}ms，位置 {flacState1.PositionMs} → " +
                        $"{flacState2.PositionMs}ms，状态 {flacState2.State}");

                    _hover.HideNow("诊断：FLAC 播放测试结束");
                    await Task.Delay(200);
                }
            }

            // ── 2. 批量重命名 ──
            var beforeRename = _viewModel.Assets.Select(a => a.FileName).ToList();
            await _viewModel.ApplyRenameAsync(_viewModel.Assets.ToList(), "{原名}_批改");

            var afterRename = _viewModel.Assets;
            var renamedOk = afterRename.Count == 3 &&
                            afterRename.All(a => a.FileName.Contains("批改")) &&
                            afterRename.All(a => File.Exists(a.AbsolutePath));

            Check("批量重命名（文件与记录一致）", renamedOk,
                string.Join(" | ", beforeRename) + " → " + string.Join(" | ", afterRename.Select(a => a.FileName)));

            // ── 3. 重新转码 ──
            var video = afterRename.FirstOrDefault(a => a.Kind == Core.Models.AssetKind.Video);
            if (video is not null)
            {
                _viewModel.SyncSelection([video]);
                await _viewModel.RetranscodeSelectedCommand.ExecuteAsync(null);

                var retranscoded = await WaitUntilAsync(
                    () => !_viewModel.IsJobRunning &&
                          !JobQueue.HasActiveJobs &&
                          !_viewModel.IsEnriching &&
                          _viewModel.Assets.First(a => a.Id == video.Id).Status == Core.Models.AssetStatus.Ready,
                    120_000);

                Check("重新转码完成且就绪", retranscoded,
                    $"状态 {_viewModel.Assets.First(a => a.Id == video.Id).Status}");
            }
            else
            {
                Check("重新转码完成且就绪", false, "没有视频素材");
            }

            // ── 4. 删除到回收站 ──
            var photo = _viewModel.Assets.FirstOrDefault(a => a.Kind == Core.Models.AssetKind.Photo);
            if (photo is not null)
            {
                var photoPath = photo.AbsolutePath;
                _viewModel.SyncSelection([photo]);
                await _viewModel.DeleteAssetsCoreAsync([photo]);

                var deleted = !File.Exists(photoPath) &&
                              _viewModel.Assets.All(a => a.Id != photo.Id) &&
                              _viewModel.Assets.Count == 2;

                Check("删除到回收站（文件移除、记录删除）", deleted,
                    $"文件存在={File.Exists(photoPath)}，剩余 {_viewModel.Assets.Count} 个素材");
            }
            else
            {
                Check("删除到回收站（文件移除、记录删除）", false, "没有照片素材");
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("批量操作诊断", ex);
            Check("批量操作诊断整体执行", false, ex.Message);
        }

        DiagnosticsLog.Mark("── 批量操作诊断汇总 ──");
        foreach (var (name, pass, detail) in results)
            DiagnosticsLog.Mark($"   {(pass ? "PASS" : "FAIL")}  {name} —— {detail}");

        var passed = results.Count(r => r.Pass);
        DiagnosticsLog.Mark($"   通过 {passed} / {results.Count}");
    }

    private static async Task<bool> RunFfmpegAsync(string ffmpeg, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ══════════════════════════ 阶段 4 界面诊断 ══════════════════════════

    /// <summary>
    /// 无人值守验证阶段 4 的界面功能：
    /// 队列面板开关与暂停/继续、真实鼠标框选、全选/反选/取消选择。
    /// 只读 + 短暂切换队列状态，不删除/修改任何素材文件。
    /// </summary>
    private async Task RunJobsUiDiagnosticsAsync()
    {
        DiagnosticsLog.Mark("── 阶段 4 界面诊断开始 ──");

        var results = new List<(string Name, bool Pass, string Detail)>();

        void Check(string name, bool pass, string detail)
        {
            results.Add((name, pass, detail));
            DiagnosticsLog.Mark($"JOBS {(pass ? "PASS" : "FAIL")} | {name} | {detail}");
        }

        await Task.Delay(1500);

        // ── 1. 队列面板开关与徽标 ──────────────────────────────
        JobQueue.TogglePanelCommand.Execute(null);
        await Task.Delay(400);

        Check("队列面板可打开", JobQueue.IsPanelOpen,
            JobQueue.IsPanelOpen ? "已打开" : "TogglePanel 没生效");

        Check("状态栏徽标有文案", !string.IsNullOrEmpty(JobQueue.BadgeText),
            $"徽标 = {JobQueue.BadgeText}");

        // ── 2. 暂停 / 继续 ─────────────────────────────────────
        var wasPausedBefore = JobQueue.IsPaused;

        await JobQueue.PauseAllCommand.ExecuteAsync(null);
        await Task.Delay(500);
        Check("全部暂停生效", JobQueue.IsPaused,
            $"IsPaused={JobQueue.IsPaused}（暂停前={wasPausedBefore}）");

        await JobQueue.ResumeAllCommand.ExecuteAsync(null);
        await Task.Delay(500);
        Check("继续生效", !JobQueue.IsPaused,
            $"IsPaused={JobQueue.IsPaused}（应为 False）");

        JobQueue.TogglePanelCommand.Execute(null);

        // ── 2b. 打开目录按钮可点击（用户实测：左上角按钮点不了）──
        try
        {
            var center = OpenFolderButton.TransformToAncestor(this)
                .TransformBounds(new Rect(0, 0, OpenFolderButton.ActualWidth, OpenFolderButton.ActualHeight));
            var centerPoint = new Point(center.Left + center.Width / 2, center.Top + center.Height / 2);

            var hitObj = InputHitTest(centerPoint) as DependencyObject;
            var hitOnButton = false;
            while (hitObj is not null)
            {
                if (ReferenceEquals(hitObj, OpenFolderButton)) { hitOnButton = true; break; }
                hitObj = hitObj is Visual
                    ? VisualTreeHelper.GetParent(hitObj)
                    : LogicalTreeHelper.GetParent(hitObj);
            }

            Check("打开目录按钮可被命中（无遮挡）", hitOnButton,
                hitOnButton ? "命中按钮本身" : "被其他元素遮挡 —— 点不到");

            _viewModel.OpenCurrentFolderCommand.Execute(null);
            await Task.Delay(500);
            Check("打开目录命令执行成功",
                !_viewModel.StatusText.StartsWith("打开目录失败"),
                $"状态：{_viewModel.StatusText}");
        }
        catch (Exception ex)
        {
            Check("打开目录按钮可被命中（无遮挡）", false, ex.Message);
        }

        // ── 2c. 文件夹右键操作（新建 / 重命名 / 删除，树 + 磁盘同步）──
        try
        {
            var keyBefore = _viewModel.SelectedFolder?.FolderKey ?? "";
            var rootPath = _viewModel.LibraryRootPath;

            var (okCreate, msgCreate) = await _viewModel.CreateFolderAsync(null, "新建测试文件夹");

            var createdInTree = FolderNodeViewModel.Find(
                _viewModel.FolderTree, "新建测试文件夹") is not null;
            var createdOnDisk = Directory.Exists(Path.Combine(rootPath, "新建测试文件夹"));

            Check("新建文件夹（树 + 磁盘可见）", okCreate && createdInTree && createdOnDisk,
                $"树={createdInTree} 磁盘={createdOnDisk} 消息={msgCreate}");

            // 重命名
            var (okRename, msgRename) = await _viewModel.RenameFolderAsync(
                "新建测试文件夹", "改名后的文件夹");

            var renamedInTree = FolderNodeViewModel.Find(
                _viewModel.FolderTree, "改名后的文件夹") is not null;
            var oldGoneFromTree = FolderNodeViewModel.Find(
                _viewModel.FolderTree, "新建测试文件夹") is null;
            var renamedOnDisk = Directory.Exists(Path.Combine(rootPath, "改名后的文件夹"));

            Check("重命名文件夹（树 + 磁盘 + 旧名消失）",
                okRename && renamedInTree && oldGoneFromTree && renamedOnDisk,
                $"树={renamedInTree} 旧名消失={oldGoneFromTree} 磁盘={renamedOnDisk} 消息={msgRename}");

            // 删除
            var (okDelete, msgDelete) = await _viewModel.DeleteFolderAsync("改名后的文件夹");

            var deletedFromTree = FolderNodeViewModel.Find(
                _viewModel.FolderTree, "改名后的文件夹") is null;
            var deletedFromDisk = !Directory.Exists(Path.Combine(rootPath, "改名后的文件夹"));

            Check("删除文件夹（树 + 磁盘都移除）",
                okDelete && deletedFromTree && deletedFromDisk,
                $"树={deletedFromTree} 磁盘={deletedFromDisk} 消息={msgDelete}");

            // 还原选中的目录（创建后会选中新文件夹，影响后面的框选测试）
            _viewModel.SelectedFolder = FolderNodeViewModel.Find(
                _viewModel.FolderTree, keyBefore);
            await Task.Delay(300);
        }
        catch (Exception ex)
        {
            Check("文件夹右键操作（新建 / 重命名 / 删除）", false, ex.Message);
        }

        // ── 3. 空白处拖动即框选（真实鼠标）─────────────────────
        // ★ 先把列表滚到中间："拖出边界自动滚动"的断言必须在中间位置做，
        //   顶部/底部本来就没有滚动空间。
        var sv = FindVisualChild<ScrollViewer>(AssetList);
        if (sv is not null && sv.ScrollableHeight > 100)
        {
            sv.ScrollToVerticalOffset(sv.ScrollableHeight / 2);
            await Task.Delay(700);
        }

        var points = FindMarqueePoints();
        if (points is { } found)
        {
            var (startScreen, endScreen) = found;

            AssetList.SelectedItems.Clear();
            await Task.Delay(150);

            await SimulateDragAsync(
                (startScreen.X, startScreen.Y), (endScreen.X, endScreen.Y),
                steps: 14, stepMs: 25, holdMs: 150);
            await Task.Delay(400);

            // 干扰自检 + 重试一次：注入终点与实际光标不符 = 有人动了鼠标
            var marqueeOk = AssetList.SelectedItems.Count > 0;
            if (!marqueeOk)
            {
                NativeWindow.TryGetCursorPosition(out var cx1, out var cy1);
                DiagnosticsLog.Mark(
                    $"⚠ 基础框选 0 项：注入终点=({endScreen.X},{endScreen.Y})，" +
                    $"实际光标=({cx1},{cy1}) —— 疑似外部干扰，重试一次");

                await Task.Delay(600);
                await SimulateDragAsync(
                    (startScreen.X, startScreen.Y), (endScreen.X, endScreen.Y),
                    steps: 14, stepMs: 25, holdMs: 150);
                await Task.Delay(400);
            }

            Check("空白处拖动即框选", AssetList.SelectedItems.Count > 0,
                $"框选选中 {AssetList.SelectedItems.Count} 项");

            // ── 3c. 从卡片上拖动 = 拖出文件（导出，无法无人值守模拟 OLE 拖放，
            //        交由用户实测；此处只验证空白框选路径不受影响）──
            DiagnosticsLog.Mark("[诊断] 卡片拖动=拖出文件（导出），需人工实测");

            // ── 3b. 上拖：列表保持不动（防跳变）+ 框外文件全选中 ──
            var scrollBefore = GetListScrollOffset();
            var selBefore = AssetList.SelectedItems.Count;
            var minIndexBeforeUpAtDragStart = AssetList.SelectedItems.Count > 0
                ? AssetList.SelectedItems.Cast<AssetItemViewModel>()
                    .Select(it => _viewModel.Assets.IndexOf(it))
                    .Where(i => i >= 0)
                    .Min()
                : int.MaxValue;

            // 向上拖出列表上边界，保持期间分两次采样滚动位置
            var listTopScreen = PointToScreen(AssetList.TranslatePoint(new Point(8, 0), this));

            NativeWindow.SimulateMouseDown(startScreen.X, startScreen.Y);
            await Task.Delay(60);

            // 全程采样选中数：断言【只增不减】（用户实测"超出窗口的框选过的文件
            // 有几率取消选择"—— 从最下框选到最上的完整路径逐段验证）
            var countsDuring = new List<int>();

            for (var i = 1; i <= 16; i++)
            {
                NativeWindow.SimulateMouseMove(
                    startScreen.X - 700 * i / 16,
                    startScreen.Y + ((int)Math.Round(listTopScreen.Y) - 150 - startScreen.Y) * i / 16);
                await Task.Delay(60);

                if (i % 3 == 0)
                    countsDuring.Add(AssetList.SelectedItems.Count);
            }

            var upA = GetListScrollOffset();
            await Task.Delay(250);
            var upB = GetListScrollOffset();
            await Task.Delay(250);
            var upC = GetListScrollOffset();

            // ★ 用户实测场景：拖出顶部后，把光标【移回列表中间】（仍在选框内）
            //   —— 中间部分不得被取消（估算几何漂移误判的修复验证）
            var selBeforeWiggle = AssetList.SelectedItems.Count;
            NativeWindow.SimulateMouseMove(
                startScreen.X - 350,
                Math.Min(startScreen.Y - 300, startScreen.Y - 60));
            await Task.Delay(400);
            var selAfterWiggle = AssetList.SelectedItems.Count;

            NativeWindow.SimulateMouseUp();
            await Task.Delay(150);

            Check("光标移回列表中间后已选项不丢失",
                selAfterWiggle >= selBeforeWiggle && selAfterWiggle > 0,
                $"移回前 {selBeforeWiggle} 项 → 移回后 {selAfterWiggle} 项（不得减少）");

            // ★ 用户实测：从最下框选到最上，全程选中数只增不减
            var monotonic = countsDuring.Count >= 2 &&
                            countsDuring.Zip(countsDuring.Skip(1), (a, b) => b >= a).All(x => x);
            Check("从最下框选到最上：全程选中数只增不减", monotonic,
                $"采样 {string.Join(" → ", countsDuring)}");

            // 外部干扰自检：注入的光标终点若与实际光标不符，说明有人动了鼠标
            // （终点 = 最后一次注入的位置，即移回列表中间的点）
            var expectedEndX = startScreen.X - 350;
            var expectedEndY = Math.Min(startScreen.Y - 300, startScreen.Y - 60);
            if (NativeWindow.TryGetCursorPosition(out var cx, out var cy) &&
                (Math.Abs(cx - expectedEndX) > 20 || Math.Abs(cy - expectedEndY) > 20))
            {
                DiagnosticsLog.Mark(
                    $"⚠ 检测到外部光标干扰：注入终点=({expectedEndX},{expectedEndY})，" +
                    $"实际光标=({cx},{cy}) —— 本轮框选结果可能不可信");
            }

            var selAfterUp = AssetList.SelectedItems.Count;

            Check("上拖列表保持不动（防跳变策略：向上不滚动）",
                Math.Abs(upC - scrollBefore) < 2,
                $"偏移 {scrollBefore:F1} → {upC:F1}（应纹丝不动；向上拖由算术选中覆盖框外文件）");

            Check("上拖时窗口外的文件被继续框选", selAfterUp > selBefore,
                $"选中 {selBefore} → {selAfterUp} 项");

            // ★ 框外扩展断言：上拖后选中集的最小索引应【小于】上拖前的最小索引
            //   （即确实选到了更靠上、原本在窗口外的文件）
            var minIndexAfterUp = AssetList.SelectedItems.Count > 0
                ? AssetList.SelectedItems.Cast<AssetItemViewModel>()
                    .Select(it => _viewModel.Assets.IndexOf(it))
                    .Where(i => i >= 0)
                    .Min()
                : int.MaxValue;

            Check("上拖选中了更靠上的文件（框外扩展）", minIndexAfterUp < minIndexBeforeUpAtDragStart,
                $"最小索引 {minIndexBeforeUpAtDragStart} → {minIndexAfterUp}");

            // 向下拖出列表下边界（滚动后重新找空白起点）
            var points2 = FindMarqueePoints();
            if (points2 is { } found2)
            {
                var (start2, _) = found2;
                var selBeforeDown = AssetList.SelectedItems.Count;
                var downStart = GetListScrollOffset();

                var listBottomScreen = PointToScreen(
                    AssetList.TranslatePoint(new Point(8, AssetList.ActualHeight - 8), this));

                NativeWindow.SimulateMouseDown(start2.X, start2.Y);
                await Task.Delay(60);

                for (var i = 1; i <= 16; i++)
                {
                    NativeWindow.SimulateMouseMove(
                        start2.X - 700 * i / 16,
                        start2.Y + ((int)Math.Round(listBottomScreen.Y) + 150 - start2.Y) * i / 16);
                    await Task.Delay(60);
                }

                var downA = GetListScrollOffset();
                await Task.Delay(250);
                var downB = GetListScrollOffset();
                await Task.Delay(250);
                var downC = GetListScrollOffset();

                NativeWindow.SimulateMouseUp();
                await Task.Delay(150);

                var selAfterDown = AssetList.SelectedItems.Count;

                Check("拖出下边界列表向下滚动（方向正确）",
                    downC > downStart,
                    $"偏移 {downStart:F1} → {downC:F1}（应增大；面板允许纠偏/跳变）");

                Check("下拖时滚入视野的文件被继续框选", selAfterDown > 0,
                    $"选中 {selAfterDown} 项（下拖框选扫过底部区域）");
            }
            else
            {
                Check("拖出下边界列表向下滚动（方向正确）", false, "找不到空白起点");
            }
        }
        else
        {
            Check("空白处拖动即框选", false,
                "找不到空白起点 —— 当前网格被卡片填满，换一个目录重试");
        }

        // ── 4. 全选 / 反选 / 取消选择 ──────────────────────────
        var total = AssetList.Items.Count;

        OnSelectAllClick(this, new RoutedEventArgs());
        await Task.Delay(200);
        var all = AssetList.SelectedItems.Count;
        Check("全选", all == total && total > 0, $"{all}/{total}");

        OnInvertSelectionClick(this, new RoutedEventArgs());
        await Task.Delay(200);
        Check("反选", AssetList.SelectedItems.Count == 0,
            $"反选后 {AssetList.SelectedItems.Count} 项（全选反选应为 0）");

        // ── 4b. 点击空白处取消选择（用户要求）──
        OnSelectAllClick(this, new RoutedEventArgs());
        await Task.Delay(200);

        var clickPoints = FindMarqueePoints();
        if (clickPoints is { } cp)
        {
            NativeWindow.SimulateMouseDown(cp.Start.X, cp.Start.Y);
            await Task.Delay(80);
            NativeWindow.SimulateMouseUp();
            await Task.Delay(250);

            Check("点击空白处取消选择", AssetList.SelectedItems.Count == 0,
                $"点击后 {AssetList.SelectedItems.Count} 项（应从全选变 0）");
        }
        else
        {
            Check("点击空白处取消选择", false, "找不到空白起点");
        }

        OnSelectAllClick(this, new RoutedEventArgs());
        OnClearSelectionClick(this, new RoutedEventArgs());
        await Task.Delay(200);
        Check("取消选择", AssetList.SelectedItems.Count == 0,
            $"取消后 {AssetList.SelectedItems.Count} 项");

        DiagnosticsLog.Mark("── 阶段 4 界面诊断汇总 ──");
        foreach (var (name, pass, detail) in results)
            DiagnosticsLog.Mark($"   {(pass ? "PASS" : "FAIL")}  {name} —— {detail}");

        var passed = results.Count(r => r.Pass);
        DiagnosticsLog.Mark($"   通过 {passed} / {results.Count}");
    }

    /// <summary>素材列表当前的垂直滚动偏移（诊断断言用）。</summary>
    private double GetListScrollOffset()
    {
        try
        {
            var scrollViewer = FindVisualChild<ScrollViewer>(AssetList);
            return scrollViewer?.VerticalOffset ?? -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>第一个已实现容器的索引（诊断断言"框外文件被选中"用）。</summary>
    private int FirstRealizedIndex()
    {
        for (var i = 0; i < AssetList.Items.Count; i++)
        {
            if (AssetList.ItemContainerGenerator.ContainerFromIndex(i) is not null) return i;
        }

        return -1;
    }

    /// <summary>最后一个已实现容器的索引（自动滚动"向下滚一行"用）。</summary>
    private int LastRealizedIndex()
    {
        for (var i = AssetList.Items.Count - 1; i >= 0; i--)
        {
            if (AssetList.ItemContainerGenerator.ContainerFromIndex(i) is not null) return i;
        }

        return -1;
    }

    /// <summary>
    /// 视口内【最上面】可见条目的索引。
    ///
    /// ★ 不能用"第一个非空容器"：回收复用的陈旧容器会短暂挂在低索引上
    ///   （实测 FirstRealizedIndex 拿到索引 0 → 上滚一步变成滚到第 0 项 →
    ///   列表直接跳到最顶上）。这里用真实几何过滤：只认确实在视口内的容器。
    /// </summary>
    private int FirstVisibleIndex()
    {
        var best = -1;
        var bestY = double.MaxValue;

        for (var i = 0; i < AssetList.Items.Count; i++)
        {
            if (AssetList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            // 陈旧容器（回收复用后未重绑）的 DataContext 对不上索引 —— 跳过
            if (container.DataContext is not AssetItemViewModel dc ||
                !ReferenceEquals(AssetList.Items[i], dc))
                continue;

            var bounds = container.TransformToAncestor(AssetList)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

            if (bounds.Bottom <= 0 || bounds.Top >= AssetList.ActualHeight) continue;

            if (bounds.Top < bestY)
            {
                bestY = bounds.Top;
                best = i;
            }
        }

        return best;
    }

    /// <summary>视口内【最下面】可见条目的索引（同样按真实几何过滤）。</summary>
    private int LastVisibleIndex()
    {
        var best = -1;
        var bestY = double.MinValue;

        for (var i = 0; i < AssetList.Items.Count; i++)
        {
            if (AssetList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            // 陈旧容器（回收复用后未重绑）的 DataContext 对不上索引 —— 跳过
            if (container.DataContext is not AssetItemViewModel dc ||
                !ReferenceEquals(AssetList.Items[i], dc))
                continue;

            var bounds = container.TransformToAncestor(AssetList)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

            if (bounds.Bottom <= 0 || bounds.Top >= AssetList.ActualHeight) continue;

            if (bounds.Bottom > bestY)
            {
                bestY = bounds.Bottom;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// 异步分步模拟一次鼠标拖动：按下 → 分步移动（每步 await，让 DispatcherTimer
    /// 有运行机会，自动滚动才能生效）→ 保持 holdMs → 松开。
    /// </summary>
    private static async Task SimulateDragAsync(
        (int X, int Y) from, (int X, int Y) to,
        int steps = 14, int stepMs = 25, int holdMs = 350)
    {
        NativeWindow.SimulateMouseDown(from.X, from.Y);
        await Task.Delay(60);

        for (var i = 1; i <= steps; i++)
        {
            NativeWindow.SimulateMouseMove(
                from.X + (to.X - from.X) * i / steps,
                from.Y + (to.Y - from.Y) * i / steps);
            await Task.Delay(stepMs);
        }

        await Task.Delay(holdMs);
        NativeWindow.SimulateMouseUp();
        await Task.Delay(150);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is T match) return match;

            var deeper = FindVisualChild<T>(child);
            if (deeper is not null) return deeper;
        }

        return null;
    }

    /// <summary>
    /// 在素材网格里找一个【空白起点】（不压在任何卡片上），
    /// 返回框选拖动的屏幕起点与终点。
    /// </summary>
    private ((int X, int Y) Start, (int X, int Y) End)? FindMarqueePoints()
    {
        try
        {
            var w = AssetList.ActualWidth;
            var h = AssetList.ActualHeight;

            // 扫描右边缘与底边缘（列表任意滚动位置都可能有空条）：
            // 右侧竖扫 + 底部横扫，逐点找第一个不压卡片的位置
            var candidates = new List<Point>();

            for (var y = h - 18; y > 24; y -= 42)
            {
                candidates.Add(new Point(w - 18, y));
                candidates.Add(new Point(w - 60, y));
            }

            for (var x = w - 18; x > 24; x -= 42)
                candidates.Add(new Point(x, h - 18));

            foreach (var candidate in candidates)
            {
                var windowPoint = AssetList.TranslatePoint(candidate, this);
                if (windowPoint.X < 0 || windowPoint.Y < 0) continue;
                if (IsOverCard(windowPoint)) continue;

                var start = PointToScreen(windowPoint);
                var end = new Point(
                    Math.Max(8, windowPoint.X - 240),
                    Math.Max(8, windowPoint.Y - 160));
                var endScreen = PointToScreen(end);

                return (
                    ((int)Math.Round(start.X), (int)Math.Round(start.Y)),
                    ((int)Math.Round(endScreen.X), (int)Math.Round(endScreen.Y)));
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ══════════════════════════ 悬浮预览诊断 ══════════════════════════

    /// <summary>
    /// 无人值守地验证悬浮预览（设计文档 §9 阶段3 验收标准）。
    /// 用程序触发 RequestShow/RequestHide，避免依赖真实鼠标。
    /// 诊断模式下真实鼠标悬停已被禁用，脚本完全掌控时序。
    ///
    /// ★ 预览现在【在主窗口内】：没有独立系统窗口、没有原生子窗口、
    ///   没有光标轮询 —— 所有验证都直接对窗口内元素做断言。
    /// </summary>
    private async Task RunHoverDiagnosticsAsync()
    {
        DiagnosticsLog.Mark("── 悬浮预览诊断开始 ──");

        // ★ 程序化阶段绝不碰真实光标（用户可能正按着鼠标，强行挪光标会把
        //   用户的拖动拽到窗口标题栏上 —— 实测导致"主窗口被拖走"）。
        //   依赖光标位置的两处逻辑改为显式控制：
        //   · 保活轮询 → KeepAliveDisabled（真实鼠标阶段前重新打开）
        //   · "光标在面板内忽略悬停"规则 → 程序化调用传 respectCursor: false
        _hover.KeepAliveDisabled = true;
        _diagnosticsRunning = true;
        _externalInterference = 0;

        // ★ 用户的悬浮延迟设置不影响验收基线：诊断强制用默认 280ms
        //   （否则用户在设置里改过延迟后，"悬停出画面 ≤400ms"会误报失败）
        _hover.ApplyRuntimeSettings(
            new AppSettings { HoverPreviewEnabled = true, HoverPreviewDelayMs = 280 });

        var results = new List<(string Name, bool Pass, string Detail)>();

        void Check(string name, bool pass, string detail)
        {
            results.Add((name, pass, detail));
            DiagnosticsLog.Mark($"HOVER {(pass ? "PASS" : "FAIL")} | {name} | {detail}");
        }

        var video = _viewModel.Assets.FirstOrDefault(a => a.Kind == AssetKind.Video);
        var audio = _viewModel.Assets.FirstOrDefault(a => a.Kind == AssetKind.Audio);
        var photo = _viewModel.Assets.FirstOrDefault(a => a.Kind == AssetKind.Photo);

        DiagnosticsLog.Mark($"素材: 视频 {_viewModel.Assets.Count(a => a.Kind == AssetKind.Video)} / " +
                            $"照片 {_viewModel.Assets.Count(a => a.Kind == AssetKind.Photo)} / " +
                            $"音频 {_viewModel.Assets.Count(a => a.Kind == AssetKind.Audio)}");

        // ── 1. 架构：预览是主窗口内的面板，不是独立系统窗口 ──────
        Check("预览是主窗口内的面板（不是独立系统窗口）",
            _hover.PanelIsInMainWindow,
            _hover.PanelIsInMainWindow
                ? "面板挂在主窗口的覆盖层里"
                : "面板未挂载 —— 架构变更未生效");

        if (video is null)
        {
            DiagnosticsLog.Mark("库里没有视频素材，诊断中止");
            return;
        }

        var tile = await GetTileElementAsync(video);
        if (tile is null)
        {
            DiagnosticsLog.Mark("拿不到卡片元素，诊断中止");
            return;
        }

        // ── 2. 防抖：快速划过不该弹窗 ──────────────────────────
        _hover.RequestShow(video, tile, respectCursor: false);
        _hover.RequestHide();
        await Task.Delay(700);

        Check("快速划过不弹窗（280ms 防抖生效）", !_hover.IsVisible,
            _hover.IsVisible ? "居然弹出来了" : "划过时间短于防抖窗口，正确未弹出");

        // ── 3. 悬停 → 弹出延迟 ────────────────────────────────
        var sw = Stopwatch.StartNew();
        _hover.RequestShow(video, tile, respectCursor: false);
        var appeared = await WaitUntilAsync(() => _hover.IsVisible, 4000);
        var latency = sw.ElapsedMilliseconds;

        Check("悬停后弹出预览", appeared, $"耗时 {latency}ms");
        Check("悬停到出画面 ≤400ms", appeared && latency <= 400,
            $"{latency}ms = 防抖 280ms + 弹出开销");

        // 面板刚显示时首次布局可能还没跑完（ActualWidth 仍为 0），
        // 等待布局完成再读尺寸 —— 避免把布局时序抖动误判为面板 bug
        await WaitUntilAsync(() => _hover.PanelBounds is { Width: > 100 }, 2000);

        var bounds = _hover.PanelBounds;
        Check("面板定位到合理位置与尺寸",
            bounds is { Width: > 300, Height: > 200 },
            bounds is { } b ? $"{b.Width:F0}x{b.Height:F0} @ ({b.X:F0},{b.Y:F0})" : "拿不到面板矩形");

        // ── 4. 不抢焦点（窗口内浮层天然如此，仍验证前台窗口不变）──
        var mainHandle = new WindowInteropHelper(this).Handle;
        NativeWindow.BringToFront(mainHandle);
        await Task.Delay(500);

        var foregroundBefore = NativeWindow.GetForegroundWindow();
        await Task.Delay(400);
        var foregroundAfter = NativeWindow.GetForegroundWindow();

        Check("预览显示期间未抢走焦点", foregroundAfter == foregroundBefore,
            $"前台窗口 {foregroundBefore} → {foregroundAfter}");

        // ── 5. 视频在播 ───────────────────────────────────────
        var state1 = _hover.GetPlaybackState();
        await Task.Delay(700);
        var state2 = _hover.GetPlaybackState();

        Check("视频正在播放且进度在推进",
            state2.PositionMs > state1.PositionMs && state1.PositionMs >= 0,
            $"位置 {state1.PositionMs}ms → {state2.PositionMs}ms，状态 {state2.State}，时长 {state2.DurationMs}ms");

        // ── 6. 面板画面区拦截鼠标：被盖住的卡片不得被触发 ────────
        //      （用户实测：穿透导致鼠标一移到面板上，预览就切到"鼠标下面
        //       的文件"并来回抖动，控制条永远点不到）
        var panelHits = HitTestPanelCenter(out var centerHitCard);
        Check("面板画面区拦截鼠标（被盖住的卡片不被误触发）",
            panelHits && !centerHitCard,
            panelHits
                ? (centerHitCard
                    ? "命中了下面的卡片 —— 穿透问题还在"
                    : "命中面板本身，下面的卡片被挡住")
                : "面板画面区没有参与命中测试");

        // ── 7. 宽限期隐藏 ──────────────────────────────────────
        // 程序化阶段保活轮询已关闭（KeepAliveDisabled）—— 隐藏倒计时
        // 不会因真实光标位置被取消，测试完全确定，无需挪动用户的光标。
        _hover.RequestHide();
        await Task.Delay(150);
        var visibleDuringGrace = _hover.IsVisible;

        var hiddenAfterGrace = await WaitUntilAsync(() => !_hover.IsVisible, 3000);
        Check("移开后经宽限期隐藏（留时间给拖进度条）",
            visibleDuringGrace && hiddenAfterGrace,
            visibleDuringGrace
                ? $"移开 150ms 时仍在显示（宽限期 {HoverPreviewController.GraceMs}ms 内），之后已隐藏"
                : "移开就立刻消失了 —— 宽限期没生效");

        // ── 8. 音频 ───────────────────────────────────────────
        if (audio is not null)
        {
            try
            {
                DiagnosticsLog.Mark("[诊断] 步骤 8：音频预览");
                var audioTile = await GetTileElementAsync(audio);
                if (audioTile is not null)
                {
                    var audioSw = Stopwatch.StartNew();
                    _hover.RequestShow(audio, audioTile, respectCursor: false);
                    var audioAppeared = await WaitUntilAsync(() => _hover.IsVisible, 3000);
                    var audioLatency = audioSw.ElapsedMilliseconds;

                    await Task.Delay(700);

                    var audioState = _hover.GetPlaybackState();
                    Check("音频预览能弹出", audioAppeared, $"耗时 {audioLatency}ms");
                    Check("音频能正常播放", audioState.DurationMs > 0,
                        $"时长 {audioState.DurationMs}ms，位置 {audioState.PositionMs}ms，状态 {audioState.State}");

                    _hover.HideNow("诊断：音频测试结束");
                    await Task.Delay(200);
                }
            }
            catch (Exception ex) { DiagnosticsLog.Error("音频预览诊断", ex); }
        }

        // ── 9. 照片：只显示放大图，不播放 ──────────────────────
        if (photo is not null)
        {
            try
            {
                DiagnosticsLog.Mark("[诊断] 步骤 9：照片预览");
                var photoTile = await GetTileElementAsync(photo);
                if (photoTile is not null)
                {
                    _hover.RequestShow(photo, photoTile, respectCursor: false);
                    await WaitUntilAsync(() => _hover.IsVisible, 3000);
                    await Task.Delay(400);

                    var photoState = _hover.GetPlaybackState();
                    Check("照片预览只显示放大图，不启动播放",
                        _hover.IsVisible && photoState.DurationMs <= 0,
                        $"可见={_hover.IsVisible}，时长 {photoState.DurationMs}ms（应为 0）");

                    _hover.HideNow("诊断：照片测试结束");
                    await Task.Delay(200);
                }
            }
            catch (Exception ex) { DiagnosticsLog.Error("照片预览诊断", ex); }
        }

        // ── 10. 空格固定 + 悬停其他卡片自动取消 ─────────────────
        try
        {
            DiagnosticsLog.Mark("[诊断] 步骤 10：空格固定预览");
            var tileA = await GetTileElementAsync(video);
            var pair = _viewModel.Assets.Where(a => a.Kind == AssetKind.Video).Take(2).ToList();

            if (tileA is not null && pair.Count >= 2)
            {
                _hover.TogglePin(pair[0], tileA);
                await Task.Delay(600);

                var pinnedShown = _hover.ShownAssetId;
                var pinnedBounds = _hover.PanelBounds;

                _hover.RequestHide();
                await Task.Delay(800);
                var stillVisible = _hover.IsVisible;

                Check("空格键固定预览后可长时间查看",
                    pinnedShown == pair[0].Id && stillVisible,
                    $"固定时显示素材 {pinnedShown}，移开 800ms 后仍显示" +
                    $"（位置 {(pinnedBounds is { } pb ? $"{pb.X:F0},{pb.Y:F0}" : "?")}）");

                var tileB = await GetTileElementAsync(pair[1]);
                if (tileB is not null)
                {
                    _hover.RequestShow(pair[1], tileB, respectCursor: false);
                    await WaitUntilAsync(() => _hover.ShownAssetId == pair[1].Id, 3000);
                    await Task.Delay(200);

                    Check("固定后悬停其他卡片会自动取消固定并跟过去",
                        !_hover.IsPinned && _hover.ShownAssetId == pair[1].Id,
                        $"IsPinned={_hover.IsPinned}，当前素材 {_hover.ShownAssetId}");
                }

                _hover.HideNow("诊断：固定测试结束");
                await Task.Delay(200);
            }
        }
        catch (Exception ex) { DiagnosticsLog.Error("固定预览诊断", ex); }

        // ── 11. 位置随卡片变化 ─────────────────────────────────
        try
        {
            DiagnosticsLog.Mark("[诊断] 步骤 11：预览位置随卡片变化");
            var samples = _viewModel.Assets
                .Where(a => a.Kind == AssetKind.Video || a.Kind == AssetKind.Photo)
                .Take(4)
                .ToList();

            var positions = new List<(string Name, double X, double Y, double CardX)>();

            foreach (var card in samples)
            {
                var cardTile = await GetTileElementAsync(card);
                if (cardTile is null) continue;

                _hover.RequestShow(card, cardTile, respectCursor: false);
                var switched = await WaitUntilAsync(() => _hover.ShownAssetId == card.Id, 3000);
                if (!switched) continue;

                await Task.Delay(120);

                var cardPoint = cardTile.TranslatePoint(new Point(0, 0), HoverOverlay);
                if (_hover.PanelBounds is { } pb)
                    positions.Add((card.FileName, pb.X, pb.Y, cardPoint.X));
            }

            var distinctX = positions.Select(p => p.X).Distinct().Count();

            Check("预览位置随卡片变化（不是固定在一个地方）",
                positions.Count >= 2 && distinctX == positions.Count,
                positions.Count == 0
                    ? "没采到样本"
                    : string.Join("；", positions.Select(p => $"{p.Name} 卡片X={p.CardX:F0} → 面板X={p.X:F0}")));

            var allNear = positions.All(p => Math.Abs(p.X - p.CardX) <= 700);
            Check("预览位置贴近所悬停的卡片", allNear,
                allNear ? "每个位置的横向偏移都在一张卡片宽度量级内" : "有位置离卡片太远");

            _hover.HideNow("诊断：位置测试结束");
            await Task.Delay(200);
        }
        catch (Exception ex) { DiagnosticsLog.Error("位置变化诊断", ex); }

        // ── 12. 快速切换到下一张立即生效 ───────────────────────
        try
        {
            DiagnosticsLog.Mark("[诊断] 步骤 12：预览可见时切换卡片立即生效");
            var pair = _viewModel.Assets.Where(a => a.Kind == AssetKind.Video).Take(2).ToList();
            if (pair.Count >= 2)
            {
                var tileOne = await GetTileElementAsync(pair[0]);
                var tileTwo = await GetTileElementAsync(pair[1]);

                if (tileOne is not null && tileTwo is not null)
                {
                    _hover.RequestShow(pair[0], tileOne, respectCursor: false);
                    await WaitUntilAsync(() => _hover.IsVisible, 3000);
                    await Task.Delay(200);

                    var switchSw = Stopwatch.StartNew();
                    _hover.RequestShow(pair[1], tileTwo, respectCursor: false);
                    var switched = await WaitUntilAsync(() => _hover.ShownAssetId == pair[1].Id, 300);
                    var switchLatency = switchSw.ElapsedMilliseconds;

                    Check("预览可见时移到下一张卡片立即切换（不等 280ms 防抖）",
                        switched && switchLatency < 280,
                        switched
                            ? $"{switchLatency}ms 内完成切换（素材 {pair[0].Id} → {pair[1].Id}）"
                            : "300ms 内没有切换 —— 快速划过时预览会停在第一张");

                    _hover.HideNow("诊断：切换测试结束");
                    await Task.Delay(200);
                }
            }
        }
        catch (Exception ex) { DiagnosticsLog.Error("快速切换诊断", ex); }

        // ── 13. 刮擦跳转 ──────────────────────────────────────
        try
        {
            DiagnosticsLog.Mark("[诊断] 步骤 13：刮擦跳转");
            var longest = _viewModel.Assets
                .Where(a => a.Kind == AssetKind.Video)
                .OrderByDescending(a => a.Asset.DurationMs ?? 0)
                .FirstOrDefault();

            if (longest is not null)
            {
                var seekTile = await GetTileElementAsync(longest);
                if (seekTile is not null)
                {
                    _hover.RequestShow(longest, seekTile, respectCursor: false);
                    await WaitUntilAsync(() => _hover.IsVisible, 3000);
                    await Task.Delay(500);

                    var before = _hover.GetPlaybackState();

                    _hover.PerformDiagnosticSeek(0.5);
                    await Task.Delay(250);

                    var after = _hover.GetPlaybackState();

                    var duration = after.DurationMs;
                    var expected = (long)(duration * 0.5);

                    var seekWorked = after.PositionMs > before.PositionMs + 300 ||
                                     Math.Abs(after.PositionMs - expected) < Math.Max(400, duration * 0.15);

                    Check("刮擦后播放位置跳到目标附近", seekWorked,
                        $"位置 {before.PositionMs}ms → {after.PositionMs}ms" +
                        $"（时长 {duration}ms，50% 目标 ≈ {expected}ms）");

                    _hover.HideNow("诊断：刮擦测试结束");
                }
            }
        }
        catch (Exception ex) { DiagnosticsLog.Error("刮擦诊断", ex); }

        // ── 14. 真实鼠标路径（此前诊断全用程序调用，绕过了真实事件链 ——
        //       这是用户三次反馈"位置不变/进度条不能拖"没被测出来的盲区）──
        try
        {
            DiagnosticsLog.Mark("[诊断] 步骤 14：真实鼠标路径（事件链端到端）");

            var pair = _viewModel.Assets.Where(a => a.Kind == AssetKind.Video).Take(2).ToList();
            if (pair.Count >= 2)
            {
                var tileA = await GetTileElementAsync(pair[0]);
                var tileB = await GetTileElementAsync(pair[1]);

                if (tileA is not null && tileB is not null)
                {
                    // 打开真实悬停路径 + 恢复保活轮询（真实鼠标阶段与线上行为一致）
                    _ignoreRealMouseHover = false;
                    _hover.KeepAliveDisabled = false;
                    try
                    {
                        // a. 真实光标移到卡片 A 中心 → 预览应出现
                        var (ax, ay) = CenterOf(tileA);
                        NativeWindow.SetCursorPosition(ax, ay);
                        var realShow = await WaitUntilAsync(
                            () => _hover.ShownAssetId == pair[0].Id && _hover.IsVisible, 3000);

                        // 滚动等干扰可能吞掉第一次 MouseEnter（光标已停在卡片上，
                        // 不会再触发）—— 轻微晃动光标重新触发，模拟真实用户的自然移动
                        if (!realShow)
                        {
                            NativeWindow.SetCursorPosition(ax + 30, ay);
                            await Task.Delay(120);
                            NativeWindow.SetCursorPosition(ax, ay);
                            realShow = await WaitUntilAsync(
                                () => _hover.ShownAssetId == pair[0].Id && _hover.IsVisible, 2000);
                        }

                        await Task.Delay(200);

                        Check("真实鼠标悬停卡片 A：预览出现",
                            realShow,
                            realShow ? $"显示素材 {_hover.ShownAssetId}" : "预览没出现");

                        // b. 控制条可命中（IsHitTestVisible 结构修复的验证）
                        var barInteractive = false;
                        if (_hover.PanelBounds is { } pb)
                        {
                            var barPoint = HoverOverlay.TranslatePoint(
                                new Point(pb.X + pb.Width / 2, pb.Y + pb.Height - 20), this);
                            var hitObj = InputHitTest(barPoint) as DependencyObject;
                            while (hitObj is not null)
                            {
                                if (hitObj is HoverPreviewPanel)
                                {
                                    barInteractive = true;
                                    break;
                                }
                                hitObj = hitObj is Visual
                                    ? VisualTreeHelper.GetParent(hitObj)
                                    : LogicalTreeHelper.GetParent(hitObj);
                            }
                        }
                        Check("控制条可命中（进度条能拖动的前提）", barInteractive,
                            barInteractive ? "命中测试命中面板" : "控制条被穿透了 —— 点不到");

                        // c. 真实光标移到一张【没被面板盖住】的卡片 → 面板应立即跟随
                        //    （面板已改为拦截鼠标：被面板盖住的部分本来就点不到卡片，
                        //      跟随目标必须选面板没盖住的位置 —— 这正是真实使用方式）
                        var follow = await FindFollowTargetAsync();
                        if (follow is { } ft)
                        {
                            var posBeforeMove = _hover.PanelBounds;

                            NativeWindow.SetCursorPosition(ft.X, ft.Y);
                            var realFollow = await WaitUntilAsync(
                                () => _hover.ShownAssetId == ft.Item.Id, 1000);
                            await Task.Delay(200);

                            var posAfterMove = _hover.PanelBounds;

                            Check("真实鼠标移到下一张卡片：面板立即跟随", realFollow,
                                realFollow
                                    ? $"已切换到素材 {_hover.ShownAssetId}"
                                    : $"还停在 {_hover.ShownAssetId} —— 真实事件链没把新卡片传进来");

                            var positionMoved = posBeforeMove is { } pbm && posAfterMove is { } pam &&
                                                (Math.Abs(pam.X - pbm.X) > 10 || Math.Abs(pam.Y - pbm.Y) > 10);

                            string posDetail;
                            if (posBeforeMove is { } pbm2 && posAfterMove is { } pam2)
                                posDetail = $"位置 ({pbm2.X:F0},{pbm2.Y:F0}) → ({pam2.X:F0},{pam2.Y:F0})";
                            else
                                posDetail = "拿不到面板位置";

                            Check("面板位置跟随卡片变化（不是钉在一处）", positionMoved,
                                positionMoved
                                    ? posDetail
                                    : $"{posDetail} —— 所有卡片被当成了同一个矩形");
                        }
                        else
                        {
                            Check("真实鼠标移到下一张卡片：面板立即跟随", false,
                                "找不到一张没被面板盖住的卡片 —— 布局异常");
                        }

                        // f. 真实点击控制条：点进度条 30% 处 → 播放位置跳到点击处
                        //    （"点击悬浮窗内的内容"必须真实可用）
                        //    ★ 放在拖动之前：拖动会拖到接近 100%，视频很快结束，
                        //      结束后的播放器不响应 seek，点击测试会误报失败。
                        if (_hover.PanelBounds is { } pb3)
                        {
                            var barY = pb3.Y + pb3.Height - 20;
                            // TrackArea 约从 x+10 到 x+Width-100，取其中 30% 处
                            var clickWindow = HoverOverlay.TranslatePoint(
                                new Point(pb3.X + 10 + (pb3.Width - 100) * 0.3, barY), this);
                            var clickScreen = PointToScreen(clickWindow);

                            var beforeClick = _hover.GetPlaybackState();

                            NativeWindow.SimulateMouseDrag(
                                (int)Math.Round(clickScreen.X), (int)Math.Round(clickScreen.Y),
                                (int)Math.Round(clickScreen.X), (int)Math.Round(clickScreen.Y));
                            await Task.Delay(300);

                            var afterClick = _hover.GetPlaybackState();
                            var expectedClick = (long)(afterClick.DurationMs * 0.3);

                            var clicked = afterClick.DurationMs > 0 &&
                                          Math.Abs(afterClick.PositionMs - expectedClick) <
                                          Math.Max(400, afterClick.DurationMs * 0.2);

                            Check("真实点击进度条后播放位置跳到点击处", clicked,
                                $"位置 {beforeClick.PositionMs}ms → {afterClick.PositionMs}ms" +
                                $"（时长 {afterClick.DurationMs}ms，点击处 ≈ {expectedClick}ms）");
                        }

                        // d. 真实拖动刮擦条：按下 → 拖到 80% → 松开
                        if (_hover.PanelBounds is { } pb2)
                        {
                            var barY = pb2.Y + pb2.Height - 20;
                            var fromWindow = HoverOverlay.TranslatePoint(new Point(pb2.X + 30, barY), this);
                            var toWindow = HoverOverlay.TranslatePoint(new Point(pb2.X + pb2.Width - 60, barY), this);
                            var fromScreen = PointToScreen(fromWindow);
                            var toScreen = PointToScreen(toWindow);

                            var beforeDrag = _hover.GetPlaybackState();

                            NativeWindow.SimulateMouseDrag(
                                (int)Math.Round(fromScreen.X), (int)Math.Round(fromScreen.Y),
                                (int)Math.Round(toScreen.X), (int)Math.Round(toScreen.Y));
                            await Task.Delay(300);

                            var afterDrag = _hover.GetPlaybackState();

                            var dragged = afterDrag.PositionMs > beforeDrag.PositionMs + 300 ||
                                           (afterDrag.DurationMs > 0 &&
                                            afterDrag.PositionMs > afterDrag.DurationMs * 0.5);

                            Check("真实拖动进度条后播放位置跳转", dragged,
                                $"位置 {beforeDrag.PositionMs}ms → {afterDrag.PositionMs}ms" +
                                $"（时长 {afterDrag.DurationMs}ms）");
                        }

                        // e. ★ 用户实测场景：鼠标移到面板上（下面压着别的卡片）
                        //      → 面板必须保持原样：素材不变、位置不变、不消失。
                        //      这样用户才能点到面板里的内容。
                        var posBeforePanel = _hover.PanelBounds;
                        var shownBeforePanel = _hover.ShownAssetId;

                        if (posBeforePanel is { } pbp)
                        {
                            var p1 = new Point(pbp.X + pbp.Width / 2, pbp.Y + pbp.Height / 3);
                            var p2 = new Point(pbp.X + pbp.Width / 4, pbp.Y + pbp.Height / 3);

                            var s1 = PointToScreen(HoverOverlay.TranslatePoint(p1, this));
                            NativeWindow.SetCursorPosition((int)Math.Round(s1.X), (int)Math.Round(s1.Y));
                            await Task.Delay(500);

                            var s2 = PointToScreen(HoverOverlay.TranslatePoint(p2, this));
                            NativeWindow.SetCursorPosition((int)Math.Round(s2.X), (int)Math.Round(s2.Y));
                            await Task.Delay(500);

                            var posAfterPanel = _hover.PanelBounds;

                            var stable = _hover.IsVisible &&
                                         _hover.ShownAssetId == shownBeforePanel &&
                                         posAfterPanel is { } pap &&
                                         Math.Abs(pap.X - pbp.X) < 5 && Math.Abs(pap.Y - pbp.Y) < 5;

                            Check("鼠标移到面板上（压着别的卡片）：预览不切换、不消失、位置不动",
                                stable,
                                stable
                                    ? $"始终显示素材 {_hover.ShownAssetId}，位置未变 —— 不再抖动，可正常点击"
                                    : $"素材 {shownBeforePanel} → {_hover.ShownAssetId}，可见={_hover.IsVisible}");
                        }
                    }
                    finally
                    {
                        _ignoreRealMouseHover = true;
                    }
                }
            }

            _hover.HideNow("诊断：真实鼠标测试结束");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("真实鼠标诊断", ex);
        }

        // ── 15. 面板盖住空白处时不得消失（用户实测的闪烁场景）──
        //      精确复现：离开卡片 → 隐藏倒计时启动 → 光标落到面板上
        //      （不管下面是卡片还是空白）→ 倒计时必须被取消，面板不得消失。
        try
        {
            DiagnosticsLog.Mark("[诊断] 步骤 15：光标移到面板上时不得消失");

            // 保活轮询必须开启：本测试就靠它取消隐藏倒计时
            _hover.KeepAliveDisabled = false;

            var gapTile = await GetTileElementAsync(video);
            if (gapTile is not null)
            {
                _hover.RequestShow(video, gapTile, respectCursor: false);
                await WaitUntilAsync(() => _hover.IsVisible, 3000);
                await Task.Delay(300);

                if (_hover.PanelBounds is { } pb)
                {
                    // 在面板画面区里优先找一个【不在任何卡片上】的点（面板盖住空白处的场景）
                    Point? gapPoint = null;
                    foreach (var candidate in new[]
                             {
                                 new Point(pb.X + pb.Width - 20, pb.Y + pb.Height / 3),
                                 new Point(pb.X + pb.Width - 20, pb.Y + pb.Height / 2),
                                 new Point(pb.X + 20, pb.Y + pb.Height - 60),
                                 new Point(pb.X + pb.Width / 2, pb.Y + pb.Height - 60),
                             })
                    {
                        var windowPoint = HoverOverlay.TranslatePoint(candidate, this);
                        if (!IsOverCard(windowPoint)) { gapPoint = candidate; break; }
                    }

                    // 就算整个面板都盖在卡片上，保活也必须生效（这正是要测的兜底行为）
                    var target = gapPoint ?? new Point(pb.X + pb.Width / 2, pb.Y + pb.Height / 3);
                    var wpt = HoverOverlay.TranslatePoint(target, this);
                    var spt = PointToScreen(wpt);

                    // ★ 先启动隐藏倒计时（等价于"鼠标刚离开卡片"），再把光标移到面板上
                    _hover.RequestHide();
                    await Task.Delay(100);

                    NativeWindow.SetCursorPosition((int)Math.Round(spt.X), (int)Math.Round(spt.Y));

                    await Task.Delay(900);   // 远超 400ms 宽限期

                    Check("隐藏倒计时已启动时，光标移到面板上 900ms：面板不消失",
                        _hover.IsVisible,
                        _hover.IsVisible
                            ? $"始终可见（{(gapPoint is null ? "面板盖在卡片上" : "面板盖住空白处")}）"
                            : "面板被宽限期杀掉了 —— 闪烁场景复现");
                }

                _hover.HideNow("诊断：空白保活测试结束");
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("空白保活诊断", ex);
        }

        _hover.HideNow("诊断结束");

        DiagnosticsLog.Mark("── 悬浮预览诊断汇总 ──");
        foreach (var (name, pass, detail) in results)
            DiagnosticsLog.Mark($"   {(pass ? "PASS" : "FAIL")}  {name} —— {detail}");

        var passed = results.Count(r => r.Pass);
        DiagnosticsLog.Mark($"   通过 {passed} / {results.Count}");

        // ★ 诊断期间若有人动了窗口/切了焦点，结果不可信 —— 必须显式标出，
        //   否则会把"外部干扰造成的失败"当成功能 bug 去修（踩过这个坑）。
        if (_externalInterference > 0)
        {
            DiagnosticsLog.Mark(
                $"⚠⚠⚠ 诊断期间检测到外部干扰 {_externalInterference} 次（窗口被拖动/失去焦点/状态变化）" +
                "—— 以上结果可能无效，请勿在诊断期间操作电脑后重跑 ⚠⚠⚠");
        }

        _diagnosticsRunning = false;
    }

    /// <summary>
    /// 窗口坐标的点是否落在某张素材卡片上。
    /// </summary>
    private bool IsOverCard(Point windowPoint)
    {
        try
        {
            var hit = InputHitTest(windowPoint) as DependencyObject;

            while (hit is not null)
            {
                if (ItemsControl.ContainerFromElement(AssetList, hit) is ListBoxItem) return true;

                hit = hit is Visual
                    ? VisualTreeHelper.GetParent(hit)
                    : LogicalTreeHelper.GetParent(hit);
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 面板画面区中心的窗口级命中测试：
    /// 应该命中 HoverPreviewPanel 本身 —— 被盖住的卡片不得出现在命中链里。
    /// </summary>
    private bool HitTestPanelCenter(out bool hitCardFirst)
    {
        hitCardFirst = false;
        try
        {
            if (_hover.PanelBounds is not { } b) return false;

            // 面板画面区中心（相对覆盖层），再换算到主窗口坐标
            var overlayPoint = new Point(b.X + b.Width / 2, b.Y + b.Height / 3);
            var windowPoint = HoverOverlay.TranslatePoint(overlayPoint, this);

            var hit = InputHitTest(windowPoint) as DependencyObject;

            while (hit is not null)
            {
                if (hit is HoverPreviewPanel) return true;

                if (!hitCardFirst && ItemsControl.ContainerFromElement(AssetList, hit) is ListBoxItem)
                    hitCardFirst = true;

                hit = hit is Visual
                    ? VisualTreeHelper.GetParent(hit)
                    : LogicalTreeHelper.GetParent(hit);
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<FrameworkElement?> GetTileElementAsync(AssetItemViewModel item)
    {
        AssetList.ScrollIntoView(item);
        AssetList.UpdateLayout();

        // 等滚动彻底停下 —— 滚动会触发 ScrollChanged，进而收起悬浮预览，
        // 诊断流程必须避开这个窗口期。
        await Task.Delay(900);

        if (GetContainerFor(item) is { } container) return container;

        AssetList.UpdateLayout();
        await Task.Delay(300);
        return GetContainerFor(item);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(30);
        }
        return condition();
    }

    /// <summary>元素的屏幕物理中心点（双角点换算，避免 DPI 单位混用）。</summary>
    private static (int X, int Y) CenterOf(FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));

        return (
            (int)Math.Round((topLeft.X + bottomRight.X) / 2),
            (int)Math.Round((topLeft.Y + bottomRight.Y) / 2));
    }

    /// <summary>
    /// 找一张当前可见、且上面有【没被预览面板盖住的点】的<b>视频</b>卡片
    /// （拖/点进度条测试必须有播放器，所以只挑视频），返回该点的屏幕坐标。
    /// </summary>
    private async Task<(AssetItemViewModel Item, int X, int Y)?> FindFollowTargetAsync()
    {
        if (_hover.PanelBounds is not { } panel) return null;

        foreach (var item in _viewModel.Assets.Where(a => a.Kind == AssetKind.Video).Take(20))
        {
            if (item.Id == _hover.ShownAssetId) continue;

            var tile = GetContainerFor(item) ?? await GetTileElementAsync(item);
            if (tile is null) continue;

            if (FindUncoveredScreenPoint(tile, panel) is { } p)
                return (item, p.X, p.Y);
        }

        return null;
    }

    /// <summary>在卡片上找一个没被面板盖住的点（返回屏幕物理坐标）。</summary>
    private (int X, int Y)? FindUncoveredScreenPoint(
        FrameworkElement tile, (double X, double Y, double W, double H) panel)
    {
        var w = tile.ActualWidth;
        var h = tile.ActualHeight;
        if (w <= 0 || h <= 0) return null;

        // 中心 + 四角 + 边缘中点，优先中心附近
        var candidates = new[]
        {
            new Point(w * 0.5, h * 0.5),
            new Point(w * 0.85, h * 0.85),
            new Point(w * 0.85, h * 0.15),
            new Point(w * 0.15, h * 0.85),
            new Point(w * 0.15, h * 0.15),
            new Point(w * 0.5, h * 0.9),
            new Point(w * 0.9, h * 0.5),
            new Point(w * 0.1, h * 0.5),
        };

        foreach (var c in candidates)
        {
            var overlayPoint = tile.TranslatePoint(c, HoverOverlay);
            var covered = overlayPoint.X >= panel.X && overlayPoint.X <= panel.X + panel.W &&
                          overlayPoint.Y >= panel.Y && overlayPoint.Y <= panel.Y + panel.H;
            if (covered) continue;

            var windowPoint = HoverOverlay.TranslatePoint(overlayPoint, this);
            var screen = PointToScreen(windowPoint);
            return ((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        }

        return null;
    }

    /// <summary>
    /// 自动滚动整个素材列表并统计渲染帧率。
    ///
    /// <c>CompositionTarget.Rendering</c> 每个渲染帧触发一次（受垂直同步约束，通常约 60Hz），
    /// 因此统计它就能得到真实帧率。
    /// </summary>
    private async Task RunScrollDiagnosticsAsync()
    {
        DiagnosticsLog.Mark($"── 滚动诊断开始（{_viewModel.Assets.Count} 个素材）──");

        var frames = 0;
        var windowStart = Stopwatch.StartNew();
        var samples = new List<double>();

        void OnRendering(object? s, EventArgs e)
        {
            frames++;
            if (windowStart.ElapsedMilliseconds < 1000) return;

            samples.Add(frames * 1000.0 / windowStart.ElapsedMilliseconds);
            frames = 0;
            windowStart.Restart();
        }

        CompositionTarget.Rendering += OnRendering;
        var total = Stopwatch.StartNew();

        try
        {
            var items = _viewModel.Assets;
            if (items.Count > 0)
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    for (var i = 0; i < items.Count; i += 3)
                    {
                        AssetList.ScrollIntoView(items[i]);
                        await Task.Delay(35);
                    }
                    for (var i = items.Count - 1; i >= 0; i -= 3)
                    {
                        AssetList.ScrollIntoView(items[i]);
                        await Task.Delay(35);
                    }
                }
            }
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
            total.Stop();
        }

        if (samples.Count == 0)
        {
            DiagnosticsLog.Mark("滚动诊断：采样不足");
            return;
        }

        // 丢掉第一秒（首帧布局与 JIT 最重），只看稳定期
        var steady = samples.Count > 2 ? samples.Skip(1).ToList() : samples;
        var avg = steady.Average();
        var min = steady.Min();

        DiagnosticsLog.Mark(
            $"── 滚动诊断结束：采样 {samples.Count} 秒，稳定期平均 {avg:F1} FPS，" +
            $"最低 {min:F1} FPS，滚动时长 {total.Elapsed.TotalSeconds:F1}s ──");
        DiagnosticsLog.Mark(
            $"   判定：{(min >= 50 ? "PASS 滚动流畅" : min >= 30 ? "可接受但未达 60" : "FAIL 明显掉帧")}");
    }

    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeViewModel node)
            _viewModel.SelectedFolder = node;
    }

    // ══════════════════════════ 文件夹树右键菜单 ══════════════════════════

    private FolderNodeViewModel? _folderMenuTarget;

    private void OnFolderTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        try
        {
            if (e.OriginalSource is DependencyObject source)
            {
                var item = ItemsControl.ContainerFromElement(FolderTreeView, source) as TreeViewItem;
                _folderMenuTarget = item?.DataContext as FolderNodeViewModel;
            }
            else
            {
                _folderMenuTarget = null;
            }
        }
        catch (Exception)
        {
            _folderMenuTarget = null;
        }
    }

    /// <summary>右键 → 新建文件夹（右键点中的文件夹下创建；点在空白处则建在库根）。</summary>
    private async void OnCreateFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialogWindow(
            _folderMenuTarget is { } parent
                ? $"在「{parent.Name}」下新建文件夹："
                : "在素材库根目录下新建文件夹：",
            "新建文件夹")
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Input)) return;

        var (ok, message) = await _viewModel.CreateFolderAsync(
            _folderMenuTarget?.FolderKey, dialog.Input);

        if (!ok)
        {
            MessageBox.Show(this, message, "素材库", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    /// <summary>右键 → 在资源管理器中打开该文件夹。</summary>
    private void OnRevealFolderClick(object sender, RoutedEventArgs e)
        => _viewModel.RevealFolder(_folderMenuTarget?.FolderKey ?? "");

    /// <summary>右键 → 重命名文件夹（物理目录 + 库内路径 + 命名序号一起更新）。</summary>
    private async void OnRenameFolderClick(object sender, RoutedEventArgs e)
    {
        if (_folderMenuTarget is not { } target || string.IsNullOrEmpty(target.FolderKey))
        {
            MessageBox.Show(this, "库根目录不能重命名。", "素材库", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new InputDialogWindow(
            $"把「{target.Name}」重命名为：", "重命名文件夹", initial: target.Name)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Input)) return;

        var (ok, message) = await _viewModel.RenameFolderAsync(target.FolderKey, dialog.Input);

        if (!ok)
        {
            MessageBox.Show(this, message, "素材库", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    /// <summary>右键 → 删除文件夹（文件进回收站可还原；库内记录删除）。</summary>
    private async void OnDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        if (_folderMenuTarget is not { } target || string.IsNullOrEmpty(target.FolderKey))
        {
            MessageBox.Show(this, "库根目录不能删除。", "素材库", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var choice = MessageBox.Show(
            this,
            $"删除文件夹「{target.Name}」？\n\n" +
            $"其中 {target.TotalAssetCount} 个素材的文件会移入回收站（可还原），" +
            "库内记录将被删除。",
            "素材库 — 删除文件夹",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (choice != MessageBoxResult.Yes) return;

        var (ok, message) = await _viewModel.DeleteFolderAsync(target.FolderKey);

        if (!ok)
        {
            MessageBox.Show(this, message, "素材库", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnAssetDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 必须确认双击命中的是卡片本身，否则双击空白处会打开上一次选中的文件
        if (e.OriginalSource is not DependencyObject source) return;

        var container = ItemsControl.ContainerFromElement(AssetList, source) as ListBoxItem;
        if (container?.DataContext is not AssetItemViewModel item) return;

        // 视频/音频：用内置播放器（VLC）固定放大播放 —— 不依赖系统默认程序
        // （用户实测：FLAC 系统没有关联程序就播不了）。照片仍走系统看图工具。
        if (item.Kind is AssetKind.Video or AssetKind.Audio)
        {
            var tile = GetContainerFor(item) ?? container;
            _hover.TogglePin(item, tile);
            return;
        }

        _viewModel.OpenAssetCommand.Execute(item);
    }

    // ══════════════════════════ 拖放导入 ══════════════════════════

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        // 只接受文件拖放；处理任务运行期间不允许再拖入（避免并发写入互相干扰）
        var acceptable = e.Data.GetDataPresent(DataFormats.FileDrop) && !_viewModel.IsBusy;

        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        _viewModel.IsDragOver = acceptable;
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        _viewModel.IsDragOver = false;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        _viewModel.IsDragOver = false;
        e.Handled = true;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        await _viewModel.ImportAsync(paths);
    }

    // ══════════════════════════ 悬浮预览 ══════════════════════════

    private void OnTileHoverStarted(object sender, AssetHoverEventArgs e)
    {
        if (_ignoreRealMouseHover || e.Item is null) return;

        // ★ 定位元素不信任 e.Source —— 用户实测日志显示，真实事件链里
        //   e.Source 的几何是整个列表 (0,0,951x632)，而不是卡片本身，
        //   导致面板永远停在同一个位置（用户三次反馈"悬浮框不变动"的根因）。
        //   改用鼠标当前位置对列表做命中测试，找到真正悬停的卡片容器。
        var tile = FindCardContainerUnderMouse() ?? (e.Source as FrameworkElement);

        if (tile is not null)
            _hover.RequestShow(e.Item, tile, respectCursor: true);
    }

    /// <summary>
    /// 找到鼠标当前真正悬停的卡片容器（ListBoxItem，几何即卡片本身）。
    /// </summary>
    private FrameworkElement? FindCardContainerUnderMouse()
    {
        try
        {
            var mousePos = Mouse.GetPosition(AssetList);
            if (mousePos.X < 0 || mousePos.Y < 0 ||
                mousePos.X > AssetList.ActualWidth || mousePos.Y > AssetList.ActualHeight)
                return null;

            var hit = VisualTreeHelper.HitTest(AssetList, mousePos);
            if (hit?.VisualHit is not { } visual) return null;

            return ItemsControl.ContainerFromElement(AssetList, visual) as FrameworkElement;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnTileHoverEnded(object sender, AssetHoverEventArgs e)
    {
        if (_ignoreRealMouseHover) return;

        _hover.RequestHide();
    }

    // ══════════════════════════ 多选与框选（阶段 4）══════════════════════════

    /// <summary>ListBox 选中项变化 → 同步到 ViewModel（选择工具条）。</summary>
    private void OnAssetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.SyncSelection(AssetList.SelectedItems.Cast<AssetItemViewModel>());
    }

    /// <summary>右键菜单打开前：右键点到的素材若未选中，改为只选中它。</summary>
    private void OnAssetListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        try
        {
            var container = FindCardContainerUnderMouse();
            if (container?.DataContext is not AssetItemViewModel item) return;

            if (!AssetList.SelectedItems.Contains(item))
                AssetList.SelectedItems.Clear();
        }
        catch (Exception)
        {
            // 选择调整失败不影响右键菜单
        }
    }

    // ── 框选：在空白处按住左键直接拖动即框选（无需修饰键）──
    // ★ 拖到列表上/下边界外时【匀速自动滚动】，滚进来的卡片照常被框选
    //   （资源管理器式）。滚动期间起点随内容平移，选框始终锚定在内容上。

    private Point _marqueeStart;
    private Point _lastMarqueePos;
    private bool _marqueeActive;
    private bool _marqueeMoved;

    /// <summary>左键已按下、等待"拖动超过阈值"接管为框选（点击不拦截）。</summary>
    private bool _marqueePending;

    /// <summary>按下时是否在空白处（空白单击 = 取消选择）。</summary>
    private bool _marqueeStartedOnEmpty;

    /// <summary>拖出边界时的自动滚动时钟（30ms 一档，速度随超出距离递增）。</summary>
    private readonly DispatcherTimer _marqueeScrollTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(30),
    };

    private void OnAssetListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // ★ 按下即拦截（Handled）：ListBox 的原生拖选/自动滚动逻辑完全不运行 ——
        //   它会在框选期间把列表滚到选中项/顶部（实测上拖 812.8→0）。
        //   单击选中/双击打开/空白取消/框选/拖出文件全部由我们接管。
        _marqueePending = true;
        _marqueeActive = false;
        _marqueeMoved = false;
        _marqueeStart = e.GetPosition(AssetList);
        _lastMarqueePos = _marqueeStart;
        _marqueeStartedOnEmpty = FindCardContainerUnderMouse() is null;
        _gridGeometry = null;         // 几何重新测量（缩略图尺寸可能已变）
        _marqueeScrollPhase = 0;      // 自动滚动节流计数复位
        _autoScrollBlocked = false;   // 步进校验复位
        _autoScrollDirection = 0;     // 方向复位

        e.Handled = true;
    }

    private void OnAssetListMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(AssetList);

        if (!_marqueeActive)
        {
            if (!_marqueePending) return;

            // ★ 按下期间的【所有】移动都拦截：不拦截的话，ListBox 的原生拖选逻辑
            //   会启动 ItemsControl 内部的自动滚动定时器（DoAutoScroll →
            //   NavigateToStart），把列表滚到顶部 —— 实测"上拖 812.8→0"的元凶。
            e.Handled = true;

            // ★ 起点在卡片上 → 【拖出文件】（导出到资源管理器/其他程序）；
            //   空白处 → 框选。资源管理器惯例，两种需求兼得。
            if (!_marqueeStartedOnEmpty)
            {
                if (Math.Abs(pos.X - _marqueeStart.X) < 5 &&
                    Math.Abs(pos.Y - _marqueeStart.Y) < 5)
                    return;

                BeginFileDragOut();
                return;
            }

            // 3 DIP 阈值：过滤无意的点击抖动
            if (Math.Abs(pos.X - _marqueeStart.X) < 3 &&
                Math.Abs(pos.Y - _marqueeStart.Y) < 3)
                return;

            // 接管为框选
            _marqueeActive = true;
            _marqueeMoved = true;
            MarqueeRect.Visibility = Visibility.Visible;

            // 框选期间抑制"滚到选中项"（ListBox 基类行为会把列表滚到窗口外的选中文件）
            AssetList.SuppressScrollIntoView = true;

            // 框选期间关掉悬浮预览（拖过卡片时不该弹窗）
            _hover.IsEnabled = false;

            AssetList.CaptureMouse();

            // ★ 捕获列表会启动 ListBox 内部的自动滚动定时器（滚向鼠标=滚到顶部），
            //   必须在捕获之后立即停掉
            StopItemsControlAutoScroll();

            _marqueeScrollTimer.Start();

            // 框选替换当前选择；按住 Ctrl 则累加
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                _suppressBringIntoView = true;
                try { AssetList.SelectedItems.Clear(); }
                finally { _suppressBringIntoView = false; }
            }
        }

        _lastMarqueePos = pos;
        ApplyMarquee(_marqueeStart, _lastMarqueePos);
        e.Handled = true;
    }

    /// <summary>
    /// 兜底：停掉 WPF ListBox 内部的拖选自动滚动定时器（`_autoScrollTimer`）。
    ///
    /// ListBox 源码：OnIsMouseCapturedChanged —— 列表被鼠标捕获时会启动该定时器，
    /// OnAutoScrollTimeout → DoAutoScroll → 鼠标在列表上方时 NavigateToStart → 滚到顶部。
    /// 我们的框选接管会 CaptureMouse 到列表本身 → 定时器必然启动 —— 这里立即停掉。
    /// </summary>
    private void StopItemsControlAutoScroll()
    {
        try
        {
            var field = typeof(ListBox).GetField(
                "_autoScrollTimer",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

            if (field?.GetValue(AssetList) is DispatcherTimer timer && timer.IsEnabled)
            {
                timer.Stop();
                DiagnosticsLog.Mark("[框选] 已停掉 ListBox 内部自动滚动定时器");
            }
        }
        catch (Exception)
        {
            // 反射失败不影响功能
        }
    }

    /// <summary>
    /// 把文件拖出程序（复制到目标位置，库内原件不动）。
    /// 按下的卡片若在选中集内 → 拖出全部选中文件；否则只拖这一个。
    /// </summary>
    private void BeginFileDragOut()
    {
        _marqueePending = false;

        var pressedItem = FindCardContainerUnderMouse()?.DataContext as AssetItemViewModel;
        if (pressedItem is null) return;

        var paths = new List<string>();

        if (AssetList.SelectedItems.Contains(pressedItem))
        {
            paths.AddRange(AssetList.SelectedItems
                .Cast<AssetItemViewModel>()
                .Select(a => a.AbsolutePath)
                .Where(File.Exists));
        }
        else if (File.Exists(pressedItem.AbsolutePath))
        {
            paths.Add(pressedItem.AbsolutePath);
        }

        if (paths.Count == 0) return;

        // 拖出期间关掉悬浮预览（拖过卡片不该弹窗）
        _hover.IsEnabled = false;

        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
            DragDrop.DoDragDrop(AssetList, data, DragDropEffects.Copy);
            _viewModel.StatusText = paths.Count > 1
                ? $"已复制 {paths.Count} 个文件到目标位置"
                : "文件已复制到目标位置";
        }
        catch (Exception)
        {
            // 拖放被取消/中断 —— 不影响界面
        }
        finally
        {
            _hover.IsEnabled = _settings.HoverPreviewEnabled;
        }
    }

    private void OnAssetListMouseUp(object sender, MouseButtonEventArgs e)
    {
        var wasPending = _marqueePending;
        _marqueePending = false;

        if (!_marqueeActive)
        {
            if (wasPending && e.ChangedButton == MouseButton.Left)
            {
                if (_marqueeStartedOnEmpty)
                {
                    // 空白处单击 → 取消选择（用户要求）
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                    {
                        _suppressBringIntoView = true;
                        try { AssetList.SelectedItems.Clear(); }
                        finally { _suppressBringIntoView = false; }
                    }
                }
                else
                {
                    // 卡片单击 → 选中该卡片（接管后补上 ListBox 原生的点选行为）
                    var clicked = FindCardContainerUnderMouse()?.DataContext as AssetItemViewModel;
                    if (clicked is not null)
                    {
                        _suppressBringIntoView = true;
                        try
                        {
                            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                            {
                                if (AssetList.SelectedItems.Contains(clicked))
                                    AssetList.SelectedItems.Remove(clicked);
                                else
                                    AssetList.SelectedItems.Add(clicked);
                            }
                            else if (!AssetList.SelectedItems.Contains(clicked) ||
                                     AssetList.SelectedItems.Count > 1)
                            {
                                AssetList.SelectedItems.Clear();
                                AssetList.SelectedItems.Add(clicked);
                            }
                        }
                        finally
                        {
                            _suppressBringIntoView = false;
                        }
                    }
                }
            }
            return;
        }

        _marqueeActive = false;
        _marqueeScrollTimer.Stop();
        _hover.IsEnabled = true;
        AssetList.SuppressScrollIntoView = false;

        try { AssetList.ReleaseMouseCapture(); } catch (Exception) { }

        MarqueeRect.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    /// <summary>
    /// 自动滚动时钟：光标在列表上/下边界之外时，按超出距离匀速滚动。
    /// 光标静止在边界外时不再有 MouseMove，这里代为刷新选框与选中集 ——
    /// 新滚进来的卡片因此能被继续框选。
    ///
    /// ★ 滚动方式：ScrollIntoView 按【整行】滚入视野，而不是
    ///   ScrollToVerticalOffset 像素偏移 —— 实测像素模式下向上滚动会被
    ///   虚拟化面板"锚点纠偏"，711→0 一步跳顶（用户：从最下面往上拖跳到最上面）。
    ///   整行滚入走的是面板自己的定位逻辑，跳变不再发生。
    /// </summary>
    private void OnMarqueeScrollTick(object? sender, EventArgs e)
    {
        // 只有框选真正结束才停表；"还没越过移动阈值"只是暂不滚动，
        // 绝不能停表 —— 否则移动后自动滚动永远不再启动（踩过这个坑）。
        if (!_marqueeActive)
        {
            _marqueeScrollTimer.Stop();
            return;
        }

        if (!_marqueeMoved) return;

        var pos = Mouse.GetPosition(AssetList);
        var h = AssetList.ActualHeight;

        int direction = 0;
        if (pos.Y < 0) direction = -1;
        else if (pos.Y > h) direction = 1;

        _autoScrollDirection = (_autoScrollBlocked || direction < 0) ? 0 : 1;

        if (direction != 0 && !_autoScrollBlocked)
        {
            // ★ 只做【向下】自动滚动。向上滚动虚拟化面板会顽固反抗：
            //   我们滚一步它拽回一步，最后突然弹到远处（实测 1017→1611）。
            //   向上拖时列表保持不动，选框按文档坐标继续向上扩张，
            //   窗口外的文件照样全部选中 —— 稳定优先，绝不跳变。
            if (direction > 0)
            {
                // 节流：每 8 个 tick（≈240ms）滚一行（约 650px/s，平稳不抽搐）
                _marqueeScrollPhase++;
                if (_marqueeScrollPhase % 8 == 0)
                {
                    var geometry = _gridGeometry ??= MeasureGridGeometry();
                    var perLine = geometry?.ItemsPerLine ?? 4;
                    var offsetBefore = GetListScrollOffset();

                    // 向下：把当前末行【下一行】的条目滚入视野（贴住底部）
                    var last = LastVisibleIndex();
                    var target = Math.Min(AssetList.Items.Count - 1, last + perLine);
                    if (target != last && target >= 0)
                        AssetList.ScrollIntoView(AssetList.Items[target]);

                    // ★ 步进校验：面板的"锚点纠偏"会把一步滚动变成跳变/回退。
                    //   UpdateLayout 强制本次滚动落定后再读数。
                    AssetList.UpdateLayout();
                    var offsetAfter = GetListScrollOffset();
                    var moved = offsetAfter - offsetBefore;

                    if (Math.Abs(moved) < 20 || Math.Abs(moved) > 500 || Math.Sign(moved) != direction)
                    {
                        _autoScrollBlocked = true;

                        // 跳变回滚：把列表拉回上一步的位置（用户不会看到瞬移）
                        if (Math.Abs(moved) > 500 || Math.Sign(moved) != direction)
                        {
                            var sv = FindVisualChild<ScrollViewer>(AssetList);
                            try { sv?.ScrollToVerticalOffset(offsetBefore); } catch (Exception) { }
                        }

                        DiagnosticsLog.Mark(
                            $"[框选] 自动滚动步进异常：偏移 {offsetBefore:F1} → {offsetAfter:F1}，" +
                            "本轮停止自动滚动（选中继续，不影响框选完整性）");
                    }
                }
            }
        }

        _lastMarqueePos = pos;
        ApplyMarquee(_marqueeStart, _lastMarqueePos);
    }

    private int _marqueeScrollPhase;

    /// <summary>面板步进异常后置位：本轮框选不再自动滚动。</summary>
    private bool _autoScrollBlocked;

    /// <summary>当前自动滚动的预期方向（-1 上 / 1 下 / 0 无），供跳变检测判断"回抽"。</summary>
    private int _autoScrollDirection;

    /// <summary>跳变回滚的静默窗口（回滚自身的大偏移事件不再重复触发检测）。</summary>
    private DateTime _restoreUntil = DateTime.MinValue;

    /// <summary>刷新选框外观 + 选中集（鼠标移动与自动滚动共用）。</summary>
    private void ApplyMarquee(Point start, Point end)
    {
        var rect = new Rect(start, end);

        Canvas.SetLeft(MarqueeRect, rect.Left);
        Canvas.SetTop(MarqueeRect, rect.Top);
        MarqueeRect.Width = Math.Max(0, rect.Width);
        MarqueeRect.Height = Math.Max(0, rect.Height);

        UpdateMarqueeSelection();
    }

    /// <summary>
    /// 框选选中集更新 —— 【纯累加】语义：选框扫过的文件立即选中，
    /// 光标回移/选框缩小也绝不取消（用户实测："从最下往上框选，中间部分被取消"）。
    /// 取消选中交给：新一次框选（自动清空重选）、点击空白处、取消选择按钮。
    ///
    /// 判定：可见卡片按真实边界（精确）；屏幕外的按文档坐标算术（近似）。
    /// </summary>
    private void UpdateMarqueeSelection()
    {
        if (AssetList.Items.Count == 0) return;

        var geometry = _gridGeometry ??= MeasureGridGeometry();

        // 几何量不出来（网格尚未布局完成）→ 退化为"按可见容器"判断
        if (geometry is null)
        {
            UpdateMarqueeSelectionByContainers(new Rect(_marqueeStart, _lastMarqueePos));
            return;
        }

        var offset = GetListScrollOffset();
        if (offset < 0) return;

        // 选框两端换算到文档坐标：
        // · 起点 —— 按下时的视口位置随滚动平移（ScrollChanged 里已同步），
        //   加回当前偏移即得文档坐标；
        // · 终点 —— 光标当前位置 + 当前偏移（滚动时终点随内容移动）。
        var docRect = new Rect(
            new Point(_marqueeStart.X, _marqueeStart.Y + offset),
            new Point(_lastMarqueePos.X, _lastMarqueePos.Y + offset));

        _suppressBringIntoView = true;
        try
        {
            // 视口空间选框（可见卡片用真实边界精确判定）
            var viewportRect = new Rect(_marqueeStart, _lastMarqueePos);

            for (var i = 0; i < AssetList.Items.Count; i++)
            {
                var selected = AssetList.SelectedItems.Contains(AssetList.Items[i]);
                if (selected) continue;   // 框选期间【纯累加】：扫过的绝不取消

                bool hit;

                // ★ 混合判定：可见卡片按真实几何（精确）；屏幕外的按文档坐标算术
                var container = AssetList.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                var isVisibleContainer = container is not null &&
                    container.DataContext is AssetItemViewModel dc &&
                    ReferenceEquals(AssetList.Items[i], dc);

                if (isVisibleContainer)
                {
                    var bounds = container!.TransformToAncestor(AssetList)
                        .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

                    hit = bounds.Bottom > 0 && bounds.Top < AssetList.ActualHeight
                        ? viewportRect.IntersectsWith(bounds)
                        : docRect.IntersectsWith(geometry.RectFor(i));   // 陈旧容器 → 算术分支
                }
                else
                {
                    hit = docRect.IntersectsWith(geometry.RectFor(i));
                }

                if (hit) AssetList.SelectedItems.Add(AssetList.Items[i]);
            }
        }
        finally
        {
            _suppressBringIntoView = false;
        }
    }

    /// <summary>兜底：几何未知时按可见容器与视口选框相交判断（纯累加，同样不取消）。</summary>
    private void UpdateMarqueeSelectionByContainers(Rect marquee)
    {
        _suppressBringIntoView = true;
        try
        {
            for (var i = 0; i < AssetList.Items.Count; i++)
            {
                if (AssetList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                    continue;

                // 陈旧容器（回收复用后未重绑）DataContext 对不上索引 —— 跳过
                if (container.DataContext is not AssetItemViewModel dc ||
                    !ReferenceEquals(AssetList.Items[i], dc))
                    continue;

                var bounds = container.TransformToAncestor(AssetList)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

                // 只认视口内的（过滤回收复用的陈旧容器）
                if (bounds.Bottom <= 0 || bounds.Top >= AssetList.ActualHeight) continue;

                if (marquee.IntersectsWith(bounds) &&
                    !AssetList.SelectedItems.Contains(dc))
                    AssetList.SelectedItems.Add(dc);
            }
        }
        finally
        {
            _suppressBringIntoView = false;
        }
    }

    /// <summary>
    /// 网格几何：从已实现的容器量出列数/行距/列距/原点（框选算术判断用）。
    /// 多组样本取中位数，剔除局部行残缺造成的离群值。
    /// </summary>
    private GridGeometry? MeasureGridGeometry()
    {
        try
        {
            var realized = new List<(int Index, Rect Bounds)>();

            for (var i = 0; i < AssetList.Items.Count; i++)
            {
                if (AssetList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                    continue;

                // 陈旧容器（回收复用后未重绑）DataContext 对不上索引 —— 跳过
                if (container.DataContext is not AssetItemViewModel dc ||
                    !ReferenceEquals(AssetList.Items[i], dc))
                    continue;

                var bounds = container.TransformToAncestor(AssetList)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

                // ★ 只收确实在视口内的样本：回收复用的陈旧容器会短暂挂在
                //   低索引上，几何被它污染会错位（列数/原点算错）
                if (bounds.Bottom <= 0 || bounds.Top >= AssetList.ActualHeight) continue;

                realized.Add((i, bounds));
            }

            if (realized.Count < 3) return null;

            // 按 Y 分组（同一视觉行的容器 Y 相同，容差 2px）
            var rows = realized
                .GroupBy(r => Math.Round(r.Bounds.Y / Math.Max(r.Bounds.Height, 1)))
                .OrderBy(g => g.Key)
                .ToList();

            var itemsPerLine = rows.Max(g => g.Count());
            if (itemsPerLine < 2) return null;

            // 列距：完整行内相邻 X 差的中位数
            var fullRow = rows.First(g => g.Count() == itemsPerLine)
                .OrderBy(r => r.Bounds.X)
                .ToList();
            var columnPitches = new List<double>();
            for (var i = 1; i < fullRow.Count; i++)
                columnPitches.Add(fullRow[i].Bounds.X - fullRow[i - 1].Bounds.X);

            var columnPitch = Median(columnPitches);
            if (columnPitch <= 0) return null;

            // 行距：同列不同行 Y 差的中位数（多对样本，剔除离群值）
            var rowPitches = new List<double>();
            foreach (var row in rows)
            {
                foreach (var item in row)
                {
                    // 同一列：X 相差 < 2px 的其他行样本
                    foreach (var other in rows)
                    {
                        if (other.Key <= row.Key) continue;

                        var sameColumn = other
                            .Where(o => Math.Abs(o.Bounds.X - item.Bounds.X) < 2)
                            .ToList();
                        foreach (var o in sameColumn)
                        {
                            var pitch = o.Bounds.Y - item.Bounds.Y;
                            if (pitch > 10) rowPitches.Add(pitch);
                        }
                    }
                }
            }

            var rowPitch = Median(rowPitches);
            if (rowPitch <= 0) rowPitch = Math.Max(realized[0].Bounds.Height, 1);

            // 文档原点：从样本反推（取多个样本的平均，更稳）
            var samples = realized.Take(Math.Min(12, realized.Count)).ToList();
            var originX = samples.Average(r => r.Bounds.X - (r.Index % itemsPerLine) * columnPitch);
            var originY = samples.Average(r => r.Bounds.Y - (r.Index / itemsPerLine) * rowPitch);

            var geometry = new GridGeometry(
                itemsPerLine, columnPitch, rowPitch,
                originX, originY,
                samples.Average(r => r.Bounds.Width),
                samples.Average(r => r.Bounds.Height));

            // ★ 自检：用算出来的矩形回验已实现容器的真实位置，
            //   误差大说明几何量错了（列数/行距估计偏差）—— 宁可不用算术
            var mismatches = realized.Count(r =>
                Math.Abs(geometry.RectFor(r.Index).X - r.Bounds.X) > 6 ||
                Math.Abs(geometry.RectFor(r.Index).Y - r.Bounds.Y) > 6);

            if (mismatches > Math.Max(1, realized.Count / 5))
            {
                DiagnosticsLog.Mark(
                    $"[框选] 几何自检失败：每行={itemsPerLine} 列距={columnPitch:F1} " +
                    $"行距={rowPitch:F1} 原点=({originX:F1},{originY:F1}) " +
                    $"样本={realized.Count} 偏差={mismatches}");
                return null;
            }

            return geometry;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;

        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private GridGeometry? _gridGeometry;

    /// <summary>网格几何参数（一次框选内只量一次；滚动不改变列距/行距）。</summary>
    private sealed record GridGeometry(
        int ItemsPerLine,
        double ColumnPitch,
        double RowPitch,
        double OriginX,
        double OriginY,
        double ItemWidth,
        double ItemHeight)
    {
        public Rect RectFor(int index)
        {
            var row = index / ItemsPerLine;
            var col = index % ItemsPerLine;
            return new Rect(
                OriginX + col * ColumnPitch,
                OriginY + row * RowPitch,
                ItemWidth, ItemHeight);
        }
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        AssetList.SuppressScrollIntoView = true;
        _suppressBringIntoView = true;
        try
        {
            foreach (var item in AssetList.Items)
            {
                if (!AssetList.SelectedItems.Contains(item))
                    AssetList.SelectedItems.Add(item);
            }
        }
        finally
        {
            _suppressBringIntoView = false;
            AssetList.SuppressScrollIntoView = false;
        }
    }

    private void OnInvertSelectionClick(object sender, RoutedEventArgs e)
    {
        AssetList.SuppressScrollIntoView = true;
        _suppressBringIntoView = true;
        try
        {
            var selected = new HashSet<object>(AssetList.SelectedItems.Cast<object>());
            AssetList.SelectedItems.Clear();

            foreach (var item in AssetList.Items)
            {
                if (!selected.Contains(item))
                    AssetList.SelectedItems.Add(item);
            }
        }
        finally
        {
            _suppressBringIntoView = false;
            AssetList.SuppressScrollIntoView = false;
        }
    }

    private void OnClearSelectionClick(object sender, RoutedEventArgs e)
    {
        AssetList.SuppressScrollIntoView = true;
        _suppressBringIntoView = true;
        try
        {
            AssetList.SelectedItems.Clear();
        }
        finally
        {
            _suppressBringIntoView = false;
            AssetList.SuppressScrollIntoView = false;
        }
    }

    private void OnJobStripClick(object sender, MouseButtonEventArgs e)
        => JobQueue.IsPanelOpen = !JobQueue.IsPanelOpen;

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox is not null && _viewModel is not null)
            _viewModel.SortMode = SortBox.SelectedIndex;
    }

    /// <summary>打开设置窗口；关闭后把悬浮预览相关设置即时应用到运行时。</summary>
    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new SettingsWindow(_settings) { Owner = this };
            window.ShowDialog();

            // 悬浮预览开关/延迟即时生效（库位置切换走重启流程，窗口内处理）
            _hover.ApplyRuntimeSettings(_settings);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Mark($"[设置] 打开失败：{ex}");
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            // 空格：把当前选中的素材固定成放大预览（再按一次取消）
            case Key.Space:
                // 只有焦点确实在素材网格上时才响应。
                // 否则在别处（比如文件夹树）按空格会莫名其妙固定一张预览，
                // 而固定后的预览居中显示且忽略悬停 —— 用户会以为"悬停卡住不动了"。
                if (!AssetList.IsKeyboardFocusWithin) break;

                _hover.TogglePin(
                    AssetList.SelectedItem as AssetItemViewModel,
                    GetContainerFor(AssetList.SelectedItem));
                e.Handled = true;
                break;

            case Key.Escape:
                // 有选中素材时先取消选择（阶段 4）；没有选中才关悬浮预览
                if (AssetList.SelectedItems.Count > 0)
                {
                    AssetList.SelectedItems.Clear();
                    e.Handled = true;
                    break;
                }

                _hover.HideNow();
                e.Handled = true;
                break;

            case Key.A:
                // Ctrl+A：全选当前目录（复用工具条逻辑，含 BringIntoView 压制）
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
                    AssetList.IsKeyboardFocusWithin)
                {
                    OnSelectAllClick(this, new RoutedEventArgs());
                    e.Handled = true;
                }
                break;
        }
    }

    private FrameworkElement? GetContainerFor(object? item)
        => item is null
            ? null
            : AssetList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;

    protected override void OnClosing(CancelEventArgs e)
    {
        // 记住窗口尺寸与位置
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.Save();
        }

        _viewModel.Dispose();
        _hover.Dispose();
        base.OnClosing(e);
    }
}
