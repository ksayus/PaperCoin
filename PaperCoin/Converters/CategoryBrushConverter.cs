using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace PaperCoin.Converters;

/// <summary>
/// 主题颜色键 → 画刷的资源查找。所有"按分类取色"的路径都走这一处，
/// 保证界面与图表拿到的规则完全一致。
/// </summary>
internal static class ThemeBrush
{
    /// <summary>
    /// 解析一个主题颜色键。查不到时返回 null，调用方各自决定回退行为。
    /// <para>
    /// 查应用级资源字典而**不是**某个元素的资源：主题字典合并进
    /// <c>Application.Resources</c>，从这里查不依赖元素有没有挂上可视树。
    /// 换成"从某个 FrameworkElement 找"的写法在离屏测试宿主里会静默拿不到画刷。
    /// </para>
    /// </summary>
    public static Brush? Resolve(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;

        return Application.Current?.TryFindResource(key) as Brush;
    }
}

/// <summary>
/// XAML 里按主题颜色键取画刷：<c>Stroke="{conv:ThemeBrush CategoryColor01}"</c>。
/// <para>
/// 存在的理由是 <see cref="System.Windows.Data.Binding.ConverterParameter"/> 接不了
/// <c>{Binding}</c>：<c>ConverterParameter</c> 是 <see cref="System.Windows.Data.Binding"/>
/// 上的普通属性，不是依赖属性，解析期会直接抛
/// <c>XamlParseException: 不能在"Binding"类型的"ConverterParameter"属性上设置"Binding"</c>。
/// 而这里需要"跟着主题切换换颜色"，所以改用标记扩展直接取 <see cref="DynamicResourceExtension"/>
/// 的同一条路径。
/// </para>
/// </summary>
[MarkupExtensionReturnType(typeof(Brush))]
public sealed class ThemeBrushExtension : MarkupExtension
{
    public ThemeBrushExtension()
    {
    }

    public ThemeBrushExtension(string colorKey) => ColorKey = colorKey;

    /// <summary>主题字典里的颜色键，如 <c>CategoryColor03</c></summary>
    public string ColorKey { get; set; } = "";

    public override object? ProvideValue(IServiceProvider serviceProvider) => ThemeBrush.Resolve(ColorKey);
}

/// <summary>
/// 颜色键（字符串，来自 ViewModel）→ 画刷。
/// <para>
/// 用在 <c>Binding</c> 的转换上：键是运行期才知道的（分类名决定槽位），
/// 没法写成 <see cref="ThemeBrushExtension"/> 那样的字面量。
/// 这里不需要额外的锚点参数，见 <see cref="ThemeBrush.Resolve"/>。
/// </para>
/// </summary>
public sealed class CategoryBrushConverter : System.Windows.Data.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => ThemeBrush.Resolve(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException("分类颜色是单向派生值，不支持回写。");
}
