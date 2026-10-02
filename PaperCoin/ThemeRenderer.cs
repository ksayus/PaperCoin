using System.Windows;
using PaperCoin.Core;

namespace PaperCoin;

/// <summary>
/// 运行中的主题渲染。DynamicResource 会把解析结果缓存到依赖属性上，
/// 只有原 ResourceDictionary 实例发出内容变更通知时才会重新解析，
/// 因此这里清空并改写该实例的内容，替换整个实例不会生效。
/// 启动阶段窗口尚未创建，那时可以直接替换实例（见 App.ApplyTheme）。
/// </summary>
internal static class ThemeRenderer
{
    /// <summary>
    /// 识别颜色字典用的哨兵 key。必须是 Dark.xaml 与 Bright.xaml 都定义、
    /// 其它字典都不定义的 key，启动时整本替换与运行时原地替换都依赖它。
    /// </summary>
    private const string SentinelKey = "ColorMainContentArea";

    /// <summary>在合并字典集合中定位颜色字典，找不到时返回 -1</summary>
    public static int IndexOfColorDictionary(IList<ResourceDictionary> merged)
    {
        for (int i = 0; i < merged.Count; i++)
        {
            if (merged[i].Contains(SentinelKey)) return i;
        }
        return -1;
    }

    /// <summary>应用主题，使所有 DynamicResource 重新解析</summary>
    public static void Apply(string theme)
    {
        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null) return;

        var index = IndexOfColorDictionary(merged);

        // 找不到说明 App.xaml 的合并结构被改过或哨兵 key 被重命名，静默返回，避免主题切换失败影响正在使用的窗口。
        if (index < 0) return;

        var target = merged[index];

        var replacement = new ResourceDictionary
        {
            Source = new Uri(
                $"/{Constants.AppName};component/Themes/{theme}.xaml",
                UriKind.Relative)
        };

        target.Clear();
        foreach (var key in replacement.Keys)
            target[key] = replacement[key];
    }
}
