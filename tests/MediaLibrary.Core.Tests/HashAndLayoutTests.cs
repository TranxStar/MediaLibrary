using MediaLibrary.Core;
using MediaLibrary.Core.Hashing;
using MediaLibrary.Core.Models;

namespace MediaLibrary.Core.Tests;

public class QuickHashTests
{
    [Fact]
    public void 相同内容产生相同哈希()
    {
        using var temp = new TempDir();
        var a = temp.CreateFileWithBytes("a.bin", MakeBytes(1000, seed: 1));
        var b = temp.CreateFileWithBytes("b.bin", MakeBytes(1000, seed: 1));

        Assert.Equal(QuickHash.Compute(a), QuickHash.Compute(b));
    }

    [Fact]
    public void 内容不同则哈希不同()
    {
        using var temp = new TempDir();
        var a = temp.CreateFileWithBytes("a.bin", MakeBytes(1000, seed: 1));
        var b = temp.CreateFileWithBytes("b.bin", MakeBytes(1000, seed: 2));

        Assert.NotEqual(QuickHash.Compute(a), QuickHash.Compute(b));
    }

    [Fact]
    public void 长度不同则哈希不同()
    {
        using var temp = new TempDir();
        var a = temp.CreateFileWithBytes("a.bin", MakeBytes(1000, seed: 1));
        var b = temp.CreateFileWithBytes("b.bin", MakeBytes(1001, seed: 1));

        Assert.NotEqual(QuickHash.Compute(a), QuickHash.Compute(b));
    }

    [Fact]
    public void 哈希固定十六位()
    {
        using var temp = new TempDir();
        var f = temp.CreateFileWithBytes("a.bin", MakeBytes(10, seed: 0));
        var hash = QuickHash.Compute(f);

        Assert.Equal(16, hash.Length);
        Assert.Matches("^[0-9a-f]{16}$", hash);
    }

    [Fact]
    public void 大文件只抽样首尾但中间改动不影响哈希()
    {
        // 这是刻意的设计取舍（设计文档 §5.3）：速度快 50~100 倍，
        // 代价是中间区域改动检测不到。此测试把该行为固化下来。
        var size = 512 * 1024;
        var original = MakeBytes(size, seed: 7);
        var modified = (byte[])original.Clone();
        modified[size / 2] ^= 0xFF;   // 改中间

        using var temp = new TempDir();
        var a = temp.CreateFileWithBytes("a.bin", original);
        var b = temp.CreateFileWithBytes("b.bin", modified);

        Assert.Equal(QuickHash.Compute(a), QuickHash.Compute(b));
    }

    [Fact]
    public void 大文件尾部改动能被检测()
    {
        var size = 512 * 1024;
        var original = MakeBytes(size, seed: 7);
        var modified = (byte[])original.Clone();
        modified[size - 10] ^= 0xFF;   // 改尾部

        using var temp = new TempDir();
        var a = temp.CreateFileWithBytes("a.bin", original);
        var b = temp.CreateFileWithBytes("b.bin", modified);

        Assert.NotEqual(QuickHash.Compute(a), QuickHash.Compute(b));
    }

    [Fact]
    public void 空文件不抛异常()
    {
        using var temp = new TempDir();
        var f = temp.CreateFileWithBytes("empty.bin", []);
        var hash = QuickHash.Compute(f);
        Assert.Equal(16, hash.Length);
    }

    private static byte[] MakeBytes(int count, int seed)
    {
        var rng = new Random(seed);
        var bytes = new byte[count];
        rng.NextBytes(bytes);
        return bytes;
    }
}

public class MediaKindClassifierTests
{
    [Theory]
    [InlineData("a.mp4", AssetKind.Video)]
    [InlineData("a.MOV", AssetKind.Video)]
    [InlineData("a.mkv", AssetKind.Video)]
    [InlineData("a.braw", AssetKind.Video)]
    [InlineData("a.jpg", AssetKind.Photo)]
    [InlineData("a.HEIC", AssetKind.Photo)]
    [InlineData("a.png", AssetKind.Photo)]
    [InlineData("a.mp3", AssetKind.Audio)]
    [InlineData("a.m4a", AssetKind.Audio)]
    public void Classify_识别常见格式(string name, AssetKind expected)
        => Assert.Equal(expected, MediaKindClassifier.Classify(name));

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.pdf")]
    [InlineData("noextension")]
    [InlineData("a.xyz")]
    public void Classify_未识别返回null(string name)
        => Assert.Null(MediaKindClassifier.Classify(name));
}

public class LibraryLayoutTests
{
    [Fact]
    public void 相对绝对路径互转()
    {
        var layout = new LibraryLayout(@"E:\MediaLibrary");

        Assert.Equal(@"E:\MediaLibrary\运动会\开幕式\开幕式_001.mp4",
            layout.ToAbsolute("运动会/开幕式/开幕式_001.mp4"));

        Assert.Equal("运动会/开幕式/开幕式_001.mp4",
            layout.ToRelative(@"E:\MediaLibrary\运动会\开幕式\开幕式_001.mp4"));
    }

    [Fact]
    public void 库外路径转相对返回null()
    {
        var layout = new LibraryLayout(@"E:\MediaLibrary");
        Assert.Null(layout.ToRelative(@"D:\别的地方\a.mp4"));
    }

    [Fact]
    public void 内部目录命名符合设计文档()
    {
        var layout = new LibraryLayout(@"E:\MediaLibrary");

        Assert.Equal(@"E:\MediaLibrary\.library.db", layout.DatabasePath);
        Assert.Equal(@"E:\MediaLibrary\.cache\thumbs", layout.ThumbDir);
        Assert.Equal(@"E:\MediaLibrary\.tmp", layout.TempDir);
        Assert.Equal(@"E:\MediaLibrary\.cache\thumbs\a3f9c1.jpg",
            layout.ThumbAbsolutePath("a3f9c1"));
    }

    [Fact]
    public void 根目录FolderKey对应库根()
    {
        var layout = new LibraryLayout(@"E:\MediaLibrary");
        Assert.Equal(@"E:\MediaLibrary", layout.FolderAbsolutePath(""));
    }
}
