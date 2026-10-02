using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PaperCoin.Core;

namespace PaperCoin;

/// <summary>
/// 运行中的主题渲染。DynamicResource 会把解析结果缓存到依赖属性上，
/// 只有原 ResourceDictionary 实例发出内容变更通知时才会重新解析，
/// 因此这里改写该实例的内容，替换整个实例不会生效。
/// 启动阶段窗口尚未创建，那时可以直接替换实例（见 App.ApplyTheme）。
/// </summary>
internal static class ThemeRenderer
{
    /// <summary>
    /// 识别颜色字典用的哨兵 key。必须是 Dark.xaml 与 Bright.xaml 都定义、
    /// 其它字典都不定义的 key，启动时整本替换与运行时原地替换都依赖它。
    /// </summary>
    private const string SentinelKey = "ColorMainContentArea";

    /// <summary>
    /// 颜色过渡时长。比页面内的交互动效（100~200ms）稍长：主题切换是全局变化，
    /// 不是对某次点击的反馈，太快会读成"闪一下"。
    /// </summary>
    private static readonly Duration TransitionDuration = TimeSpan.FromMilliseconds(300);

    private static readonly IEasingFunction TransitionEasing =
        new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>按主题名装载颜色字典</summary>
    public static ResourceDictionary LoadTheme(string theme) => new()
    {
        Source = new Uri(
            $"/{Constants.AppName};component/Themes/{theme}.xaml",
            UriKind.Relative)
    };

    /// <summary>在合并字典集合中定位颜色字典，找不到时返回 -1</summary>
    public static int IndexOfColorDictionary(IList<ResourceDictionary> merged)
    {
        for (int i = 0; i < merged.Count; i++)
        {
            if (merged[i].Contains(SentinelKey)) return i;
        }
        return -1;
    }

    /// <summary>
    /// 应用主题，颜色与不透明度从当前值过渡到新值。
    /// ResourceDictionary 会冻结装进去的 Freezable，所以没法原地改已有画刷的颜色；
    /// 这里改为逐个换上新画刷，并在换上去之前挂好动画：带活动动画的 Freezable 不可冻结，
    /// 字典的冻结尝试会静默失败，画刷因此保持可变。DynamicResource 消费者拿到的就是这些实例，
    /// 整片界面于是跟着一起过渡。
    /// </summary>
    public static void Apply(string theme)
    {
        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null) return;

        var index = IndexOfColorDictionary(merged);

        // 找不到说明 App.xaml 的合并结构被改过或哨兵 key 被重命名，静默返回，避免主题切换失败影响正在使用的窗口。
        if (index < 0) return;

        var target = merged[index];
        var incoming = LoadTheme(theme);

        foreach (var key in incoming.Keys)
            target[key] = Transitional(target, key, incoming[key]);

        // 新主题删掉的 key 必须一并移除，否则旧颜色会留在字典里继续被引用
        foreach (var stale in target.Keys.Cast<object>().Where(k => !incoming.Contains(k)).ToList())
            target.Remove(stale);
    }

    /// <summary>
    /// 为一个条目造过渡版本：颜色与不透明度都从字典里的旧值插值到新值。
    /// 非画刷条目、或新旧完全相同的条目直接返回原值。
    /// </summary>
    private static object Transitional(ResourceDictionary current, object key, object incoming)
    {
        if (incoming is not SolidColorBrush to) return incoming;
        if (current[key] is not SolidColorBrush from) return incoming;

        bool colorChanged = from.Color != to.Color;
        bool opacityChanged = from.Opacity != to.Opacity;
        if (!colorChanged && !opacityChanged) return incoming;

        // 基准值必须是目标值：动画用 FillBehavior.Stop，跑完会落回基准值，设错会闪回旧颜色。
        var brush = new SolidColorBrush(to.Color) { Opacity = to.Opacity };

        if (colorChanged)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, Animate(from.Color, to.Color));

        if (opacityChanged)
            brush.BeginAnimation(SolidColorBrush.OpacityProperty, Animate(from.Opacity, to.Opacity));

        return brush;
    }

    private static ColorAnimation Animate(Color from, Color to) => new()
    {
        From = from,
        To = to,
        Duration = TransitionDuration,
        EasingFunction = TransitionEasing,
        FillBehavior = FillBehavior.Stop
    };

    private static DoubleAnimation Animate(double from, double to) => new()
    {
        From = from,
        To = to,
        Duration = TransitionDuration,
        EasingFunction = TransitionEasing,
        FillBehavior = FillBehavior.Stop
    };
}
