using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PaperCoin.Converters;

/// <summary>
/// bool → Visibility。WPF 内置的 <see cref="BooleanToVisibilityConverter"/> 不支持反向参数，
/// 传 ConverterParameter=Invert 不报错也不生效，需要反向时用 InverseBoolToVisibilityConverter。
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>反向转换只认 Visible，其余（含 Collapsed/Hidden）一律视为 true。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>bool → Visibility 的反相版本，用于空状态提示等互补显示。</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}
