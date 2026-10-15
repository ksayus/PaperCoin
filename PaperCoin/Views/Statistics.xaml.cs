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


    /// <summary>排行条的最小可见宽度：占比极小的分类也要能看到一条，否则读成"没有数据"</summary>
    private const double MinBarWidth = 4;

    /// <summary>趋势柱的最高高度，与 XAML 里那条 RowDefinition 的高度一致</summary>
    private const double TrendMaxHeight = 100;

    /// <summary>
    /// 展开动画的注册名。LAE 的分组表全进程共享，加页面前缀避免与其它页面的过渡互相停掉。
    /// </summary>
    internal const string RevealAnimationName = "StatisticsReveal";



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

    /// <summary>
    /// 选中分类后把"分类明细"滚进视野。
    /// <para>
    /// 明细在页面最下面，不滚的话用户点完排行/图例只觉得"什么都没发生" ——
    /// 内容确实更新了，只是不在屏幕上。滚动放在 DataBind 之后，等明细列表的行生成完，
    /// 否则量到的可滚动高度还是旧的，滚不到位。
    /// </para>
    /// </summary>
    private void ScrollToCategoryDetail()
    {
        if (Vm?.HasCategorySelection != true) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            DetailScroll.UpdateLayout();

            // 算目标是"让整块刚好露出来"，不是无脑滚到底：
            // 滚到底会把明细下面那点内容顶上来，用户刚点的排行反而被推走。
            var origin = DetailSection.TransformToAncestor(DetailScroll)
                .Transform(new Point(0, 0));

            double current = DetailScroll.VerticalOffset;
            double viewport = DetailScroll.ViewportHeight;
            double sectionTop = origin.Y + current;
            double sectionBottom = sectionTop + DetailSection.ActualHeight;

            // 已经完整可见就不动，避免每次刷新都把用户的滚动位置拽走
            if (sectionTop >= current && sectionBottom <= current + viewport) return;

            DetailScroll.ScrollToVerticalOffset(
                Math.Min(sectionBottom - viewport, DetailScroll.ScrollableHeight));
        }), DispatcherPriority.Loaded);
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

            // 选中了分类：把明细滚进视野，否则用户点完不知道发生了什么
            case nameof(StatisticsViewModel.SelectedCategory):
                ScrollToCategoryDetail();
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


    // ---- 自动化验证入口（仅供 tools/UiSmokeTest 使用，internal + InternalsVisibleTo）----

    /// <summary>取排行的第一行按钮，供测试驱动下钻。</summary>
    internal Button? FindRankRowForTest()
        => Descendants<Button>(RankList, "").FirstOrDefault(b => b.DataContext is CategoryRankRow);

    /// <summary>
    /// 让三块图表直接落到展开完成的状态，不播动画。
    /// 截图宿主里没有渲染循环、LAE 时钟不推进，走动画的话圆环的描边宽度会停在 0，
    /// 截出来的图看不出问题，截图就失去了核对的价值。
    /// </summary>
    internal void SettleChartsForTest()
    {
        // 先把可见性落地：环所在的 Grid 绑定了 HasExpense，而数据是异步到的，
        // 到截图这一刻绑定未必已经传播完 —— 那时 Grid 还是 Collapsed，弧线画了也看不见。
        Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

        // 冻结展开动画：宿主里 LAE 时钟不推进，不冻结的话弧线永远停在 0 宽度
        CategoryDonut.AnimationFrozen = true;

        // 再跑一次真实布局：弧线是在 Arrange 里按最终尺寸生成的
        UpdateLayout();

        CategoryDonut.SnapToRevealed();

        var bars = Descendants<Border>(RankList, "RankBar").ToList();
        var rows = Vm?.VisibleRankRows ?? [];

        for (int i = 0; i < bars.Count && i < rows.Count; i++)
            bars[i].Width = Math.Max(MinBarWidth, rows[i].BarFraction * RankList.ActualWidth);

        var trendBars = Descendants<Border>(TrendList, "TrendBar").ToList();
        var trend = Vm?.TrendBars ?? [];

        for (int i = 0; i < trendBars.Count && i < trend.Count; i++)
            trendBars[i].Height = Math.Max(3, trend[i].HeightFraction * TrendMaxHeight);

        UpdateLayout();
    }

    /// <summary>
    /// 截图模式：选中第一个分类并滚到"分类明细"那一块。
    /// 明细在页面最下面，不选中的话截出来只有一句引导语，看不出明细到底画没画出来。
    /// </summary>
    internal void ScrollToCategoryDetailForTest()
    {
        if (Vm is null) return;

        if (!Vm.HasCategorySelection && Vm.Categories.Count > 0)
            Vm.ShowCategoryCommand.Execute(Vm.Categories[0]);

        // 等绑定把明细列表生成出来，再滚到底
        Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        UpdateLayout();

        DetailScroll.ScrollToEnd();
        UpdateLayout();
    }

    /// <summary>截图模式：分类明细区块的实际状态</summary>
    internal string CategoryDetailSnapshotForTest
    {
        get
        {
            var texts = new List<TextBlock>();
            CollectDescendants(CategoryDetailList, texts);

            return $"选中={Vm?.SelectedCategory ?? "(无)"} 数据={(Vm?.CategoryEntries.Count ?? 0)} 条 " +
                   $"画出的行={texts.Count} 区块可见={CategoryDetailList.IsVisible} " +
                   $"尺寸={CategoryDetailList.ActualWidth:0.#}×{CategoryDetailList.ActualHeight:0.#} " +
                   $"滚动位置={DetailScroll.VerticalOffset:0.#}/{DetailScroll.ScrollableHeight:0.#}";
        }
    }

    /// <summary>当前纵向滚动位置，供测试断言"选中分类后会自动滚到明细"</summary>
    internal double ScrollOffsetForTest => DetailScroll.VerticalOffset;

    /// <summary>截图模式用的图表状态，用来判断"环没画出来"是布局没跑还是动画没播</summary>
    internal string DonutSnapshot
    {
        get
        {
            var paths = new List<System.Windows.Shapes.Path>();
            CollectDescendants(CategoryDonut, paths);

            var detail = string.Join(" ", paths.Select(p =>
                $"[{p.StrokeThickness:0.#}px {(p.Stroke as SolidColorBrush)?.Color.ToString() ?? "无画刷"}]"));

            return $"环: 段数={CategoryDonut.SegmentSweeps.Count} " +
                   $"子元素={VisualTreeHelper.GetChildrenCount(CategoryDonut)} " +
                   $"尺寸={CategoryDonut.ActualWidth:0.#}×{CategoryDonut.ActualHeight:0.#} " +
                   $"可见={CategoryDonut.IsVisible} 诊断={CategoryDonut.LastRebuildDiagnostic} {detail}";
        }
    }

    // ---- 可视树工具 ----

    /// <summary>按类型深度优先收集可视后代</summary>
    private static void CollectDescendants<T>(DependencyObject root, List<T> into)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) into.Add(match);
            CollectDescendants(child, into);
        }
    }

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
