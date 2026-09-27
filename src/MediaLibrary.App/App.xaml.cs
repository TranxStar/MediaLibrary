using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MediaLibrary.App.Services;
using MediaLibrary.App.ViewModels;
using MediaLibrary.App.Views;
using MediaLibrary.Core;
using MediaLibrary.Core.Abstractions;
using MediaLibrary.Data;
using MediaLibrary.Data.Repositories;
using MediaLibrary.Data.Services;
using MediaLibrary.Media;
using Microsoft.Extensions.DependencyInjection;

namespace MediaLibrary.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// 诊断模式（MEDIALIBRARY_DIAG_* 环境变量）。
    /// 此模式下<b>绝不弹模态对话框</b> —— 无人值守运行时弹窗会永久阻塞进程。
    /// </summary>
    public static bool IsDiagnosticMode =>
        Environment.GetEnvironmentVariable("MEDIALIBRARY_DIAG_HOVER") == "1" ||
        Environment.GetEnvironmentVariable("MEDIALIBRARY_DIAG_SCROLL") == "1" ||
        Environment.GetEnvironmentVariable("MEDIALIBRARY_DIAG_JOBS") == "1" ||
        Environment.GetEnvironmentVariable("MEDIALIBRARY_DIAG_BATCH") == "1" ||
        Environment.GetEnvironmentVariable("MEDIALIBRARY_DIAG_THUMBS") == "1";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DiagnosticsLog.BeginSession();
        DiagnosticsLog.Mark("App.OnStartup 开始");

        SetupExceptionHandling();

        // ── 定位 ffmpeg / ffprobe ──────────────────────────────
        FfmpegLocator.Initialize();
        DiagnosticsLog.Mark($"ffmpeg 定位 → {FfmpegLocator.FfmpegPath ?? "未找到!"}");

        // ── 确定库根目录（首次启动由用户选择）──────────────────
        var settings = AppSettings.Load();
        DiagnosticsLog.Mark($"配置载入 → {settings.LibraryRoot ?? "(未设置)"}");

        var libraryRoot = ResolveLibraryRoot(settings);
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            Shutdown();
            return;
        }

        // ── 组装依赖 ───────────────────────────────────────────
        var services = new ServiceCollection();
        ConfigureServices(services, libraryRoot, settings);
        Services = services.BuildServiceProvider();

        // ── 打开 / 初始化库 ────────────────────────────────────
        try
        {
            var layout = Services.GetRequiredService<LibraryLayout>();

            if (!layout.IsWritable())
            {
                Fatal($"素材库目录不可写：\n{layout.Root}\n\n请换一个位置，或以管理员身份运行。");
                return;
            }

            layout.EnsureCreated();

            // 崩溃恢复：清掉上次中断留下的临时文件
            var orphans = layout.CleanOrphanTempFiles();
            if (orphans > 0) Debug.WriteLine($"[Startup] 清理了 {orphans} 个临时文件");

            var db = Services.GetRequiredService<LibraryDb>();
            db.Initialize();
            DiagnosticsLog.Mark($"数据库就绪 (schema v{db.SchemaVersion}, " +
                                $"{db.GetDatabaseSizeBytes() / 1024.0:F0} KB)");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("打开素材库", ex);
            Fatal($"打开素材库失败：\n\n{ex.Message}");
            return;
        }

        // ── 探测硬件编码可用性 ─────────────────────────────────
        // 必须赶在处理任何转码任务之前完成。
        // 阶段0 实测：ffmpeg 版本与显卡驱动版本任一不匹配，NVENC 就会失败；
        // 不探测的话会静默退化成 CPU 编码（慢约 10 倍但不报错）。
        try
        {
            var transcoder = Services.GetRequiredService<VideoTranscoder>();
            transcoder.ProbeHardwareAsync().GetAwaiter().GetResult();
            DiagnosticsLog.Mark($"编码器探测 → {transcoder.ActiveEncoder}（{transcoder.EncoderDescription}）");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("编码器探测", ex);
        }

        // ── 显示主窗口 ─────────────────────────────────────────
        try
        {
            var window = Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            DiagnosticsLog.Mark("主窗口已显示（界面可交互）");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Error("创建主窗口", ex);
            Fatal($"创建主窗口失败：\n\n{ex}");
            return;
        }

        // ── 预热 VLC 与悬浮窗（放到窗口显示之后，不占用启动时间）──
        // 设计目标是"悬停 ≤400ms 出画面"，这里有两笔一次性开销必须提前付掉：
        //   1. LibVLC 首次创建        ≈ 100ms
        //   2. 悬浮窗首次 Show()      ≈ 1700ms（XAML 解析 + 布局 + VideoView 原生子窗口创建）
        // 不做的话，第一次悬停要 2 秒以上。
        _ = Task.Run(() =>
        {
            try
            {
                var pool = Services.GetRequiredService<VlcPlayerPool>();
                pool.WarmUp();
                DiagnosticsLog.Mark($"VLC 预热完成 → libvlc {pool.Version}");
            }
            catch (Exception ex)
            {
                DiagnosticsLog.Error("VLC 预热", ex);
            }

            // 悬浮窗是 WPF 窗口，必须在 UI 线程上创建
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Services.GetRequiredService<HoverPreviewController>().Preload();
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.Error("悬浮窗预热", ex);
                }
            }, DispatcherPriority.ApplicationIdle);
        });
    }

    /// <summary>
    /// 确定一个<b>可用</b>的库根目录。
    ///
    /// 关键点：素材库可能放在移动硬盘 / U 盘上，拔掉后配置里的路径就失效了。
    /// 这时不能直接报错退出 —— 要给用户重新选择的出路，
    /// 否则每次都只能删配置文件才能启动。
    /// </summary>
    private static string? ResolveLibraryRoot(AppSettings settings)
    {
        // 诊断模式可用环境变量指定库根（不写配置），供批量操作诊断使用临时库
        if (IsDiagnosticMode)
        {
            var envRoot = Environment.GetEnvironmentVariable("MEDIALIBRARY_LIBRARY_ROOT");
            if (!string.IsNullOrWhiteSpace(envRoot))
            {
                try { Directory.CreateDirectory(envRoot); } catch (Exception) { }
                return envRoot;
            }
        }

        var configured = settings.LibraryRoot;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Directory.Exists(configured) && new LibraryLayout(configured).IsWritable())
                return configured;

            // 配置的库不可用：说清楚原因，然后让用户重新选
            var reason = Directory.Exists(configured)
                ? "该目录当前不可写（可能被其他程序占用，或权限不足）。"
                : "该目录不存在。如果素材库在移动硬盘或 U 盘上，请先插好设备再重试。";

            var choice = MessageBox.Show(
                $"打不开上次使用的素材库：\n\n{configured}\n\n{reason}\n\n" +
                "要现在选择另一个素材库位置吗？",
                "素材库 — 找不到库",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (choice != MessageBoxResult.Yes) return null;
        }

        var picker = new LibraryPickerWindow();
        if (picker.ShowDialog() == true && !string.IsNullOrWhiteSpace(picker.SelectedPath))
        {
            settings.RememberLibrary(picker.SelectedPath);
            settings.Save();
            return picker.SelectedPath;
        }

        return null;
    }

    private static void ConfigureServices(IServiceCollection services, string libraryRoot, AppSettings settings)
    {
        var layout = new LibraryLayout(libraryRoot);

        services.AddSingleton(layout);
        services.AddSingleton(settings);

        // ── 数据层 ─────────────────────────────────────────────
        services.AddSingleton(_ => new LibraryDb(layout.DatabasePath));
        services.AddSingleton<AssetRepository>();
        services.AddSingleton<NamingCounterRepository>();
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<JobRepository>();
        services.AddSingleton<LibrarySyncService>();
        services.AddSingleton<JobQueueService>();
        services.AddSingleton<ImportService>();

        // ── 媒体层 ─────────────────────────────────────────────
        services.AddSingleton<IMediaProbe>(_ => new FfprobeService(maxConcurrency: 4));
        services.AddSingleton<IThumbnailer>(_ => new ThumbnailService(maxConcurrency: 4));
        services.AddSingleton<IShellService, ShellService>();

        // 转码 / 转换 / 复制
        services.AddSingleton<VideoTranscoder>();
        services.AddSingleton<IVideoTranscoder>(sp => sp.GetRequiredService<VideoTranscoder>());
        services.AddSingleton<IImageConverter>(_ => new ImageConverter());
        services.AddSingleton<IFileCopier>(_ => new FileCopier());

        services.AddSingleton(sp => new AssetEnrichmentService(
            layout,
            sp.GetRequiredService<AssetRepository>(),
            sp.GetRequiredService<IMediaProbe>(),
            sp.GetRequiredService<IThumbnailer>(),
            concurrency: 4));

        services.AddSingleton(sp => new ImportJobRunner(
            layout,
            sp.GetRequiredService<AssetRepository>(),
            sp.GetRequiredService<JobRepository>(),
            sp.GetRequiredService<JobQueueService>(),
            sp.GetRequiredService<IMediaProbe>(),
            sp.GetRequiredService<IVideoTranscoder>(),
            sp.GetRequiredService<IImageConverter>(),
            sp.GetRequiredService<IFileCopier>()));

        // ── 界面层 ─────────────────────────────────────────────
        services.AddSingleton(_ => new ThumbnailImageCache(capacity: 500, maxConcurrentLoads: 6));

        // VLC 播放池：全局单例。创建 LibVLC 有约 100ms 开销，只付一次。
        // 悬停预览与空格固定预览共用这一个池 —— 物理上不可能同时播放多个。
        services.AddSingleton<VlcPlayerPool>();
        services.AddSingleton<HoverPreviewController>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<JobQueueViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private void SetupExceptionHandling()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticsLog.Error("UI 线程未处理异常", args.Exception);

            // 诊断模式下只记日志：弹窗会卡死无人值守运行
            if (IsDiagnosticMode)
            {
                args.Handled = true;
                return;
            }

            MessageBox.Show(
                $"发生未预期的错误：\n\n{args.Exception.Message}\n\n程序会继续运行。",
                "素材库", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticsLog.Mark($"[致命] 域未处理异常: {args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticsLog.Mark($"[任务] 未观察异常: {args.Exception.InnerException?.Message ?? args.Exception.Message}");
            args.SetObserved();
        };
    }

    private void Fatal(string message)
    {
        DiagnosticsLog.Mark($"[致命] {message}");

        // 诊断模式下绝不弹窗 —— 无人值守时模态框会把进程永久挂住
        if (IsDiagnosticMode)
        {
            Shutdown(1);
            return;
        }

        MessageBox.Show(message, "素材库 — 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            (Services as IDisposable)?.Dispose();
        }
        catch (Exception)
        {
            // 退出阶段的清理失败不重要
        }

        base.OnExit(e);
    }
}
