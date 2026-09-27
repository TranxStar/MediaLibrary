using System.IO.Hashing;

namespace MediaLibrary.Core.Hashing;

/// <summary>
/// 快速去重哈希（设计文档 §5.3）。
/// <c>XxHash64(文件大小 ‖ 首 64KB ‖ 尾 64KB)</c> → 16 位十六进制。
///
/// 为什么不用全文件 SHA-256：几十 GB 素材做全哈希要几十分钟，
/// 而首尾抽样在这个规模下碰撞概率可忽略。
/// </summary>
public static class QuickHash
{
    public const int HeadBytes = 64 * 1024;
    public const int TailBytes = 64 * 1024;

    /// <summary>小于此值直接哈希整个文件。</summary>
    public const long SmallFileThreshold = HeadBytes + TailBytes;

    /// <summary>计算文件的快速哈希。十六进制小写，固定 16 字符。</summary>
    public static string Compute(string filePath)
    {
        using var fs = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 0,
            FileOptions.SequentialScan);

        return Compute(fs, fs.Length);
    }

    /// <summary>异步计算，避免大文件读取阻塞 UI 线程。</summary>
    public static Task<string> ComputeAsync(string filePath, CancellationToken ct = default)
        => Task.Run(() => Compute(filePath, ct), ct);

    public static string Compute(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var fs = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 0,
            FileOptions.SequentialScan);

        return Compute(fs, fs.Length, ct);
    }

    /// <summary>核心算法。流必须可定位。</summary>
    public static string Compute(Stream stream, long length, CancellationToken ct = default)
    {
        var hasher = new XxHash64();

        // 把长度也纳入哈希，避免不同长度的文件抽样恰好相同
        Span<byte> lengthBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
        hasher.Append(lengthBytes);

        if (length <= SmallFileThreshold)
        {
            HashWholeStream(hasher, stream, ct);
        }
        else
        {
            var buffer = new byte[HeadBytes];

            stream.Position = 0;
            stream.ReadExactly(buffer, 0, HeadBytes);
            hasher.Append(buffer);

            ct.ThrowIfCancellationRequested();

            stream.Position = length - TailBytes;
            stream.ReadExactly(buffer, 0, TailBytes);
            hasher.Append(buffer);
        }

        return hasher.GetCurrentHashAsUInt64().ToString("x16");
    }

    private static void HashWholeStream(XxHash64 hasher, Stream stream, CancellationToken ct)
    {
        stream.Position = 0;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hasher.Append(buffer.AsSpan(0, read));
        }
    }
}
