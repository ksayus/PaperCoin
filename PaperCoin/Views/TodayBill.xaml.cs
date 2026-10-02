using System.Collections.Specialized;
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
/// TodayBill 的视图逻辑。动画目标是可视树对象，所以约定 ViewModel 只翻转 IsEditorOpen 这类布尔状态，由本类按状态播放。
/// </summary>
public partial class TodayBill : UserControl
{
    // ---- 编辑面板 ----
    private const double PanelAnimationMs = 200;

    /// <summary>面板收起时下移的距离。用固定像素：打开动画发生在测量之前，那时 ActualHeight 还是 0。</summary>
    private const double PanelHiddenOffset = 300;

    // ---- 流水卡片 ----
    private const double RowPressMs = 80;
    private const double RowPressScale = 0.97;
    private const double RowHoverScale = 1.02;

    // ---- 删除回流 ----
    private const double ReflowMs = 180;

    // ---- 空状态提示 ----
    private const double EmptyHintMs = 280;

    /// <summary>空状态提示的起始上浮距离，与列表入场取同一节奏。</summary>
    private const double EmptyHintRise = 6;

    /// <summary>
    /// 空状态提示过渡的注册名。LAE 的分组表全进程共享，加页面前缀避免两个页面互相停掉对方的过渡。
    /// </summary>
    internal const string EmptyHintAnimationName = "TodayBillEmptyHint";

    /// <summary>空状态提示是否已经播过，避免同一轮刷新里重复起动画</summary>
    private bool _emptyHintShown;

    /// <summary>
    /// 空状态提示入场的实际播放次数，供 tools/UiSmokeTest 断言动效真的接上了。
    /// 计数与时钟无关：LAE 按真实时间推进，测试里那一轮 IO 若跨过 280ms，动画会在一帧内跑完。
    /// </summary>
    internal int EmptyHintPlayCount { get; private set; }

    /// <summary>
    /// 悬浮缩放的峰值，供 tools/UiSmokeTest 验证放大后不会越出滚动视口。
    /// TodayBill.xaml 给列表留的横向余量必须 ≥ 这个值，否则 ScrollViewer 会裁掉放大后的卡片边缘。
    /// </summary>
    internal const double MaxHoverScale = RowHoverScale;

    private bool _playingEditorPanel;
    private bool _rowPressed;

    /// <summary>卡片入场（上浮 + 淡入，按顺序错开），实现见 <see cref="CardListEntrance"/></summary>
    private readonly CardListEntrance _cardEntrance;

    /// <summary>删除前各条目容器的位置，用来算回流位移</summary>
    private readonly List<(ContentPresenter Container, double Y)> _slotsBeforeRemoval = new();

    private bool _reflowPending;

    /// <summary>卡片入场的实际播放张数，供 tools/UiSmokeTest 断言动效真的接上了</summary>
    internal int CardsEntrancePlayCount => _cardEntrance.PlayCount;

    /// <summary>删除回流的实际播放次数，同上</summary>
    internal int CardReflowPlayCount { get; private set; }

    private TodayBillViewModel? Vm => DataContext as TodayBillViewModel;

