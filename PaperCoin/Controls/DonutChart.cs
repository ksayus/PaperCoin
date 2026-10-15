using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LAE;
using PaperCoin.ViewModels;

namespace PaperCoin.Controls;

/// <summary>
/// 分类占比圆环。纯自绘：每个分段是一个 <see cref="Path"/>，几何由
/// <see cref="PathGeometry"/> + <see cref="ArcSegment"/> 直接算出来，不引入图表库。
/// <para>
/// 控件只负责"把角度画对"，不做任何聚合：每段的角度来自
/// <see cref="DonutSegment.Fraction"/>，标签与金额已经在 ViewModel 里拼好。
/// </para>
/// <para>
/// 动画不重建几何。逐帧重算弧线会每帧新建几何对象、让 WPF 反复重建渲染数据，
/// 这里改成对**预先算好的**弧线做描边宽度展开，并按顺序错开：
/// 描边宽度是常量依赖属性，LAE 写它不产生任何几何重建。
/// </para>
/// <para>
/// 继承 <see cref="Canvas"/> 而不是 <see cref="FrameworkElement"/>：弧线要以
/// <see cref="Panel.Children"/> 的形式挂进来，让布局系统自己去量、去摆。
/// 早先的版本自己调 <c>AddVisualChild</c> 挂可视子元素，但从不为它们调 <c>Arrange</c> ——
/// 那些弧线的 <c>RenderSize</c> 一直是 0×0，渲染时被整批跳过，界面上一个环都看不到，
/// 而且不报任何错。这属于"挂上去 ≠ 画出来"。
/// </para>
/// </summary>
public sealed class DonutChart : Canvas
{
    /// <summary>段与段之间留的缝（弧度）。不留缝时相邻两段颜色接近就看不出分界。</summary>
    private const double GapRadians = 0.022;

    /// <summary>环上只有一段时不留缝，否则会看到一道缺口。</summary>
    private const double SingleSegmentGap = 0.0;

    private const double RevealMs = 420;
    private const double StaggerMs = 55;

    /// <summary>最小段角度（弧度）。占比为 0 的极端数据不该画出一条负角度或消失的弧。</summary>
    private const double MinSweep = 0.02;

    private readonly List<Path> _segmentPaths = [];

    private double _builtWidth = double.NaN;
    private double _builtHeight = double.NaN;

    /// <summary>
    /// 本控件是否已经展开过。
    /// <para>
    /// 用途见 <see cref="Rebuild"/>：尺寸一变就要重建弧线，而新建弧线的描边宽度是 0
    /// （展开动画的起点）。展开过之后再重建就必须直接落到最终宽度，否则窗口一拉伸，
    /// 整个环就消失，而且不报任何错。
    /// </para>
    /// </summary>
    private bool _revealed;

    /// <summary>已经播完展开动画的分段数，供 tools/UiSmokeTest 断言动效真的接上了</summary>
    public int SegmentsPlayed { get; private set; }

    /// <summary>
    /// 冻结展开动画：期间重建的弧线直接落到最终描边宽度，不经过 0 起点。
    /// 供截图与静态核对使用 —— 没有渲染循环的宿主里 LAE 时钟不推进，
    /// 走动画的话截出来永远是一个空环。
    /// </summary>
    internal bool AnimationFrozen { get; set; }


    /// <summary>每段实际画出的角度（弧度），供测试核对角度分配与占比一致</summary>
    internal IReadOnlyList<double> SegmentSweeps { get; private set; } = [];

    /// <summary>最近一次重建的结果，供 tools/UiSmokeTest 定位"为什么一条弧都没有"</summary>
    internal string LastRebuildDiagnostic { get; private set; } = "(尚未重建)";

