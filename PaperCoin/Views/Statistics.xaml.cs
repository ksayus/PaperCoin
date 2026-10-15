using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LAE;
using PaperCoin.ViewModels;

namespace PaperCoin.Views;

/// <summary>
/// Statistics 的视图逻辑。动画目标是可视树对象、宽度要按容器实宽现算，所以聚合在
/// <see cref="StatisticsViewModel"/>，播放放在这里。
/// <para>
/// 三处展开动画（圆环、排行条、趋势柱）都由代码后置按实际尺寸算目标值再交给 LAE：
/// 条形宽度依赖运行时容器宽度，写在 XAML 里的绑定拿不到这个数。
/// </para>
/// </summary>
public partial class Statistics : UserControl
{
    // ---- 节奏。与项目其它页面同一套：进入 OutCubic、落定 OutBack 收尾。 ----

    private const double BarGrowMs = 460;
    private const double BarStaggerMs = 45;
    private const double TrendGrowMs = 520;
    private const double TrendStaggerMs = 38;
    private const double PanelMs = 220;

    /// <summary>排行条的最小可见宽度：占比极小的分类也要能看到一条，否则读成"没有数据"</summary>
    private const double MinBarWidth = 4;

    /// <summary>趋势柱的最高高度，与 XAML 里那条 RowDefinition 的高度一致</summary>
    private const double TrendMaxHeight = 100;

    /// <summary>
    /// 展开动画的注册名。LAE 的分组表全进程共享，加页面前缀避免与其它页面的过渡互相停掉。
    /// </summary>
    internal const string RevealAnimationName = "StatisticsReveal";

    internal const string FocusAnimationName = "StatisticsFocus";

    /// <summary>圆环展开的实际播放段数，供 tools/UiSmokeTest 断言动效真的接上了</summary>
    internal int DonutPlayCount => CategoryDonut.SegmentsPlayed;

    /// <summary>排行条展开的实际播放条数，同上</summary>
    internal int RankBarsPlayCount { get; private set; }

    /// <summary>趋势柱展开的实际播放条数，同上</summary>
    internal int TrendBarsPlayCount { get; private set; }

    private bool _revealed;

    private StatisticsViewModel? Vm => DataContext as StatisticsViewModel;

    public Statistics()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is StatisticsViewModel oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;

        if (e.NewValue is StatisticsViewModel newVm)
            newVm.PropertyChanged += OnViewModelPropertyChanged;

