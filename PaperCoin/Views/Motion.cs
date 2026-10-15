using System.Windows.Media;
using LAE;

namespace PaperCoin.Views;

/// <summary>
/// 全项目动效的节奏与缓动。各处动画的时长不再各写各的数字，统一从这里取。
/// <para>
/// 三档时长对应三种语义：<see cref="Fast"/> 是"控件对指针的直接反馈"，
/// <see cref="Normal"/> 是"状态换了，内容要移动到位"，<see cref="Playful"/> 是带过冲的
/// 收尾。缓动的用法也固定下来：进场用 <see cref="EaseIn"/>（快速起步、缓慢落定），
/// 往复用 <see cref="EaseBoth"/>（两头都慢），带回弹的落定用 <see cref="EaseSettle"/>。
/// </para>
/// <para>
/// 时长写得比常见值略短是有意的：这个窗口只有 420px 宽，动画跨过的距离都在几十像素内，
/// 300ms 以上会读成"卡"而不是"顺"。
/// </para>
/// </summary>
internal static class Motion
{
    /// <summary>直接反馈：指针进入、按下、颜色提亮</summary>
    public const double Fast = 110;

    /// <summary>状态切换：内容落定、面板开合、位置滑动</summary>
    public const double Normal = 200;

    /// <summary>带过冲的收尾：按钮回弹、卡片入场</summary>
    public const double Playful = 380;

    /// <summary>较长的展开：图表分段、柱状图长出来</summary>
    public const double Reveal = 460;

    /// <summary>进场：快速起步、缓慢落定</summary>
    public static Func<double, double> EaseIn => Easing.OutCubic;

    /// <summary>往复：两头都慢，用于"移过去再移回来"这类对移动</summary>
    public static Func<double, double> EaseBoth => Easing.InOutCubic;

    /// <summary>落定：越过目标一点再落回，让收尾有重量</summary>
    public static Func<double, double> EaseSettle => Easing.OutBack(2.0);

    /// <summary>
    /// 「按下去 → 弹回来」的两段式回弹。
    /// <para>
    /// 不写成单段 overshoot：单段缓动的终点会停在与超调绑定的值上，回不到静止值。
    /// 拆成去程与回程两段，终点才是精确的 <paramref name="rest"/>。
    /// </para>
    /// </summary>
    /// <param name="scale">目标缩放变换</param>
    /// <param name="peak">回弹峰值</param>
    /// <param name="durationMs">总时长，去程占 35%，回程占 65%</param>
    /// <param name="rest">静止值，通常为 1.0</param>
    public static void PlayElastic(ScaleTransform scale, double peak, double durationMs = Playful, double rest = 1.0)
    {
        LA.Builder()
            .Ease(EaseBoth)
            .Scale(scale, peak, durationMs * 0.35)
            .Then()
            .Ease(EaseSettle)
            .Scale(scale, rest, durationMs * 0.65)
            .Play();
    }
}
