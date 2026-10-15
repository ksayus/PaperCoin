using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LAE;
using PaperCoin.ViewModels;

namespace PaperCoin.Views;

/// <summary>
/// CalenderBill 的视图逻辑。
/// </summary>
public partial class CalenderBill : UserControl
{
    private const double HoverScale = 1.05;

    /// <summary>内容落定的起点上浮距离</summary>
    private const double SettleRise = 6;

    /// <summary>粒度按钮按下的回弹峰值</summary>
    private const double ModePressPeak = 1.12;

    /// <summary>"+ 号"按下的回弹峰值</summary>
    private const double AddNewBillPeak = 1.18;

    /// <summary>
    /// 明细卡片悬浮时的放大倍数。它横向占满列表宽度，放大比例决定左右各溢出多少，
    /// 越出视口的部分会被 ScrollViewer 裁掉，所以比月卡片收敛。
    /// </summary>
    private const double EntryHoverScale = 1.03;

    /// <summary>
    /// 卡片悬浮缩放的峰值上限，供 tools/UiSmokeTest 验证放大后不越出滚动视口。
    /// CalenderBill.xaml 给明细列表留的横向余量必须 ≥ 这个值。
    /// </summary>
    internal const double MaxHoverScale = EntryHoverScale;

    // ---- 空状态提示 ----

    /// <summary>空状态提示的起始上浮距离，与明细列表入场的位移同一节奏</summary>
    private const double EmptyHintRise = 6;

    /// <summary>
    /// 空状态提示过渡的注册名。LAE 的分组表全进程共享，加页面前缀避免两个页面互相停掉对方的过渡。
    /// </summary>
    internal const string EmptyHintAnimationName = "CalendarEmptyHint";

    /// <summary>空状态提示是否已经播过，避免同一轮刷新里重复起动画</summary>
    private bool _emptyHintShown;

    /// <summary>明细卡片的入场（上浮 + 淡入，按顺序错开），实现见 <see cref="CardListEntrance"/></summary>
    private readonly CardListEntrance _entryEntrance;

    /// <summary>
    /// 首屏那一批数据是否已经处理过。首屏到货算入场，交给卡片逐个入场并错开；
    /// 之后的翻页与换粒度才是内容替换，那时才走 <see cref="Settle"/>。
    /// </summary>
    private bool _firstLoadHandled;

    /// <summary>
    /// 空状态提示入场的实际播放次数，供 tools/UiSmokeTest 断言动效真的接上了。
    /// 计数与时钟无关：LAE 按真实时间推进，测试里那一轮 IO 若跨过 280ms，动画会在一帧内跑完。
    /// </summary>
    internal int EmptyHintPlayCount { get; private set; }

    /// <summary>明细卡片入场的实际播放张数，同上</summary>
    internal int EntryEntrancePlayCount => _entryEntrance.PlayCount;

    /// <summary>落定动画重入保护。快速连点翻页时不让两段动画互相打断。</summary>
    private bool _settling;

    /// <summary>季节卡片入场是否已经播过（整页只播一次，之后换月不再重复）</summary>
    private bool _yearEntrancePlayed;

    private CalenderBillViewModel? Vm => DataContext as CalenderBillViewModel;

    public CalenderBill()
    {
        InitializeComponent();

        _entryEntrance = new CardListEntrance(EntryList);

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is CalenderBillViewModel oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;

        if (e.NewValue is CalenderBillViewModel newVm)
            newVm.PropertyChanged += OnViewModelPropertyChanged;

        // 新数据源的容器要等下一次布局才生成，这里先挂上入场钩子，好在卡片第一次被画出来之前写好起点
        _firstLoadHandled = false;
        _entryEntrance.Reset();
        _entryEntrance.TryPlay();

        // ViewModel 构造时就发起了首屏加载，数据可能早于本控件就绪，这里用 Loaded 优先级补播一次
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 首屏不做容器落定：数据可能比这次回调更早到，有数据时由卡片逐个入场，空的时候由空提示入场
            RequestEmptyHint();
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 解除订阅。MainWindow 长期缓存页面实例而每次导航都会取新的 ViewModel，不退订会把翻过的每一页都留在内存里。
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Vm is not null)
            Vm.PropertyChanged -= OnViewModelPropertyChanged;