    // ---- 依赖属性 ----

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource), typeof(IEnumerable), typeof(DonutChart),
            new PropertyMetadata(null, OnItemsSourceChanged));

    /// <summary>分段集合，元素类型为 <see cref="DonutSegment"/></summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty RingThicknessProperty =
        DependencyProperty.Register(
            nameof(RingThickness), typeof(double), typeof(DonutChart),
            new PropertyMetadata(26.0, OnRingThicknessChanged));

    /// <summary>环的粗细（像素），即弧线描边宽度</summary>
    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    public static readonly DependencyProperty PlayedProperty =
        DependencyProperty.Register(
            nameof(Played), typeof(bool), typeof(DonutChart),
            new PropertyMetadata(false, OnPlayedChanged));

    /// <summary>
    /// 展开动画的开关。数据到货时 View 把它置 true 触发播放；
    /// 用依赖属性而不是直接调方法，是为了让"何时播"由 View 的布局时机决定，
    /// 控件不必猜自己有没有被真正挂上可视树。
    /// </summary>
    public bool Played
    {
        get => (bool)GetValue(PlayedProperty);
        set => SetValue(PlayedProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (DonutChart)d;

        if (e.OldValue is INotifyCollectionChanged oldNotify)
            oldNotify.CollectionChanged -= chart.OnItemsChanged;

        if (e.NewValue is INotifyCollectionChanged newNotify)
            newNotify.CollectionChanged += chart.OnItemsChanged;

        chart.SegmentsPlayed = 0;
        chart.Rebuild(chart.RenderSize);
    }

    private static void OnRingThicknessChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DonutChart)d).Rebuild(((DonutChart)d).RenderSize);

    private static void OnPlayedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        var chart = (DonutChart)d;

        // 还没画过弧线（布局未完成或数据未到）时不能置 _revealed：置了以后
        // 第一次重建会直接落到最终宽度，展开动画就永远不会播了。
        if (chart._segmentPaths.Count == 0) return;

        chart.RevealAll();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SegmentsPlayed = 0;
        Rebuild(RenderSize);
    }

    // ---- 构建 ----

    /// <summary>
    /// 按给定尺寸与当前数据重建全部弧线。
    /// 尺寸必须由调用方传入，不能在这里读 <see cref="FrameworkElement.ActualWidth"/>：
    /// 布局过程中 <c>ActualWidth</c> 还没被赋值，在 <see cref="ArrangeOverride"/> 里读到的
    /// 是 0，症状是一条弧都画不出来且没有任何报错。
    /// </summary>
    private void Rebuild(Size size)
    {
        Children.Clear();
        _segmentPaths.Clear();

        if (ItemsSource is not IEnumerable source || size.Width <= 0 || size.Height <= 0)
        {
            LastRebuildDiagnostic =
                $"提前返回: source={(ItemsSource is null ? "null" : "有")} size={size.Width:0.#}×{size.Height:0.#}";
            SegmentSweeps = [];
            return;
        }

        var segments = source.Cast<object>().OfType<DonutSegment>().ToList();
        if (segments.Count == 0)
        {
            LastRebuildDiagnostic = $"提前返回: 分段空, 集合元素 {source.Cast<object>().Count()} 个";
            SegmentSweeps = [];
            return;
        }

        // 描边画在路径中心线上，半径要往里缩半个笔宽，否则外缘会被裁掉
        double radius = (Math.Min(size.Width, size.Height) - RingThickness) / 2;
        if (radius <= 0)
        {
            LastRebuildDiagnostic = $"提前返回: 半径 {radius:0.#} <= 0 (笔宽 {RingThickness:0.#})";
            SegmentSweeps = [];
            return;
        }

        LastRebuildDiagnostic = $"画出 {segments.Count} 段, 半径 {radius:0.#}";
        var center = new Point(size.Width / 2, size.Height / 2);
        double gap = segments.Count == 1 ? SingleSegmentGap : GapRadians;

        double totalFraction = segments.Sum(s => s.Fraction);
        if (totalFraction <= 0) totalFraction = 1;

        var sweeps = new List<double>();
        double cursor = -Math.PI / 2;   // 12 点方向

        foreach (var segment in segments)
        {
            double sweep = Math.Max(MinSweep, segment.Fraction / totalFraction * (Math.PI * 2));
            double drawn = Math.Max(MinSweep / 2, sweep - gap);

            // 起点固定在 -90°（12 点），并按金额降序排列：占比最大的那一块从正上方开始
            var path = CreateSegmentPath(center, radius, cursor, drawn, segment);

            _segmentPaths.Add(path);
            Children.Add(path);

            // 间隔在绘制之后推进：留缝是从这一段末尾扣掉的，不是从下一段开头
            cursor += sweep;
            sweeps.Add(drawn);
        }

        SegmentSweeps = sweeps;

        // 冻结中，或已经展开过：新弧线直接落到最终描边宽度。不补这一步，窗口尺寸一变
        // 环就整条消失（新弧线的起点宽度是 0，不会再有人来推进它）。
        if (AnimationFrozen || _revealed)
        {
            foreach (var path in _segmentPaths)
                path.StrokeThickness = RingThickness;
        }
    }

    private Path CreateSegmentPath(
        Point center, double radius, double startAngle, double sweep, DonutSegment segment)
    {
        double endAngle = startAngle + sweep;

        var figure = new PathFigure
        {
            StartPoint = OnCircle(center, radius, startAngle),
            IsClosed = false,
            IsFilled = false
        };

        figure.Segments.Add(new ArcSegment
        {
            Point = OnCircle(center, radius, endAngle),
            Size = new Size(radius, radius),
            // 超过半圈必须置位，否则 WPF 会走短的那一边
            IsLargeArc = sweep > Math.PI,
            SweepDirection = SweepDirection.Clockwise
        });

        var path = new Path
        {
            Data = new PathGeometry([figure]),

            // 展开动画从 0 描边开始，静止值由 RevealAll 写到 RingThickness。
            // 冻结时直接给最终值，否则渲染出来是个空环。
            StrokeThickness = AnimationFrozen ? RingThickness : 0,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
            ToolTip = segment.Tooltip
        };

        // 颜色是主题键，必须走 DynamicResource 才能在主题切换时跟着一起过渡
        path.SetResourceReference(Shape.StrokeProperty, segment.ColorKey);

        return path;
    }

    private static Point OnCircle(Point center, double radius, double angle)
        => new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    // ---- 布局 ----

    /// <summary>
    /// 尺寸在 Arrange 阶段才最终确定，而半径依赖最终尺寸，所以几何重建放这里。
    /// <para>
    /// 注意 <see cref="FrameworkElement.ActualWidth"/> 在这一刻还没被赋值，必须用
    /// <paramref name="finalSize"/>；重建之后再交给基类摆放弧线
    /// （这一步不能省：不被 Arrange 的子元素 RenderSize 是 0，渲染时会被跳过）。
    /// </para>
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        // 首次进入时 _built 是 NaN，而任何与 NaN 的比较都是 false，
        // 所以必须先判 NaN，否则第一次布局永远不重建。
        if (double.IsNaN(_builtWidth)
            || double.IsNaN(_builtHeight)
            || Math.Abs(finalSize.Width - _builtWidth) > 0.5
            || Math.Abs(finalSize.Height - _builtHeight) > 0.5)
        {
            _builtWidth = finalSize.Width;
            _builtHeight = finalSize.Height;
            Rebuild(finalSize);
        }

        return base.ArrangeOverride(finalSize);
    }

    // ---- 动画 ----

    /// <summary>
    /// 播放展开：每段的描边宽度从 0 展开到 <see cref="RingThickness"/>，按顺序错开。
    /// 起始宽度写死为 0，否则重播会从上一轮的收尾值起算，成为空转。
    /// </summary>
    public void RevealAll()
    {
        if (_segmentPaths.Count == 0) return;

        SegmentsPlayed = 0;
        _revealed = true;

        var builder = LA.Builder("StatisticsDonut").Ease(Easing.OutCubic);

        for (int i = 0; i < _segmentPaths.Count; i++)
        {
            var path = _segmentPaths[i];

            path.StrokeThickness = 0;

            // Delay 只作用于下一个动作，所以每段各写一次
            builder
                .Delay(i * StaggerMs)
                .To(path, Shape.StrokeThicknessProperty, RingThickness, RevealMs)
                .Callback(() => SegmentsPlayed++);
        }

        builder.Play();
    }

    /// <summary>
    /// 直接落到展开完成的状态，不播动画。
    /// <para>
    /// 供截图模式使用：截图宿主里没有渲染循环，LAE 的时钟不推进，走动画的话
    /// 描边宽度会停在 0，截出来的环永远是空的 —— 那样截图就失去了核对的价值。
    /// 这也正好覆盖 <see cref="Rebuild"/> 里"展开过之后再重建"那条路径。
    /// </para>
    /// </summary>
    internal void SnapToRevealed()
    {
        _revealed = true;
        SegmentsPlayed = _segmentPaths.Count;

        foreach (var path in _segmentPaths)
            path.StrokeThickness = RingThickness;
    }
}