        // 页面实例被 MainWindow 长期缓存，换数据源要复位，否则切回来不会再展开一次
        _revealed = false;
    }

    /// <summary>
    /// 解除订阅。MainWindow 长期缓存页面实例而每次导航都取新的 ViewModel，
    /// 不退订会把翻过的每一份统计数据都留在内存里。
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Vm is not null)
            Vm.PropertyChanged -= OnViewModelPropertyChanged;

        _revealed = false;
    }

    // ---- 载入与展开 ----

    /// <summary>
    /// 由 MainWindow 在切到本页后调用。统计页不订阅仓库，靠"索引版本变了才重算"
    /// 决定要不要重新聚合，所以每次进页面都要走一次这个入口。
    /// </summary>
    internal async Task RefreshAsync()
    {
        if (Vm is null) return;

        // 重新载入前先收起旧的展开状态：不清的话新数据会从上一轮的收尾值起算
        _revealed = false;

        await Vm.EnsureFreshAsync();

        // 推迟到 Render 优先级：此刻容器还没生成，AnimateBars 取不到任何行。
        // 返回值丢弃写法：本方法是 async，不丢弃会报 CS4014。
        _ = Dispatcher.BeginInvoke(new Action(Reveal), DispatcherPriority.Render);
    }

    /// <summary>播放全部展开动画。同一轮里重复调用只播一次。</summary>
    private void Reveal()
    {
        if (_revealed) return;

        // 数据还没到或者没有支出时没有可播的元素，保持未播状态，等数据到货那一次通知再来
        if (Vm is null) return;

        _revealed = true;

        CategoryDonut.Played = true;
        AnimateRankBars();
        AnimateTrendBars();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // 数据换了：重新展开。放在 Render 优先级是因为此刻容器尚未重建。
            case nameof(StatisticsViewModel.Segments):
                _revealed = false;
                Dispatcher.BeginInvoke(new Action(Reveal), DispatcherPriority.Render);
                break;

            case nameof(StatisticsViewModel.IsFocusOpen):
                PlayFocusTransition();
                break;
        }
    }

    // ---- 排行条 ----

    /// <summary>
    /// 排行条从 0 长到 占比 × 内容宽度。逐行错开，整片看起来是一波而不是一次齐动。
    /// </summary>
    private void AnimateRankBars()
    {
        RankBarsPlayCount = 0;

        double available = RankList.ActualWidth;
        if (available <= 0) return;

        // 展开/收起会重建容器，这里按显示顺序取，与 VisibleRankRows 一一对应
        var bars = Descendants<Border>(RankList, "RankBar").ToList();
        if (bars.Count == 0) return;

        var rows = Vm?.VisibleRankRows ?? [];

        var builder = LA.Builder($"{RevealAnimationName}_Rank").Ease(Easing.OutCubic);

        for (int i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];

            double fraction = i < rows.Count ? rows[i].BarFraction : 0;
            double target = Math.Max(MinBarWidth, fraction * available);

            // 起点写死：不写会从上一轮的收尾值起算，重播成为空转
            bar.Width = 0;

            builder
                .Delay(i * BarStaggerMs)
                .To(bar, FrameworkElement.WidthProperty, target, BarGrowMs)
                .Callback(() => RankBarsPlayCount++);
        }

        builder.Play();
    }

    // ---- 趋势柱 ----

    /// <summary>
    /// 趋势柱从 0 弹起到 占比 × 最高高度。用 OutBack 让柱子越过目标一点再落回，
    /// 读起来是"长出来"而不是"变出来"。
    /// </summary>
    private void AnimateTrendBars()
    {
        TrendBarsPlayCount = 0;

        var bars = Descendants<Border>(TrendList, "TrendBar").ToList();
        if (bars.Count == 0) return;

        var data = Vm?.TrendBars ?? [];

        var builder = LA.Builder($"{RevealAnimationName}_Trend").Ease(Easing.OutBack(1.6));

        for (int i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];

            double fraction = i < data.Count ? data[i].HeightFraction : 0;
            double target = Math.Max(3, fraction * TrendMaxHeight);

            bar.Height = 0;

            builder
                .Delay(i * TrendStaggerMs)
                .To(bar, FrameworkElement.HeightProperty, target, TrendGrowMs)
                .Callback(() => TrendBarsPlayCount++);
        }

        builder.Play();
    }

    // ---- 排行下钻 ----

    // ---- 下钻面板开合 ----

    /// <summary>
    /// 面板缩放 + 上浮入场。只在打开时播：关闭时 <see cref="UIElement.Visibility"/> 立刻变
    /// <c>Collapsed</c>，再播退场动画也看不到。
    /// </summary>
    private void PlayFocusTransition()
    {
        if (Vm?.IsFocusOpen != true) return;

        FocusShift.Y = 10;
        FocusScale.ScaleX = 0.96;
        FocusScale.ScaleY = 0.96;

        LA.Builder(FocusAnimationName)
            .Ease(Easing.OutCubic)
            .Move(FocusShift, 0, 0, PanelMs)
            .Play();

        LA.Builder($"{FocusAnimationName}_Scale")
            .Ease(Easing.OutBack(2.0))
            .Scale(FocusScale, 1.0, PanelMs * 1.4)
            .Play();
    }

    // ---- 自动化验证入口（仅供 tools/UiSmokeTest 使用，internal + InternalsVisibleTo）----

    /// <summary>取排行的第一行按钮，供测试驱动下钻。</summary>
    internal Button? FindRankRowForTest()
        => Descendants<Button>(RankList, "").FirstOrDefault(b => b.DataContext is CategoryRankRow);

    // ---- 可视树工具 ----

    /// <summary>
    /// 按名字深度优先收集指定类型的后代元素，顺序为文档顺序。
    /// <paramref name="name"/> 传空串表示只按类型匹配。
    /// </summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match && (name.Length == 0 || match.Name == name))
                yield return match;

            foreach (var nested in Descendants<T>(child, name))
                yield return nested;
        }
    }
}
