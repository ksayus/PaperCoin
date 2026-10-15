namespace PaperCoin.Core;

/// <summary>
/// 分类到颜色的稳定映射。
/// <para>
/// 只产出键名，不产出颜色值：颜色字面量归主题字典所有（<c>Themes/Dark.xaml</c> 与
/// <c>Themes/Bright.xaml</c>），这样才能跟着主题切换一起过渡。因此本类属于 Core ——
/// 它表达的是"同名分类在任何视图、任何时间范围里都得到同一种颜色"这条领域约定，
/// 而不是渲染细节。
/// </para>
/// <para>
/// 映射按名字求哈希，不按出现顺序分配：统计页切换时间范围后分类集合会变，
/// 按顺序分配会让同一个分类换一个范围就换一种颜色。
/// </para>
/// </summary>
public static class CategoryPalette
{
    /// <summary>可选颜色槽位数，与主题字典里 <c>CategoryColor01</c>…<c>CategoryColor10</c> 对应。</summary>
    public const int SlotCount = 10;

    /// <summary>分类为空时的归并名。仓库聚合与界面显示共用这一个写法。</summary>
    public const string Uncategorized = "未分类";

    /// <summary>
    /// 按分类名取颜色键（<c>CategoryColor01</c> 这种形式）。
    /// 空白分类固定落在 <c>CategoryColorOther</c>，与有名字的分类区分开：未分类是一类事实，
    /// 不该被哈希随机分到某个颜色上。
    /// </summary>
    public static string ThemeKeyOf(string? category)
        => string.IsNullOrWhiteSpace(category) ? ThemeKeyOfSlot(SlotCount) : ThemeKeyOfSlot(SlotOf(category));

    /// <summary>把分类名归一化成显示用的名字，空白统一成 <see cref="Uncategorized"/>。</summary>
    public static string Normalize(string? category)
        => string.IsNullOrWhiteSpace(category) ? Uncategorized : category.Trim();

    /// <summary>分类名落在哪个槽位，取值 <c>[0, SlotCount)</c>，与大小写和首尾空格无关。</summary>
    public static int SlotOf(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return SlotCount;

        // FNV-1a。不用 string.GetHashCode：.NET Core 对字符串哈希做了每进程随机化，
        // 用它会让同一份账单在每次启动后换一套配色。
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        uint hash = offsetBasis;
        foreach (char c in category.Trim())
        {
            // 大写字母折成小写，让 "Food" 与 "food" 落到同一个槽位
            char lower = char.ToLowerInvariant(c);
            hash = (hash ^ (byte)lower) * prime;
            hash = (hash ^ (byte)(lower >> 8)) * prime;
        }

        return (int)(hash % SlotCount);
    }

    /// <summary>槽位号取颜色键。槽位等于 <see cref="SlotCount"/> 时返回未分类专用的键。</summary>
    public static string ThemeKeyOfSlot(int slot)
        => slot >= SlotCount ? "CategoryColorOther" : $"CategoryColor{slot + 1:00}";

    /// <summary>全部颜色键，升序。主题字典与测试共用这一份清单。</summary>
    public static IReadOnlyList<string> AllThemeKeys { get; } =
        [.. Enumerable.Range(0, SlotCount + 1).Select(ThemeKeyOfSlot)];
}
