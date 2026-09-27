using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MediaLibrary.App.Converters;

/// <summary>
/// 卡片宽度 → 卡片总高度。
/// 高 = 宽 × 9/16（缩略图 16:9）+ 固定高度（文件名那一行）。
/// </summary>
public sealed class TileWidthToHeightConverter : IValueConverter
{
    private const double ThumbnailAspect = 9.0 / 16.0;
    private const double LabelHeight = 34;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = value is double d && d > 0 ? d : 200;

        // 支持 "0.5625|34" 形式覆盖默认值
        if (parameter is string s && s.Contains('|'))
        {
            var parts = s.Split('|');
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var aspect))
                return width * aspect + LabelHeight;
        }

        return width * ThumbnailAspect + LabelHeight;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null → Collapsed；非 null → Visible。</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null → Visible；非 null → Collapsed。</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>空字符串/空白 → Collapsed。</summary>
public sealed class EmptyStringToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool 取反。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>false → Visible；true → Collapsed。用于"无内容时显示提示"。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>数值乘以参数（用于按比例换算尺寸）。</summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var input = value is double d ? d : 0;
        var factor = 1.0;
        if (parameter is string s)
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out factor);
        return input * factor;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
