using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PaperCoin.Converters;

/// <summary>
/// 可用宽度 → 悬浮缩放所需的左右留白（Thickness）。悬浮放大只改变绘制、不改变布局尺寸，
/// 卡片溢出部分会被外层 ScrollViewer 裁掉，因此按视口宽度预留留白，单边留白取 W·s/2 − W/2，
/// 其中 W 为视口宽、s 为放大倍数。固定像素在窗口缩放后会失效。
/// </summary>
public class HoverSlackConverter : IValueConverter
{
    /// <summary>value 为可用宽度，parameter 为悬浮缩放峰值。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double width || double.IsNaN(width) || width <= 0)
            return default(Thickness);

        double scale = ParseScale(parameter);

        // 不放大时不预留留白，否则卡片会被压窄
        if (scale <= 1.0) return default(Thickness);

        double slack = width * (scale - 1.0) / 2.0;

        return new Thickness(slack, 0, slack, 0);
    }

    /// <summary>解析缩放峰值，失败时回退到 1.0（不放大）。</summary>
    private static double ParseScale(object? parameter)
    {
        if (parameter is double d) return d;

        if (parameter is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return 1.0;
    }

    /// <summary>只支持单向绑定。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(HoverSlackConverter)} 只支持单向绑定");
}
