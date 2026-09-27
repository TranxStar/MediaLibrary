using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaLibrary.App.Services;

namespace MediaLibrary.App.Views;

/// <summary>
/// 框选用的 ListBox。
///
/// WPF 的 ListBox 基类在【选中项变化】时会把列表滚到选中项 ——
/// 框选把窗口外的文件选进 SelectedItems 的瞬间，列表会被滚到那个文件
/// （实测：从下往上框选时 812.8 → 0 一步跳顶，选中数越多越容易触发）。
/// <see cref="SuppressScrollIntoView"/> 置位期间，在同一帧内撤销基类的滚动。
/// </summary>
public sealed class MarqueeListBox : ListBox
{
    /// <summary>程序化批量选中期间置位：撤销基类的"滚到选中项"。</summary>
    public bool SuppressScrollIntoView { get; set; }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        var sv = FindScrollViewer();
        var offsetBefore = sv?.VerticalOffset ?? 0;

        base.OnSelectionChanged(e);

        if (SuppressScrollIntoView && sv is not null &&
            Math.Abs(sv.VerticalOffset - offsetBefore) > 2)
        {
            sv.ScrollToVerticalOffset(offsetBefore);
        }
    }

    private ScrollViewer? FindScrollViewer()
    {
        if (Template.FindName("PART_ScrollViewer", this) is ScrollViewer named) return named;

        return FindVisualChild<ScrollViewer>(this);
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
}
