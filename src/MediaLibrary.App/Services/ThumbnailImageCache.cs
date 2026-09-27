using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaLibrary.App.Services;

/// <summary>
/// 已解码缩略图的 LRU 缓存（设计文档 §5.7 的内存优化）。
///
/// 三个关键点：
/// <list type="number">
///   <item><c>BitmapCacheOption.OnLoad</c> —— 否则 BitmapImage 会一直占用文件句柄，缩略图无法重建</item>
///   <item><c>DecodePixelWidth = 320</c> —— 按需解码，几十 MB 的原图也不会吃内存</item>
///   <item><c>Freeze()</c> —— 冻结后可跨线程共享，解码能放在后台线程做</item>
/// </list>
/// </summary>
public sealed class ThumbnailImageCache
{
    private const int DecodeWidth = 320;
    private const int DefaultCapacity = 500;

    private readonly int _capacity;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _lru = new();
    private readonly SemaphoreSlim _loadGate;

    public ThumbnailImageCache(int capacity = DefaultCapacity, int maxConcurrentLoads = 6)
    {
        _capacity = Math.Max(16, capacity);
        _loadGate = new SemaphoreSlim(Math.Max(1, maxConcurrentLoads));
    }

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }

    /// <summary>只查缓存，不触发解码。可能返回 null。</summary>
    public ImageSource? TryGet(string path)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(path, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Image;
        }
    }

    /// <summary>取缓存或解码。文件不存在 / 解码失败返回 null。</summary>
    public async Task<ImageSource?> GetOrLoadAsync(string path, CancellationToken ct = default)
    {
        var cached = TryGet(path);
        if (cached is not null) return cached;

        if (!File.Exists(path)) return null;

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 双检：等锁期间可能已被别的请求加载好
            cached = TryGet(path);
            if (cached is not null) return cached;

            // 解码放后台线程；Freeze 后即可跨线程使用
            var image = await Task.Run(() => Decode(path), ct).ConfigureAwait(false);
            if (image is null) return null;

            Add(path, image);
            return image;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>缩略图被重新生成后调用，丢弃旧图。</summary>
    public void Invalidate(string path)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(path, out var node)) return;
            _lru.Remove(node);
            _map.Remove(path);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    private static BitmapImage? Decode(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = DecodeWidth;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // 文件被占用、损坏、格式异常
            return null;
        }
    }

    private void Add(string path, ImageSource image)
    {
        lock (_lock)
        {
            if (_map.ContainsKey(path)) return;

            var node = _lru.AddFirst(new Entry(path, image));
            _map[path] = node;

            while (_map.Count > _capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Path);
            }
        }
    }

    private readonly record struct Entry(string Path, ImageSource Image);
}
