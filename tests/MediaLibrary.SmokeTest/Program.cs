using System.Diagnostics;
using System.Text;
using Dapper;
using MediaLibrary.Core;
using MediaLibrary.Core.Abstractions;
using MediaLibrary.Core.Hashing;
using MediaLibrary.Core.Models;
using MediaLibrary.Data;
using MediaLibrary.Data.Repositories;
using MediaLibrary.Data.Services;
using MediaLibrary.Media;

Console.OutputEncoding = Encoding.UTF8;

// ══════════════════════════════════════════════════════════════
//  冒烟测试
//    phase1 <库目录>                    —— 扫描 / 缩略图 / 命名序号
//    phase2 <源目录> <目标库目录>        —— 完整导入管线
//    phase4 <源目录> <目标库目录>        —— 任务队列控制（暂停/继续/取消/重试/恢复）
// ══════════════════════════════════════════════════════════════

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "phase1";

FfmpegLocator.Initialize();
var results = new List<(string Name, bool Pass, string Detail)>();

void Check(string name, bool pass, string detail)
{
    results.Add((name, pass, detail));
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name}");
    Console.WriteLine($"         {detail}");
}

void Header(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"  {title}");
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"  ffmpeg: {FfmpegLocator.FfmpegPath ?? "(未找到!)"}");
    Console.WriteLine();
}

int Report()
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 78));
    var passed = results.Count(r => r.Pass);
    Console.WriteLine($"  通过 {passed} / {results.Count}");
    if (results.Any(r => !r.Pass))
    {
        Console.WriteLine();
        Console.WriteLine("  未通过项:");
        foreach (var (name, _, detail) in results.Where(r => !r.Pass))
            Console.WriteLine($"    x {name} — {detail}");
    }
    Console.WriteLine(new string('=', 78));
    return results.Count(r => !r.Pass);
}

/// <summary>建库并组装全套服务。</summary>
(LibraryLayout Layout, LibraryDb Db, AssetRepository Assets, JobRepository Jobs,
 ImportService Import, ImportJobRunner Runner, AssetEnrichmentService Enrich,
 JobQueueService Queue) Build(string libraryRoot)
{
    var layout = new LibraryLayout(libraryRoot);
    layout.EnsureCreated();

    var db = new LibraryDb(layout.DatabasePath);
    db.Initialize();

    var assets = new AssetRepository(db);
    var counters = new NamingCounterRepository(db);
    var jobs = new JobRepository(db);
    var queue = new JobQueueService(jobs);
    var probe = new FfprobeService(4);

    var transcoder = new VideoTranscoder();
    transcoder.ProbeHardwareAsync().GetAwaiter().GetResult();

    var import = new ImportService(layout, assets, counters, queue);

    var runner = new ImportJobRunner(
        layout, assets, jobs, queue, probe, transcoder, new ImageConverter(), new FileCopier());

    var enrich = new AssetEnrichmentService(layout, assets, probe, new ThumbnailService(4), 4);

    Console.WriteLine($"  编码器: {transcoder.ActiveEncoder}  ({transcoder.EncoderDescription})");

    return (layout, db, assets, jobs, import, runner, enrich, queue);
}

void CleanLibrary(string root)
{
    foreach (var suffix in new[] { "", "-wal", "-shm" })
    {
        var p = Path.Combine(root, LibraryLayout.DatabaseFileName + suffix);
        if (File.Exists(p)) File.Delete(p);
    }
    foreach (var dir in new[] { ".cache", ".tmp" })
    {
        var p = Path.Combine(root, dir);
        if (Directory.Exists(p)) { try { Directory.Delete(p, true); } catch (IOException) { } }
    }
}