        // 页面实例被缓存，不在换页时复位，入场就只在本次进程第一次进入时看得到
        _entryEntrance.Reset();
        _firstLoadHandled = false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // 换粒度：主体重新落定，并给卡片一次入场
            case nameof(CalenderBillViewModel.Mode):
                Settle();
                if (Vm?.IsYearMode == true && !_yearEntrancePlayed)
                {
                    _yearEntrancePlayed = true;
                    Dispatcher.BeginInvoke(new Action(AnimateCardsIn), DispatcherPriority.Loaded);
                }
                break;

            // 翻页 / 首屏到货：首屏那一批交给卡片自己的入场，不走容器落定
            case nameof(CalenderBillViewModel.CountText):
                if (!_firstLoadHandled)
                {
                    _firstLoadHandled = true;
                    break;
                }

                Settle();
                break;

            // 明细空/非空切换：空提示有自己的入场。连续两次为空时不重播，那一次过渡由 Settle 负责；
            // 从空变成有货就是卡片入场的时候
            case nameof(CalenderBillViewModel.IsEmpty):
                if (Vm?.IsEmpty == true)
                {
                    RequestEmptyHint();
                }
                else
                {
                    _emptyHintShown = false;
                    _entryEntrance.TryPlay();
                }
                break;
        }
    }

    /// <summary>
    /// 请求播放空状态提示的入场。推迟到 Render 优先级再决定：Visibility 由绑定驱动，
    /// ViewModel 抛通知的那一刻绑定未必已经落地，读到的可能是上一轮的值。
    /// </summary>
    private void RequestEmptyHint()
    {
        if (_emptyHintShown) return;
        _emptyHintShown = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Vm?.IsEmpty != true || EmptyHint.Visibility != Visibility.Visible)
            {
                // 已经又有数据：复位，下次真的空了还能再播
                _emptyHintShown = false;
                return;
            }

            // 起点要先写死，否则会从上一轮动画的收尾值起算（那时 Opacity 已是 1，淡入成为空转）
            EmptyHintShift.Y = EmptyHintRise;
            EmptyHint.Opacity = 0;

            // 位移与淡入放进同一个动画组：同名的组会被后者替换，拆成两次 Play 会互相停掉
            LA.Builder(EmptyHintAnimationName)
                .Ease(Motion.EaseIn)
                .Move(EmptyHintShift, 0, 0, Motion.Normal)
                .Opacity(EmptyHint, 1, Motion.Normal)
                .Play();

            EmptyHintPlayCount++;
        }), DispatcherPriority.Render);
    }

    // ---- 主体落定 ----

    /// <summary>
    /// 内容落定：从下方 6px 滑入 + 淡入。作用对象是承载内容的 Grid，导航栏与粒度切换按钮保持不动。
    /// 只在内容被换掉（翻页、换粒度）时走这里，首屏那一批交给明细卡片逐个入场。
    /// </summary>
    private void Settle()
    {
        if (!_firstLoadHandled) return;
        if (_settling) return;
        _settling = true;

        // 每次新建变换对象：复用会让上一次动画的收尾值成为下一次的起点，连续翻页时位移越积越小
        var transform = new TranslateTransform(0, SettleRise);
        SettleTarget.RenderTransform = transform;
        SettleTarget.Opacity = 0;

        LA.Builder().Ease(Motion.EaseIn)
            .Move(transform, 0, 0, Motion.Normal)
            .Play();

        LA.Builder().Ease(Motion.EaseIn)
            .Opacity(SettleTarget, 1, Motion.Normal)
            .OnComplete(() => _settling = false)
            .Play();
    }

    // ---- 卡片悬浮 / 入场 ----

    /// <summary>年视图的月份卡片悬浮：轻微抬起</summary>
    private void MonthCard_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateScale(sender, HoverScale, Motion.Fast);
    }

    private void MonthCard_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateScale(sender, 1.0, Motion.Fast);
    }

    /// <summary>明细卡片的悬浮：与月卡片共用同一个缩放原语，幅度更小</summary>
    private void EntryCard_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateScale(sender, EntryHoverScale, Motion.Fast);
    }

    private void EntryCard_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateScale(sender, 1.0, Motion.Fast);
    }

    /// <summary>
    /// 悬浮动画的直接入口，供 tools/UiSmokeTest 验证。测试不合成 MouseEnter 路由事件：
    /// 合成事件能否命中取决于命中测试、元素可见性等条件，失败时分不清是动画坏了还是事件没送到。
    /// </summary>
    internal void HoverCardForTest(FrameworkElement card, bool entering)
    {
        if (entering) MonthCard_MouseEnter(card, null!);
        else MonthCard_MouseLeave(card, null!);
    }

    /// <summary>明细卡片的悬浮入口，与 <see cref="HoverCardForTest"/> 同理，供测试直接驱动</summary>
    internal void HoverEntryCardForTest(FrameworkElement card, bool entering)
    {
        if (entering) EntryCard_MouseEnter(card, null!);
        else EntryCard_MouseLeave(card, null!);
    }

    /// <summary>取卡片上的 ScaleTransform 并动画到目标值</summary>
    private static void AnimateScale(object sender, double target, double durationMs)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.RenderTransform is not ScaleTransform scale) return;

        LA.Builder().Ease(Motion.EaseIn)
            .Scale(scale, target, durationMs)
            .Play();
    }

    /// <summary>
    /// 月 / 年视图的卡片整批入场。在列表层面统一驱动：容器的 Loaded 逐个触发，各起一条动画会让整片出现时间差。
    /// </summary>
    private void AnimateCardsIn()
    {
        // 先强制完成容器生成，否则 ItemsPanel 可能还是空的，一条都取不到
        if (Vm?.IsYearMode == true)
        {
            YearCards.UpdateLayout();
            AnimatePanel(FindVisualChild<WrapPanel>(YearCards) ?? (Panel?)FindVisualChild<UniformGrid>(YearCards));
        }
        else if (Vm?.IsMonthMode == true)
        {
            MonthGrid.UpdateLayout();
            AnimatePanel(FindVisualChild<UniformGrid>(MonthGrid));
        }
    }

    private static void AnimatePanel(Panel? panel)
    {
        if (panel is null) return;

        int index = 0;
        foreach (var child in panel.Children)
        {
            if (child is not FrameworkElement fe) continue;
            if (fe.RenderTransform is not ScaleTransform scale) continue;

            // 逐项递减峰值，整片看起来是一波而不是一次齐动
            double peak = 1.06 - index * 0.004;
            if (peak < 1.02) peak = 1.02;

            Motion.PlayElastic(scale, peak, Motion.Playful);
            index++;
        }
    }


    /// <summary>+ 号按下的回弹。面板的开合由 <see cref="BillEditorOverlay"/> 负责。</summary>
    private void AddNewBill_Click(object sender, RoutedEventArgs e)
        => Motion.PlayElastic(AddNewBillBtnScaleTransform, AddNewBillPeak);

    /// <summary>
    /// 粒度切换按钮按下的回弹。与其它按钮同一档反馈，让"点到了"这件事在所有控件上一致。
    /// </summary>
    private void ModeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.RenderTransform is not ScaleTransform scale) return;

        Motion.PlayElastic(scale, ModePressPeak);
    }

    /// <summary>在可视树中深度优先查找第一个指定类型的子元素</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;

            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
