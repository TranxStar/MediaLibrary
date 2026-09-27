using MediaLibrary.Core.Naming;

namespace MediaLibrary.Core.Tests;

public class NamingEngineTests
{
    [Theory]
    [InlineData(0, "000")]
    [InlineData(1, "001")]
    [InlineData(9, "009")]
    [InlineData(42, "042")]
    [InlineData(999, "999")]
    [InlineData(1000, "1000")]      // 超 999 自动扩位
    [InlineData(12345, "12345")]
    public void FormatIndex_按需扩位(int index, string expected)
        => Assert.Equal(expected, NamingEngine.FormatIndex(index));

    [Theory]
    [InlineData("开幕式", 1, ".mp4", "开幕式_001.mp4")]
    [InlineData("开幕式", 12, "mp4", "开幕式_012.mp4")]
    [InlineData("元旦晚会", 1000, ".MOV", "元旦晚会_1000.mov")]
    public void BuildFileName_文件夹名加序号(string folder, int index, string ext, string expected)
        => Assert.Equal(expected, NamingEngine.BuildFileName(folder, index, ext));

    [Fact]
    public void SanitizeFolderName_替换非法字符()
    {
        // \ / : * ? " < > | 全部替换为下划线
        var result = NamingEngine.SanitizeFolderName("a\\b/c:d*e?f\"g<h>i|j");
        Assert.Equal("a_b_c_d_e_f_g_h_i_j", result);
    }

    [Fact]
    public void SanitizeFolderName_去掉尾部点与空格()
    {
        Assert.Equal("运动会", NamingEngine.SanitizeFolderName("运动会..."));
        Assert.Equal("运动会", NamingEngine.SanitizeFolderName("运动会   "));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul", "_nul")]
    [InlineData("COM1", "_COM1")]
    public void SanitizeFolderName_规避保留设备名(string reserved, string expected)
        => Assert.Equal(expected, NamingEngine.SanitizeFolderName(reserved));

    [Fact]
    public void SanitizeFolderName_普通名字不保留不误伤()
    {
        // "CONSOLE" 不是保留名，不应被加前缀
        Assert.Equal("CONSOLE", NamingEngine.SanitizeFolderName("CONSOLE"));
        Assert.Equal("运动会", NamingEngine.SanitizeFolderName("运动会"));
    }

    [Fact]
    public void SanitizeFolderName_空名回退兜底()
    {
        Assert.Equal(LibraryPathHelper.FallbackFolder, NamingEngine.SanitizeFolderName(""));
        Assert.Equal(LibraryPathHelper.FallbackFolder, NamingEngine.SanitizeFolderName("   "));
        Assert.Equal(LibraryPathHelper.FallbackFolder, NamingEngine.SanitizeFolderName("..."));
        Assert.Equal(LibraryPathHelper.FallbackFolder, NamingEngine.SanitizeFolderName(null));
    }

    [Fact]
    public void SanitizeFolderName_超长截断()
    {
        var longName = new string('运', 200);
        var result = NamingEngine.SanitizeFolderName(longName);
        Assert.Equal(NamingEngine.MaxFolderNameLength, result.Length);
    }

    [Theory]
    [InlineData("运动会", "运动会")]
    [InlineData("运动会/开幕式", "开幕式")]
    [InlineData("a/b/c", "c")]
    [InlineData("", LibraryPathHelper.FallbackFolder)]
    public void InnermostFolderName_取最内层(string key, string expected)
        => Assert.Equal(expected, NamingEngine.InnermostFolderName(key));
}

public class LibraryPathHelperTests
{
    [Theory]
    [InlineData(@"开幕式", "开幕式")]
    [InlineData(@"运动会\开幕式", "运动会/开幕式")]
    [InlineData(@"运动会\开幕式\颁奖", "运动会/开幕式/颁奖")]
    [InlineData("", "")]
    public void ToFolderKey_统一正斜杠(string input, string expected)
        => Assert.Equal(expected, LibraryPathHelper.ToFolderKey(input));

    [Fact]
    public void ExpandDrop_拖入的文件夹自身成为一层()
    {
        using var temp = new TempDir();
        temp.CreateFile(@"运动会\开幕式\a.mp4");
        temp.CreateFile(@"运动会\开幕式\b.mp4");
        temp.CreateFile(@"运动会\颁奖\c.mp4");
        temp.CreateFile(@"运动会\readme.txt");   // 非媒体，应被忽略

        var dropRoot = Path.Combine(temp.Path, "运动会");
        var items = LibraryPathHelper.ExpandDrop([dropRoot]).ToList();

        Assert.Equal(3, items.Count);

        // 最内层文件夹名就是文件名前缀来源
        var open = items.Single(i => i.SourcePath.EndsWith("a.mp4"));
        Assert.Equal("运动会/开幕式", open.FolderKey);
        Assert.Equal("开幕式", NamingEngine.InnermostFolderName(open.FolderKey));

        var award = items.Single(i => i.SourcePath.EndsWith("c.mp4"));
        Assert.Equal("运动会/颁奖", award.FolderKey);

        // 非媒体文件被过滤
        Assert.DoesNotContain(items, i => i.SourcePath.EndsWith(".txt"));
    }

    [Fact]
    public void ExpandDrop_直接拖子文件夹则它就是顶层()
    {
        using var temp = new TempDir();
        temp.CreateFile(@"运动会\开幕式\a.mp4");

        var leaf = Path.Combine(temp.Path, "运动会", "开幕式");
        var items = LibraryPathHelper.ExpandDrop([leaf]).ToList();

        Assert.Single(items);
        Assert.Equal("开幕式", items[0].FolderKey);
    }

    [Fact]
    public void ExpandDrop_单个文件取父目录名()
    {
        using var temp = new TempDir();
        var file = temp.CreateFile(@"随便\VID_1234.mp4");

        var items = LibraryPathHelper.ExpandDrop([file]).ToList();

        Assert.Single(items);
        Assert.Equal("随便", items[0].FolderKey);
        Assert.Equal("随便", NamingEngine.InnermostFolderName(items[0].FolderKey));
    }

    [Fact]
    public void ScanLibrary_镜像目录结构且跳过保留目录()
    {
        using var temp = new TempDir();
        temp.CreateFile(@"运动会\开幕式\a.mp4");
        temp.CreateFile(@"元旦晚会\b.jpg");
        temp.CreateFile(@".tmp\c.mp4");           // 库内临时区，必须跳过
        temp.CreateFile(@".cache\thumbs\d.mp4");  // 缩略图缓存，必须跳过

        var files = LibraryPathHelper.ScanLibrary(temp.Path).ToList();

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.FileName == "a.mp4" && f.FolderKey == "运动会/开幕式");
        Assert.Contains(files, f => f.FileName == "b.jpg" && f.FolderKey == "元旦晚会");
        Assert.DoesNotContain(files, f => f.FileName is "c.mp4" or "d.mp4");
    }
}

/// <summary>测试用临时目录，Dispose 时自动清理。</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "MediaLibraryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateFile(string relativePath)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
        return full;
    }

    public string CreateFileWithBytes(string relativePath, byte[] content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