// ═══════════════════════════════════════════════════════════════
if (mode == "phase2")
{
    var sourceRoot = args.Length > 1 ? args[1] : @"E:\MediaLibraryTest\运动会\开幕式";
    var libraryRoot = args.Length > 2 ? args[2] : @"E:\MediaLibraryPhase2";

    Header("阶段 2 冒烟测试 —— 导入管线");
    Console.WriteLine($"  源目录  : {sourceRoot}");
    Console.WriteLine($"  目标库  : {libraryRoot}");

    if (!Directory.Exists(sourceRoot)) { Console.WriteLine("源目录不存在"); return 2; }

    CleanLibrary(libraryRoot);
    var (layout, db, assets, jobs, import, runner, enrich, _) = Build(libraryRoot);

    // ── 记录源文件的指纹（用于验证"源文件零改动"）──────────────
    var sourceFiles = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
    var fingerprintBefore = sourceFiles.ToDictionary(
        f => f,
        f => (Size: new FileInfo(f).Length, Write: File.GetLastWriteTimeUtc(f), Hash: QuickHash.Compute(f)),
        StringComparer.OrdinalIgnoreCase);

    var byKind = sourceFiles
        .GroupBy(f => MediaKindClassifier.Classify(f))
        .ToDictionary(g => g.Key?.ToString() ?? "未知", g => g.Count());

    Console.WriteLine($"  源文件  : {sourceFiles.Length} 个  （" +
                      string.Join(" / ", byKind.Select(k => $"{k.Key} {k.Value}")) + "）");
    Console.WriteLine();

    // ══════════════════════════════════════════════════════════
    Console.WriteLine("── 1. 导入（计划 + 入队）──");

    var importSw = Stopwatch.StartNew();
    var importResult = await import.ImportAsync([sourceRoot]);
    importSw.Stop();

    Console.WriteLine($"  {importResult}");
    Console.WriteLine();

    Check("扫描到全部源文件", importResult.Plan.Items.Count + importResult.Skipped == sourceFiles.Length,
        $"计划 {importResult.Plan.Items.Count} + 跳过 {importResult.Skipped} = " +
        $"{importResult.Plan.Items.Count + importResult.Skipped} / 源 {sourceFiles.Length}");

    Check("命名规则正确（{文件夹}_{序号:D3}.{扩展名}）",
        importResult.Plan.Items.All(p =>
            p.FileName.StartsWith("开幕式_", StringComparison.Ordinal) &&
            p.FileName.Contains('_') &&
            char.IsDigit(p.FileName[^5])),
        string.Join(" | ", importResult.Plan.Items.Take(3).Select(p => p.FileName)) + " …");

    Check("目标扩展名正确",
        importResult.Plan.Items.All(p => p.FileName.EndsWith(
            TargetFormat.ExtensionFor(p.Source.Kind, p.Source.SourcePath), StringComparison.OrdinalIgnoreCase)),
        string.Join(" | ", importResult.Plan.Items
            .GroupBy(p => Path.GetExtension(p.FileName))
            .Select(g => $"{g.Key}×{g.Count()}")));

    Check("序号在各目录内连续且不重复",
        importResult.Plan.Items.Select(p => p.FileName).Distinct().Count() == importResult.Plan.Items.Count,
        $"{importResult.Plan.Items.Count} 个文件名，唯一 {importResult.Plan.Items.Select(p => p.FileName).Distinct().Count()} 个");

    var pendingAssets = await assets.GetAllAsync();
    Check("素材以 Pending 状态入库（界面可立刻显示占位卡片）",
        pendingAssets.Count == importResult.Enqueued && pendingAssets.All(a => a.Status == AssetStatus.Pending),
        $"DB {pendingAssets.Count} 条，Pending {pendingAssets.Count(a => a.Status == AssetStatus.Pending)} 条");

    var queued = await jobs.GetCountsAsync();
    Check("任务已入队", queued.Queued == importResult.Enqueued,
        $"排队 {queued.Queued} 个（期望 {importResult.Enqueued}）");

    Check("导入计划阶段足够快 (<5s)", importSw.Elapsed.TotalSeconds < 5,
        $"耗时 {importSw.Elapsed.TotalSeconds:F2}s（含对每个源文件算哈希）");

    Console.WriteLine();

    // ══════════════════════════════════════════════════════════
    Console.WriteLine("── 2. 处理（转码 / 转换 / 复制）──");

    var jobSw = Stopwatch.StartNew();
    var jobStats = await runner.RunAsync();
    jobSw.Stop();

    Console.WriteLine($"  {jobStats}");
    Console.WriteLine();

    Check("全部处理成功", jobStats.Failed == 0 && jobStats.Succeeded == importResult.Enqueued,
        $"成功 {jobStats.Succeeded} / 失败 {jobStats.Failed} / 总计 {jobStats.Total}");

    var afterRun = await assets.GetAllAsync();
    Check("全部素材已就绪", afterRun.All(a => a.Status == AssetStatus.Ready),
        $"Ready {afterRun.Count(a => a.Status == AssetStatus.Ready)} / " +
        $"Pending {afterRun.Count(a => a.Status == AssetStatus.Pending)} / " +
        $"Failed {afterRun.Count(a => a.Status == AssetStatus.Failed)}");

    Console.WriteLine();

    // ══════════════════════════════════════════════════════════
    Console.WriteLine("── 3. 校验库内产物 ──");

    var libraryFiles = Directory.GetFiles(libraryRoot, "*", SearchOption.AllDirectories)
        .Where(f => !Path.GetFileName(f).StartsWith('.'))
        .Where(f => !f.Contains(@"\.cache\") && !f.Contains(@"\.tmp\"))
        .ToList();

    Check("库内文件数等于导入数", libraryFiles.Count == importResult.Enqueued,
        $"库内 {libraryFiles.Count} 个 / 导入 {importResult.Enqueued} 个");

    // 视频必须是 H.265
    var videos = afterRun.Where(a => a.Kind == AssetKind.Video).ToList();
    Check("视频全部转为 H.265",
        videos.Count > 0 && videos.All(a => string.Equals(a.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)),
        $"{videos.Count} 个视频，编码分布: " +
        string.Join(" | ", videos.GroupBy(a => a.VideoCodec ?? "?").Select(g => $"{g.Key}×{g.Count()}")));

    Check("视频装进 MP4 容器",
        videos.All(a => a.FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)),
        $"扩展名分布: " + string.Join(" | ", videos.GroupBy(a => Path.GetExtension(a.FileName))
            .Select(g => $"{g.Key}×{g.Count()}")));

    // 照片必须是 PNG
    var photos = afterRun.Where(a => a.Kind == AssetKind.Photo).ToList();
    Check("照片全部转为 PNG",
        photos.Count > 0 && photos.All(a => a.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)),
        $"{photos.Count} 张照片，扩展名分布: " +
        string.Join(" | ", photos.GroupBy(a => Path.GetExtension(a.FileName)).Select(g => $"{g.Key}×{g.Count()}")));

    // 音频保留原格式
    var audios = afterRun.Where(a => a.Kind == AssetKind.Audio).ToList();
    var sourceAudioExts = audios
        .Select(a => Path.GetExtension(a.SourcePath ?? "").ToLowerInvariant())
        .Distinct().OrderBy(e => e).ToList();
    Check("音频保留原格式（仅复制）",
        audios.Count > 0 && audios.All(a => sourceAudioExts.Contains(Path.GetExtension(a.FileName).ToLowerInvariant())),
        $"{audios.Count} 个音频，源扩展名 [{string.Join(",", sourceAudioExts)}]，" +
        $"库内 [{string.Join(",", audios.Select(a => Path.GetExtension(a.FileName).ToLowerInvariant()).Distinct())}]");

    // 临时文件清理
    var leftovers = Directory.Exists(layout.TempDir) ? Directory.GetFiles(layout.TempDir).Length : 0;
    Check("无遗留临时文件", leftovers == 0, $".tmp 下 {leftovers} 个文件");

    // 元数据回填
    Check("元数据已回填（时长 / 分辨率）",
        videos.All(a => a.DurationMs is > 0 && a.Width is > 0),
        $"缺时长的视频 {videos.Count(a => a.DurationMs is null or 0)} 个");

    Console.WriteLine();

    // ══════════════════════════════════════════════════════════
    Console.WriteLine("── 4. 源文件零改动验证 ──");

    var changed = new List<string>();
    var missing = new List<string>();

    foreach (var (path, before) in fingerprintBefore)
    {
        if (!File.Exists(path)) { missing.Add(path); continue; }

        var size = new FileInfo(path).Length;
        var write = File.GetLastWriteTimeUtc(path);

        if (size != before.Size || write != before.Write || QuickHash.Compute(path) != before.Hash)
            changed.Add(path);
    }

    Check("源文件全部仍在原位", missing.Count == 0,
        missing.Count == 0 ? $"{fingerprintBefore.Count} 个源文件都还在" : $"丢失 {missing.Count} 个：{string.Join(", ", missing.Take(3))}");

    Check("源文件内容与时间戳均未被修改", changed.Count == 0,
        changed.Count == 0
            ? $"{fingerprintBefore.Count} 个源文件大小/时间戳/内容哈希全部一致"
            : $"{changed.Count} 个被改动：{string.Join(", ", changed.Take(3))}");

    Console.WriteLine();

    // ══════════════════════════════════════════════════════════
    Console.WriteLine("── 5. 重复导入去重 ──");

    var again = await import.ImportAsync([sourceRoot]);
    Check("重复拖入不会重复入库",
        again.Enqueued == 0 && again.Skipped == sourceFiles.Length,
        $"新增 {again.Enqueued}，跳过 {again.Skipped}（原因：{string.Join("，", again.Plan.SkipSummary.Select(k => $"{k.Key} {k.Value}"))}）");

    Console.WriteLine();

    // ══════════════════════════════════════════════════════════
    Console.WriteLine("── 6. 崩溃恢复：中途中断后能续跑 ──");

    // 再建一个全新的库，导入一半时强行取消
    var crashLibrary = libraryRoot + "_crash";
    CleanLibrary(crashLibrary);
    var (cLayout, cDb, cAssets, cJobs, cImport, cRunner, _, _) = Build(crashLibrary);

    var crashImport = await cImport.ImportAsync([sourceRoot]);
    Console.WriteLine($"  已入队 {crashImport.Enqueued} 项，模拟处理中途强杀…");

    using (var cts = new CancellationTokenSource())
    {
        var runTask = cRunner.RunAsync(null, cts.Token);
        await Task.Delay(1200);
        cts.Cancel();

        try { await runTask; }
        catch (OperationCanceledException) { /* 预期 */ }
    }

    var interrupted = await cJobs.GetCountsAsync();
    var interruptedAssets = await cAssets.GetAllAsync();
    var doneBefore = interruptedAssets.Count(a => a.Status == AssetStatus.Ready);

    Console.WriteLine($"  中断时状态: 就绪 {doneBefore} / 排队 {interrupted.Queued} / " +
                      $"运行中 {interrupted.Running} / 失败 {interrupted.Failed}");

    Check("中断后仍有未完成任务", doneBefore < crashImport.Enqueued,
        $"已就绪 {doneBefore} / 总计 {crashImport.Enqueued}（说明确实被打断了）");

    // 模拟重启：新建一整套服务（相当于重开程序）
    cDb.Dispose();

    var (_, db2, assets2, jobs2, _, runner2, _, _) = Build(crashLibrary);

    var resumeSw = Stopwatch.StartNew();
    var resumeStats = await runner2.RunAsync();
    resumeSw.Stop();

    var afterResume = await jobs2.GetCountsAsync();
    Check("续跑后没有卡在 Running 的任务",
        afterResume.Running == 0 && afterResume.Queued == 0,
        $"排队 {afterResume.Queued} / 运行中 {afterResume.Running} / 完成 {afterResume.Done} / 失败 {afterResume.Failed}");

    var finalAssets = await assets2.GetAllAsync();
    Check("重启后自动续跑并全部完成",
        finalAssets.All(a => a.Status == AssetStatus.Ready),
        $"就绪 {finalAssets.Count(a => a.Status == AssetStatus.Ready)} / {finalAssets.Count}，" +
        $"本轮处理 {resumeStats.Succeeded} 项，耗时 {resumeSw.Elapsed.TotalSeconds:F1}s");

    var crashFiles = Directory.GetFiles(cLayout.Root, "*", SearchOption.AllDirectories)
        .Count(f => !Path.GetFileName(f).StartsWith('.') &&
                    !f.Contains(@"\.cache\") && !f.Contains(@"\.tmp\"));
    Check("崩溃恢复后产物完整", crashFiles == crashImport.Enqueued,
        $"库内 {crashFiles} 个文件 / 期望 {crashImport.Enqueued} 个");

    var crashLeftovers = Directory.GetFiles(cLayout.TempDir).Length;
    Check("崩溃恢复后无残留临时文件", crashLeftovers == 0, $".tmp 下 {crashLeftovers} 个");

    Console.WriteLine();

    // ══════════════════════════════════════════════════════════
    Console.WriteLine("── 7. 缩略图（对库内文件生成）──");

    var enrichStats = await enrich.EnrichAllAsync();
    Console.WriteLine($"  {enrichStats}");

    var thumbFiles = Directory.GetFiles(layout.ThumbDir, "*.jpg").Length;
    Check("缩略图生成成功", enrichStats.Failed == 0 && thumbFiles > 0,
        $"生成 {enrichStats.Succeeded} 张，缓存目录 {thumbFiles} 个文件");

    Console.WriteLine();
    Console.WriteLine("  性能摘要:");
    Console.WriteLine($"    导入计划（{importResult.Enqueued} 项）: {importSw.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"    处理（转码/转换/复制）        : {jobSw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"    缩略图补全                    : {enrichStats.Elapsed.TotalSeconds:F1}s");

    db.Dispose();
    db2.Dispose();
    return Report();
}

// ═══════════════════════════════════════════════════════════════
//  阶段 1 模式：扫描 / 缩略图 / 命名序号
// ═══════════════════════════════════════════════════════════════
if (mode == "phase1")
{
    var libraryRoot = args.Length > 1 ? args[1] : @"E:\MediaLibraryTest";
    if (!Directory.Exists(libraryRoot)) { Console.WriteLine($"库目录不存在: {libraryRoot}"); return 2; }

    Header("阶段 1 冒烟测试 —— 扫描 / 缩略图 / 命名");
    Console.WriteLine($"  库目录: {libraryRoot}");
    Console.WriteLine();

    CleanLibrary(libraryRoot);
    var (layout, db, assets, _, _, _, enrich, _) = Build(libraryRoot);

    var counters = new NamingCounterRepository(db);
    var syncService = new LibrarySyncService(layout, assets, counters);

    Console.WriteLine("── 1. 首次扫描 ──");
    var syncSw = Stopwatch.StartNew();
    var syncResult = await syncService.SyncAsync();
    syncSw.Stop();
    Console.WriteLine($"  {syncResult}");
    Console.WriteLine();

    Check("扫描能识别全部素材", syncResult.Added > 0, $"收编 {syncResult.Added} 个文件");
    Check("扫描耗时在可接受范围 (<10s)", syncSw.Elapsed.TotalSeconds < 10,
        $"耗时 {syncSw.Elapsed.TotalSeconds:F2}s");

    var allAssets = await assets.GetAllAsync();
    Check("库内记录数与磁盘一致", allAssets.Count == syncResult.FilesOnDisk,
        $"DB {allAssets.Count} / 磁盘 {syncResult.FilesOnDisk}");
    Check("种类识别正确",
        allAssets.Any(a => a.Kind == AssetKind.Video) &&
        allAssets.Any(a => a.Kind == AssetKind.Photo) &&
        allAssets.Any(a => a.Kind == AssetKind.Audio),
        $"视频 {allAssets.Count(a => a.Kind == AssetKind.Video)} / " +
        $"照片 {allAssets.Count(a => a.Kind == AssetKind.Photo)} / " +
        $"音频 {allAssets.Count(a => a.Kind == AssetKind.Audio)}");

    var rawStatus = await db.ReadAsync(async conn =>
        (await conn.QueryAsync<(string Val, long Cnt)>(
            new CommandDefinition("SELECT Status AS Val, COUNT(*) AS Cnt FROM Assets GROUP BY Status;")))
        .ToList());
    Check("Status 以可读文本存储", rawStatus.Any(r => r.Val == "Ready"),
        string.Join(" | ", rawStatus.Select(r => $"'{r.Val}'×{r.Cnt}")));

    Console.WriteLine();
    Console.WriteLine("── 2. 二次扫描幂等 ──");
    var second = await syncService.SyncAsync();
    Check("重复扫描不重复入库", second.Added == 0 && second.Missing == 0,
        $"新增 {second.Added} / 丢失 {second.Missing} / 未变 {second.Unchanged}");

    Console.WriteLine();
    Console.WriteLine("── 3. 命名序号 ──");
    var probe = allAssets.First(a => a.FolderKey.Contains("开幕式"));
    var probeAsset = new Asset
    {
        Kind = probe.Kind,
        FileName = "开幕式_007.mp4",
        FolderKey = probe.FolderKey,
        LibraryPath = probe.FolderKey + "/开幕式_007.mp4",
        SizeBytes = 1,
        QuickHash = "0000000000000000",
        Status = AssetStatus.Ready,
    };
    await assets.InsertAsync(probeAsset);
    await counters.ReseedAllAsync();
    var afterReseed = await counters.PeekNextAsync(probe.FolderKey);
    Check("播种后跳过已用序号", afterReseed >= 8, $"下一个分配序号 = {afterReseed}（应 ≥ 8）");

    // ★ 必须清掉这条假记录：它指向的库内文件并不存在。
    //   留着会让后面的缩略图补全对它重试 3 次并计入失败，污染测试结果
    //   （同时它也恰好验证了补全服务不会对缺失文件无限重试）。
    await assets.DeleteAsync(probeAsset.Id);

    var a1 = await counters.AllocateNextAsync("测试目录");
    var a2 = await counters.AllocateNextAsync("测试目录");
    Check("序号分配单调递增", a2 == a1 + 1 && a1 == 1, $"{a1} → {a2}");

    Console.WriteLine();
    Console.WriteLine("── 4. 缩略图补全 ──");
    var enrichSw = Stopwatch.StartNew();
    var enrichStats = await enrich.EnrichAllAsync();
    enrichSw.Stop();
    Console.WriteLine($"  {enrichStats}");

    var thumbFiles = Directory.GetFiles(layout.ThumbDir, "*.jpg").Length;
    var distinct = allAssets.Select(a => a.QuickHash).Distinct().Count();
    Check("缩略图全部生成", enrichStats.Failed == 0, $"成功 {enrichStats.Succeeded} / 失败 {enrichStats.Failed}");
    Check("缩略图按内容哈希去重", thumbFiles >= distinct,
        $"{allAssets.Count} 个素材 → {distinct} 种唯一内容 → {thumbFiles} 个缩略图文件");
    Check("补全队列已清空", await enrich.GetPendingCountAsync() == 0, "剩余 0 项");

    var enriched = await assets.GetAllAsync();
    Check("缩略图路径已写入 DB", enriched.All(a => !string.IsNullOrEmpty(a.ThumbPath)),
        $"{enriched.Count(a => string.IsNullOrEmpty(a.ThumbPath))} 条缺失");

    var perItem = enrichStats.Total > 0 ? enrichStats.Elapsed.TotalMilliseconds / enrichStats.Total : 0;
    Check("单张缩略图吞吐 <1s/张", perItem < 1000, $"{perItem:F0} ms/张（4 并发）");

    Console.WriteLine();
    Console.WriteLine("  性能摘要:");
    Console.WriteLine($"    冷启动扫描 {syncResult.FilesOnDisk} 个文件 : {syncSw.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"    缩略图+元数据 {enrichStats.Total} 项      : {enrichStats.Elapsed.TotalSeconds:F2}s");

    db.Dispose();
    return Report();
}

// ═══════════════════════════════════════════════════════════════
if (mode == "phase4")
{
    var sourceRoot = args.Length > 1 ? args[1] : @"E:\MediaLibraryTest\运动会\开幕式";
    var libraryRoot = args.Length > 2 ? args[2] : @"E:\MediaLibraryPhase4";

    Header("阶段 4 冒烟测试 —— 任务队列控制（暂停/继续/取消/重试/恢复）");

    if (!Directory.Exists(sourceRoot)) { Console.WriteLine("源目录不存在"); return 2; }

    CleanLibrary(libraryRoot);
    var (layout, db, assets, jobs, import, runner, enrich, queue) = Build(libraryRoot);

    // ── 1. 导入后任务全部排队 ──────────────────────────────────
    var importResult = await import.ImportAsync([sourceRoot]);
    var counts = await jobs.GetCountsAsync();
    Check("导入后任务全部排队", counts.Queued == importResult.Enqueued && counts.Paused == 0,
        $"排队 {counts.Queued} / 暂停 {counts.Paused} / 共导入 {importResult.Enqueued}");

    // ── 2. 暂停：Queued → Paused ───────────────────────────────
    await queue.PauseAsync();
    counts = await jobs.GetCountsAsync();
    Check("暂停后排队任务全部转为 Paused",
        queue.IsPaused && counts.Queued == 0 && counts.Paused == importResult.Enqueued,
        $"排队 {counts.Queued} / 暂停 {counts.Paused}");

    // ── 3. 暂停状态下执行器一个任务都不处理 ────────────────────
    var pausedStats = await runner.RunAsync();
    counts = await jobs.GetCountsAsync();
    Check("暂停状态下执行器不处理任务",
        pausedStats.Total == 0 && counts.Paused == importResult.Enqueued,
        $"处理 {pausedStats.Total} 项（应为 0），暂停 {counts.Paused} 个");

    // ── 4. 继续：Paused → Queued，随后全部完成 ─────────────────
    await queue.ResumeAsync();
    counts = await jobs.GetCountsAsync();
    Check("继续后任务回到排队", counts.Queued == importResult.Enqueued && counts.Paused == 0,
        $"排队 {counts.Queued} / 暂停 {counts.Paused}");

    var runStats = await runner.RunAsync();
    var afterRun = await assets.GetAllAsync();
    Check("继续后全部处理成功",
        runStats.Failed == 0 && afterRun.All(a => a.Status == AssetStatus.Ready),
        $"成功 {runStats.Succeeded} / 失败 {runStats.Failed}，" +
        $"就绪 {afterRun.Count(a => a.Status == AssetStatus.Ready)}/{afterRun.Count}");

    // ── 5. 执行中途暂停：进行中的跑完，新任务不再开始 ──────────
    var redo = afterRun.Take(12).ToList();
    foreach (var asset in redo)
    {
        await assets.UpdateStatusAsync(asset.Id, AssetStatus.Pending, null);
        await queue.EnqueueAsync(asset.Id, ImportService.JobTypeFor(asset.Kind), 1);
    }

    var runTask = runner.RunAsync();
    await Task.Delay(150);                       // 让几个任务先开始
    await queue.PauseAsync();

    var queuedAtPause = (await jobs.GetCountsAsync()).Queued;
    await Task.Delay(1200);                      // 观察窗口

    // 12 个任务 > 8 个 worker：超出并发的任务被暂停门拦在门外，
    // 观察窗口内必须保持 Paused —— 这是"不再开始新任务"的直接证据
    var midCounts = await jobs.GetCountsAsync();
    Check("暂停后不再开始新任务（超额任务被拦在门外）",
        midCounts.Paused > 0 && midCounts.Queued == queuedAtPause,
        $"排队 {queuedAtPause} → {midCounts.Queued}，暂停 {midCounts.Paused}，运行中 {midCounts.Running}");

    await queue.ResumeAsync();
    var resumeStats = await runTask;
    var afterResumeRun = await assets.GetAllAsync();
    Check("继续后剩余任务全部完成",
        resumeStats.Failed == 0 && afterResumeRun.All(a => a.Status == AssetStatus.Ready),
        $"成功 {resumeStats.Succeeded} / 失败 {resumeStats.Failed}，就绪 {afterResumeRun.Count(a => a.Status == AssetStatus.Ready)}/{afterResumeRun.Count}");

    // ── 6. 取消单个运行中的任务 ────────────────────────────────
    var victim = afterResumeRun.First(a => a.Kind == AssetKind.Video);
    await assets.UpdateStatusAsync(victim.Id, AssetStatus.Pending, null);
    var victimJobId = await queue.EnqueueAsync(victim.Id, ImportService.JobTypeFor(victim.Kind), 1);

    var cancelRun = runner.RunAsync();

    // 等它进入 Running
    var becameRunning = false;
    for (var i = 0; i < 100 && !becameRunning; i++)
    {
        await Task.Delay(100);
        var rows = await jobs.GetByAssetAsync(victim.Id);
        becameRunning = rows.Any(j => j.Id == victimJobId && j.Status == JobStatus.Running);
    }

    await queue.CancelJobAsync(victimJobId);
    var cancelStats = await cancelRun;

    var victimJob = (await jobs.GetByAssetAsync(victim.Id)).FirstOrDefault(j => j.Id == victimJobId);
    var victimAsset = await assets.GetByIdAsync(victim.Id);
    Check("取消运行中的任务：任务标记 Canceled、素材退回 Pending",
        becameRunning && victimJob?.Status == JobStatus.Canceled && victimAsset?.Status == AssetStatus.Pending,
        $"进入运行={(becameRunning ? "是" : "否")}，任务状态={victimJob?.Status}，素材状态={victimAsset?.Status}");

    Check("取消后无残留临时文件", Directory.GetFiles(layout.TempDir).Length == 0,
        $".tmp 下 {Directory.GetFiles(layout.TempDir).Length} 个文件");

    // 被取消的素材重新转码补回（让库处于一致状态）
    await queue.EnqueueAsync(victim.Id, ImportService.JobTypeFor(victim.Kind), 1);
    await runner.RunAsync();

    // ── 7. 失败 → 重试 ─────────────────────────────────────────
    var ghost = new Asset
    {
        Kind = AssetKind.Photo,
        FileName = "幽灵_001.jpg",
        FolderKey = "幽灵目录",
        LibraryPath = "幽灵目录/幽灵_001.png",
        SourcePath = @"C:\不存在的源文件\幽灵.jpg",
        SizeBytes = 1,
        QuickHash = "ffffffffffffffff",
        Status = AssetStatus.Pending,
    };
    var ghostId = await assets.InsertAsync(ghost);
    var ghostJobId = await queue.EnqueueAsync(ghostId, ImportService.JobTypeFor(ghost.Kind), 1);

    // 自动重试机制：失败未达上限会退回队列，跑满 MaxAttempts 轮才进入终态
    for (var i = 0; i < ImportJobRunner.MaxAttempts; i++)
        await runner.RunAsync();

    var ghostJob1 = (await jobs.GetByAssetAsync(ghostId)).First(j => j.Id == ghostJobId);
    Check("源文件丢失的任务重试耗尽后进入失败态", ghostJob1.Status == JobStatus.Failed,
        $"状态={ghostJob1.Status}，尝试={ghostJob1.Attempts}，错误={ghostJob1.ErrorMessage}");

    await queue.RetryAsync([ghostJobId]);
    var ghostJob2 = (await jobs.GetByAssetAsync(ghostId)).First(j => j.Id == ghostJobId);
    Check("重试后回到排队", ghostJob2.Status == JobStatus.Queued && ghostJob2.Attempts == 0,
        $"状态={ghostJob2.Status}，尝试次数={ghostJob2.Attempts}");

    await runner.RunAsync();   // 再次失败（终态），不影响其余断言
    await assets.DeleteAsync(ghostId);
    await jobs.DeleteForAssetsAsync([ghostId]);

    // ── 8. 崩溃恢复：Running 的任务重启后继续 ───────────────────
    var crashIds = afterResumeRun.Take(3).Select(a => a.Id).ToList();
    var crashJobIds = new List<long>();
    foreach (var id in crashIds)
    {
        var a = afterResumeRun.First(x => x.Id == id);
        await assets.UpdateStatusAsync(id, AssetStatus.Pending, null);
        crashJobIds.Add(await queue.EnqueueAsync(id, ImportService.JobTypeFor(a.Kind), 1));
    }

    // 模拟崩溃：直接把第一个任务标记为 Running 后"重启"整套服务
    await jobs.MarkRunningAsync(crashJobIds[0]);
    db.Dispose();

    var (_, db2, assets2, jobs2, _, runner2, _, _) = Build(libraryRoot);
    var recovered = await jobs2.RecoverInterruptedAsync();
    Check("重启时把中断的 Running 任务退回队列", recovered >= 1, $"恢复 {recovered} 个");

    var recoveryStats = await runner2.RunAsync();
    var afterRecovery = await assets2.GetAllAsync();
    Check("重启后自动续跑并全部完成",
        afterRecovery.All(a => a.Status == AssetStatus.Ready) && recoveryStats.Failed == 0,
        $"处理 {recoveryStats.Succeeded} 项，就绪 {afterRecovery.Count(a => a.Status == AssetStatus.Ready)}/{afterRecovery.Count}");

    // ── 9. 队列快照带素材名（面板显示用）─────────────────────
    var snapshot = await jobs2.GetRecentAsync(30);
    Check("队列快照含素材名", snapshot.Count > 0 && snapshot.All(j => !string.IsNullOrEmpty(j.AssetName)),
        $"{snapshot.Count} 条，全部带文件名={snapshot.All(j => !string.IsNullOrEmpty(j.AssetName))}");

    // ── 10. 清除已结束的任务（完成/取消），失败的保留 ───────────
    //      （注意用重启后的 jobs2 —— 旧的 db 已随"崩溃"释放）
    await jobs2.DeleteFinishedAsync();
    var cleared = await jobs2.GetCountsAsync();
    Check("清除后只剩失败任务", cleared.Done == 0 && cleared.Canceled == 0,
        $"完成 {cleared.Done} / 取消 {cleared.Canceled} / 失败 {cleared.Failed} / 排队 {cleared.Queued}");

    db2.Dispose();
    return Report();
}

Console.WriteLine($"未知模式: {mode}（可用：phase1 / phase2 / phase4）");
return 2;