    public TodayBill()
    {
        InitializeComponent();

        _cardEntrance = new CardListEntrance(EntryList);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    // ---- 生命周期 ----

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 推迟到 Render 优先级：此时布局尚未完成，缩放会以错误的 RenderTransformOrigin 起算。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PlayQElastic(AddNewBillBtnScaleTransform, 1.18, 420);
        }), DispatcherPriority.Render);

        _cardEntrance.TryPlay();

        // 首屏为空（全新安装）时提示也要有一次入场
        RequestEmptyHint();

        if (Vm?.IsEditorOpen == true)
        {
            EditorLayer.Visibility = Visibility.Visible;
            EditorTranslate.Y = 0;
            EditorScrim.Opacity = 0.35;
        }
    }

    /// <summary>
    /// 解除对 ViewModel 的事件订阅。ViewModel 以 Transient 注册而页面被 MainWindow 长期缓存，不退订会一直引用住旧实例。
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Vm is not null)
        {
            Vm.PropertyChanged -= OnViewModelPropertyChanged;
            Vm.Entries.CollectionChanged -= OnEntriesChanged;
        }

        // 换页时摘掉钩子并复位入场标志，切回来才能重播
        _cardEntrance.Reset();

        if (_reflowPending)
        {
            LayoutUpdated -= OnReflowLayoutUpdated;
            _reflowPending = false;
            _slotsBeforeRemoval.Clear();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TodayBillViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            oldVm.Entries.CollectionChanged -= OnEntriesChanged;
        }

        if (e.NewValue is TodayBillViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            newVm.Entries.CollectionChanged += OnEntriesChanged;
        }

        // 新数据源的容器要等下一次布局才生成，这里先挂上钩子，好在卡片第一次被画出来之前写好起点
        _cardEntrance.Reset();
        _cardEntrance.TryPlay();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 列表空/非空切换：空了播空提示入场，有数据了播卡片入场
        if (e.PropertyName == nameof(TodayBillViewModel.IsEmpty))
        {
            if (Vm?.IsEmpty == true)
            {
                RequestEmptyHint();
            }
            else
            {
                // 复位，下次真的空了还能再播
                _emptyHintShown = false;
                _cardEntrance.TryPlay();
            }

            return;
        }

        if (e.PropertyName != nameof(TodayBillViewModel.IsEditorOpen)) return;

        if (Vm?.IsEditorOpen == true)
            PlayEditorOpen();
        else if (EditorLayer.Visibility == Visibility.Visible)
            PlayEditorClose();
    }

    // ---- 删除回流 ----

    /// <summary>
    /// 删除条目后让下方卡片滑到新位置（FLIP）。集合变更时先记下删除前的位置，
    /// 等布局跑完再用位移把卡片摆回旧位置并动画归零。
    /// 挂在 LayoutUpdated 上是因为它在新布局之后、本帧渲染之前触发，摆回旧位置这一步不会被看到。
    /// </summary>
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 只有删除会让后面的卡片往上挪；新增的卡片有自己的入场，下移的卡片不需要追上
        if (e.Action != NotifyCollectionChangedAction.Remove) return;
        if (_reflowPending) return;

        _slotsBeforeRemoval.Clear();

        // 位置从容器读：行元素自己挂着位移/缩放变换，读出来会带上动画中间态
        foreach (var container in ItemContainers())
        {
            _slotsBeforeRemoval.Add((container, YInList(container)));
        }

        if (_slotsBeforeRemoval.Count == 0) return;

        _reflowPending = true;
        LayoutUpdated += OnReflowLayoutUpdated;
    }

    private void OnReflowLayoutUpdated(object? sender, EventArgs e)
    {
        LayoutUpdated -= OnReflowLayoutUpdated;
        _reflowPending = false;

        foreach (var (container, yBefore) in _slotsBeforeRemoval)
        {
            // 容器可能已随它那个条目一起被摘掉
            if (!container.IsDescendantOf(EntryList)) continue;

            double delta = yBefore - YInList(container);

            // 只有真的往上挪的才滑
            if (delta < 0.5) continue;

            var row = RowOf(container);
            if (row is null) continue;

            // 先摆回旧位置（本帧渲染之前就位，看不到跳变），再滑到 0
            SlotShiftOf(row).Y = delta;

            LA.Builder().Ease(Easing.OutCubic)
                .Move(SlotShiftOf(row), 0, 0, ReflowMs)
                .Play();

            CardReflowPlayCount++;
        }

        _slotsBeforeRemoval.Clear();
    }

    // ---- 条目容器定位 ----

    /// <summary>
    /// 按显示顺序取条目容器。不走 ItemContainerGenerator：集合变更处理器的先后顺序不确定，
    /// 排在 ItemsControl 前面时生成器里可能还挂着已被删掉的条目。
    /// </summary>
    private List<ContentPresenter> ItemContainers()
    {
        var result = new List<ContentPresenter>();
        Collect(EntryList);
        return result;

        void Collect(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);

                // 按内容类型认容器：删除按钮的模板里也有 ContentPresenter
                if (child is ContentPresenter presenter
                    && presenter.Content is PaperCoin.Core.Models.BillEntry)
                    result.Add(presenter);
                else
                    Collect(child);
            }
        }
    }

    private static FrameworkElement? RowOf(DependencyObject container)
    {
        int count = VisualTreeHelper.GetChildrenCount(container);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(container, i) is FrameworkElement element)
                return element;
        }
        return null;
    }

    private double YInList(Visual element)
        => element.TransformToAncestor(EntryList).Transform(new Point(0, 0)).Y;

    /// <summary>行上负责上下位移的变换，位置约定见 TodayBill.xaml 的行格子注释。</summary>
    private static TranslateTransform SlotShiftOf(FrameworkElement row)
        => row.RenderTransform as TranslateTransform
           ?? throw new InvalidOperationException("行格子缺少 TranslateTransform");

    // ---- 空状态提示动效 ----

    /// <summary>
    /// 请求播放空状态提示的入场。推迟到 Render 优先级再决定：绑定驱动的 Visibility 可能还没落地，
    /// 而且列表刷新会短暂经过空状态，等布局跑完再判断，真的还是空才播。
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

            // 起点必须先写死，否则会从上一轮动画的收尾值起算（那时 Opacity 已是 1，淡入成为空转）
            EmptyHintShift.Y = EmptyHintRise;
            EmptyHint.Opacity = 0;

            // 位移与淡入放进同一个动画组：同名的组会被后者替换，拆成两次 Play 会互相停掉
            LA.Builder(EmptyHintAnimationName)
                .Ease(Easing.OutCubic)
                .Move(EmptyHintShift, 0, 0, EmptyHintMs)
                .Opacity(EmptyHint, 1, EmptyHintMs)
                .Play();

            EmptyHintPlayCount++;
        }), DispatcherPriority.Render);
    }

    // ---- 编辑面板动效 ----

    private void PlayEditorOpen()
    {
        if (_playingEditorPanel) return;
        _playingEditorPanel = true;

        PlayQElastic(AddNewBillBtnScaleTransform, 1.18, 420);

        EditorLayer.Visibility = Visibility.Visible;
        EditorScrim.Opacity = 0;
        EditorTranslate.Y = 24;

        // Visibility 刚从 Collapsed 切过来的元素本帧还没有可用的渲染变换，动画要等布局跑完
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 收尾用 OutBack 制造轻微回弹；起始位移仅 24px，超调量不会溢出被裁切
            LA.Builder().Ease(Easing.OutBack(1.4))
                .Move(EditorTranslate, 0, 0, PanelAnimationMs)
                .Play();

            LA.Builder().Ease(Easing.OutCubic)
                .Opacity(EditorScrim, 0.35, PanelAnimationMs)
                .OnComplete(() =>
                {
                    _playingEditorPanel = false;

                    // 焦点直接给描述框：点 + 号就是要打字，不必让用户再点一次输入框
                    DescriptionBox.Focus();
                    Keyboard.Focus(DescriptionBox);
                })
                .Play();
        }), DispatcherPriority.Render);
    }

    private void PlayEditorClose()
    {
        if (_playingEditorPanel) return;
        _playingEditorPanel = true;

        // 关闭不做回弹
        LA.Builder().Ease(Easing.OutPow(2))
            .Move(EditorTranslate, 0, PanelHiddenOffset, PanelAnimationMs)
            .Play();

        LA.Builder().Ease(Easing.OutCubic)
            .Opacity(EditorScrim, 0, PanelAnimationMs)
            .OnComplete(() =>
            {
                // 动画结束后必须真正收起可视树，否则这块全屏遮罩会继续拦截鼠标，列表点不动
                EditorLayer.Visibility = Visibility.Collapsed;
                _playingEditorPanel = false;
            })
            .Play();
    }

    // ---- 流水卡片动效 ----

    /// <summary>指针进入：轻微抬起</summary>
    private void RowCard_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.RenderTransform is not TransformGroup group) return;
        if (group.Children[0] is not ScaleTransform scale) return;

        LA.Builder().Ease(Easing.OutCubic)
            .Scale(scale, RowHoverScale, 100)
            .Play();
    }

    private void RowCard_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.RenderTransform is not TransformGroup group) return;
        if (group.Children[0] is not ScaleTransform scale) return;
        if (group.Children[1] is not TranslateTransform offset) return;

        LA.Builder().Ease(Easing.OutCubic)
            .Scale(scale, 1.0, 100)
            .Move(offset, 0, 0, 100)
            .Play();
    }

    /// <summary>按下：立刻压缩</summary>
    private void RowCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (sender is not FrameworkElement fe || fe.RenderTransform is not TransformGroup group) return;
        if (group.Children[0] is not ScaleTransform scale) return;
        if (group.Children[1] is not TranslateTransform offset) return;

        _rowPressed = true;

        LA.Builder().Ease(Easing.InOutCubic)
            .Scale(scale, RowPressScale, RowPressMs)
            .Move(offset, 0, 1, RowPressMs)
            .Play();
    }

    private void RowCard_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_rowPressed) return;
        _rowPressed = false;

        if (sender is not FrameworkElement fe || fe.RenderTransform is not TransformGroup group) return;
        if (group.Children[0] is not ScaleTransform scale) return;
        if (group.Children[1] is not TranslateTransform offset) return;

        // 落点不在本卡片上（按下后拖了出去）就只还原，不触发编辑
        if (!fe.IsMouseOver)
        {
            LA.Builder().Ease(Easing.OutCubic)
                .Scale(scale, 1.0, 100)
                .Move(offset, 0, 0, 100)
                .Play();
            return;
        }

        // 抬起时回弹：先冲到 1.04 再落回 1.0
        PlayQElastic(scale, 1.04, 300);

        LA.Builder().Ease(Easing.InOutCubic)
            .Move(offset, 0, 0, 120)
            .Play();

        // 整个卡片都是编辑热区；删除按钮会在自己的 MouseLeftButtonUp 里把事件标记为已处理，不会走到这里
        if (Vm?.EditCommand.CanExecute(fe.DataContext) == true)
            Vm.EditCommand.Execute(fe.DataContext);
    }

    /// <summary>判断事件源是否落在某个 Button 内部，用于让删除按钮的点击不触发整行的按下动效。</summary>
    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    // ---- 交互 ----

    /// <summary>点遮罩即取消；只处理落在遮罩自身的点击，面板内部的点击由面板吞掉。</summary>
    private void EditorScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, EditorScrim))
            Vm?.CancelCommand.Execute(null);
    }

    /// <summary>
    /// 面板内快捷键：Esc 取消，Ctrl+Enter 保存。挂在 EditorPanel 而非 UserControl 上，只在按键经过面板时生效。
    /// </summary>
    private void EditorPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is null) return;

        if (e.Key == Key.Escape)
        {
            Vm.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (Vm.SaveCommand.CanExecute(null))
                Vm.SaveCommand.Execute(null);

            e.Handled = true;
        }
    }

    // ---- 动效基元 ----

    /// <summary>
    /// 播一次放大到 <paramref name="peak"/> 再回落到 1.0 的回弹动效。用两段串行动画而不是单段 OutBack：
    /// OutBack 的回弹终点会停在与超调绑定的目标值上，拆成去程与回程两段才能精确落回静止值。
    /// </summary>
    private static void PlayQElastic(ScaleTransform scale, double peak, double durationMs)
    {
        LA.Builder()
            .Ease(Easing.InOutCubic)
            .Scale(scale, peak, durationMs * 0.35)
            .Then()
            .Ease(Easing.OutBack(2.0))
            .Scale(scale, 1.0, durationMs * 0.65)
            .Play();
    }
}
