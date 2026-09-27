using MediaLibrary.Core;
using MediaLibrary.Core.Models;
using MediaLibrary.Core.Naming;

namespace MediaLibrary.Core.Tests;

public class TargetFormatTests
{
    [Theory]
    [InlineData(AssetKind.Video, "a.mov", ".mp4")]
    [InlineData(AssetKind.Video, "a.mkv", ".mp4")]
    [InlineData(AssetKind.Video, "a.avi", ".mp4")]
    [InlineData(AssetKind.Photo, "a.jpg", ".png")]
    [InlineData(AssetKind.Photo, "a.heic", ".png")]
    [InlineData(AssetKind.Photo, "a.webp", ".png")]
    [InlineData(AssetKind.Audio, "a.m4a", ".m4a")]
    [InlineData(AssetKind.Audio, "a.MP3", ".mp3")]
    public void 目标扩展名符合设计文档(AssetKind kind, string source, string expected)
        => Assert.Equal(expected, TargetFormat.ExtensionFor(kind, source));
}

public class ImportPlannerTests
{
    private static SourceFile Make(string path, string folderKey, string hash,
        AssetKind kind = AssetKind.Video, long size = 1000)
        => new(path, folderKey, kind, size, hash);

    [Fact]
    public void 过滤掉库内已存在的内容()
    {
        var candidates = new[]
        {
            Make(@"D:\a\1.mp4", "运动会", "aaaa000000000001"),
            Make(@"D:\a\2.mp4", "运动会", "bbbb000000000002"),
        };

        var (accepted, skipped) = ImportPlanner.FilterDuplicates(
            candidates, new HashSet<string> { "aaaa000000000001" });

        Assert.Single(accepted);
        Assert.Equal("bbbb000000000002", accepted[0].QuickHash);
        Assert.Single(skipped);
        Assert.Equal(ImportPlanner.ReasonDuplicateInLibrary, skipped[0].Reason);
    }

    [Fact]
    public void 过滤掉同一次拖入内的重复()
    {
        // 同一段素材被放在两个文件夹里，一次拖入 —— 只能入库一份，否则会转码两遍
        var candidates = new[]
        {
            Make(@"D:\a\1.mp4", "运动会", "cccc000000000003"),
            Make(@"D:\b\copy.mp4", "元旦晚会", "cccc000000000003"),
        };

        var (accepted, skipped) = ImportPlanner.FilterDuplicates(candidates, new HashSet<string>());

        Assert.Single(accepted);
        Assert.Single(skipped);
        Assert.Equal(ImportPlanner.ReasonDuplicateInBatch, skipped[0].Reason);
    }

    [Fact]
    public void 无重复时全部接受()
    {
        var candidates = new[]
        {
            Make(@"D:\a\1.mp4", "运动会", "0000000000000001"),
            Make(@"D:\a\2.mp4", "运动会", "0000000000000002"),
            Make(@"D:\a\3.mp4", "运动会", "0000000000000003"),
        };

        var (accepted, skipped) = ImportPlanner.FilterDuplicates(candidates, new HashSet<string>());

        Assert.Equal(3, accepted.Count);
        Assert.Empty(skipped);
    }

    [Fact]
    public void 命名用最内层文件夹名加序号()
    {
        var source = Make(@"D:\素材\运动会\开幕式\VID_001.mp4", "运动会/开幕式", "x");
        var named = ImportPlanner.Name(source, 1);

        Assert.Equal("开幕式_001.mp4", named.FileName);
        Assert.Equal("运动会/开幕式/开幕式_001.mp4", named.LibraryPath);
    }

    [Fact]
    public void 命名对照片用PNG扩展名()
    {
        var source = Make(@"D:\a\IMG_1.jpg", "开幕式", "x", AssetKind.Photo);
        Assert.Equal("开幕式_007.png", ImportPlanner.Name(source, 7).FileName);
    }

    [Fact]
    public void 命名对音频保留原扩展名()
    {
        var source = Make(@"D:\a\BGM.m4a", "开幕式", "x", AssetKind.Audio);
        Assert.Equal("开幕式_002.m4a", ImportPlanner.Name(source, 2).FileName);
    }

    [Fact]
    public void 命名对超过999的序号自动扩位()
    {
        var source = Make(@"D:\a\v.mp4", "开幕式", "x");
        Assert.Equal("开幕式_1000.mp4", ImportPlanner.Name(source, 1000).FileName);
    }

    [Fact]
    public void 库根目录的文件直接放在库根()
    {
        var source = Make(@"D:\v.mp4", "", "x");
        var named = ImportPlanner.Name(source, 3);

        // FolderKey 为空时无目录前缀
        Assert.Equal($"{LibraryPathHelper.FallbackFolder}_003.mp4", named.FileName);
        Assert.Equal(named.FileName, named.LibraryPath);
    }

    [Fact]
    public void 跳过原因会归类汇总()
    {
        var plan = new ImportPlan([], [
            new SkippedImport("a", ImportPlanner.ReasonDuplicateInLibrary),
            new SkippedImport("b", ImportPlanner.ReasonDuplicateInLibrary),
            new SkippedImport("c", ImportPlanner.ReasonDuplicateInBatch),
        ]);

        Assert.Equal(3, plan.TotalSkipped);
        Assert.Equal(2, plan.SkipSummary[ImportPlanner.ReasonDuplicateInLibrary]);
        Assert.Equal(1, plan.SkipSummary[ImportPlanner.ReasonDuplicateInBatch]);
    }
}
