using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaLibrary.App.ViewModels;

namespace MediaLibrary.App.Views;

/// <summary>卡片悬停事件参数。</summary>
public sealed class AssetHoverEventArgs : RoutedEventArgs
{
    public AssetHoverEventArgs(RoutedEvent routedEvent, object source) : base(routedEvent, source) { }

    /// <summary>对应的素材。仅在 <c>HoverStarted</c> 时有值。</summary>
    public AssetItemViewModel? Item { get; init; }
}

/// <summary>
/// 网格里的一张素材卡片。
///
/// 缩略图采用"按需加载"：只有卡片真正进入可视区才触发解码。
/// 配合虚拟化面板，天然做到滚到哪加载到哪，几百个素材也不会一次性占满内存。
///
/// 悬停预览通过<b>冒泡路由事件</b>上报给主窗口，
/// 这样卡片本身不需要知道预览窗口的存在（便于单测与复用）。
/// </summary>
public partial class AssetTile : UserControl
{
    /// <summary>卡片宽度。高度由 XAML 里经 <c>TileHeight</c> 转换器自动算出。</summary>
    public static readonly DependencyProperty TileWidthProperty =
        DependencyProperty.Register(
            nameof(TileWidth),
            typeof(double),
            typeof(AssetTile),
            new PropertyMetadata(200.0));

    /// <summary>鼠标进入卡片（会冒泡到主窗口）。</summary>
    public static readonly RoutedEvent HoverStartedEvent = EventManager.RegisterRoutedEvent(
        nameof(HoverStarted), RoutingStrategy.Bubble,
        typeof(EventHandler<AssetHoverEventArgs>), typeof(AssetTile));

    /// <summary>鼠标离开卡片（会冒泡到主窗口）。</summary>
    public static readonly RoutedEvent HoverEndedEvent = EventManager.RegisterRoutedEvent(
        nameof(HoverEnded), RoutingStrategy.Bubble,
        typeof(EventHandler<AssetHoverEventArgs>), typeof(AssetTile));

    public AssetTile()
    {
        InitializeComponent();

        MouseEnter += OnTileMouseEnter;
        MouseLeave += OnTileMouseLeave;
    }

    public double TileWidth
    {
        get => (double)GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    public event EventHandler<AssetHoverEventArgs> HoverStarted
    {
        add => AddHandler(HoverStartedEvent, value);
        remove => RemoveHandler(HoverStartedEvent, value);
    }

    public event EventHandler<AssetHoverEventArgs> HoverEnded
    {
        add => AddHandler(HoverEndedEvent, value);
        remove => RemoveHandler(HoverEndedEvent, value);
    }

    private void OnTileMouseEnter(object sender, MouseEventArgs e)
    {
        RequestThumbnail();

        RaiseEvent(new AssetHoverEventArgs(HoverStartedEvent, this)
        {
            Item = DataContext as AssetItemViewModel,
        });
    }

    private void OnTileMouseLeave(object sender, MouseEventArgs e)
    {
        // 卡片被虚拟化回收时 DataContext 可能已经变了，但"离开"这件事仍然要上报，
        // 否则悬浮预览会一直挂在屏幕上。
        RaiseEvent(new AssetHoverEventArgs(HoverEndedEvent, this));
    }

    /// <summary>首次进入可视区。</summary>
    private void OnLoaded(object sender, RoutedEventArgs e) => RequestThumbnail();

    /// <summary>
    /// 虚拟化会复用容器：同一个 AssetTile 实例可能被换成另一条数据而不重新触发 Loaded，
    /// 所以 DataContext 变化时也要请求一次。
    /// <see cref="AssetItemViewModel.RequestThumbnail"/> 是幂等的，重复调用无副作用。
    /// </summary>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == DataContextProperty)
            RequestThumbnail();
    }

    private void RequestThumbnail()
        => (DataContext as AssetItemViewModel)?.RequestThumbnail();
}
