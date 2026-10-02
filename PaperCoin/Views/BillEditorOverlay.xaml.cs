using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LAE;
using PaperCoin.ViewModels;

namespace PaperCoin.Views;

/// <summary>
/// 新增 / 编辑流水的浮层面板，DataContext 是 <see cref="BillEditorViewModel"/>。
/// 今日页与日历页共用本控件，两页的观感与快捷键因此保持一致。
/// </summary>
public partial class BillEditorOverlay : UserControl
{
    private const double PanelAnimationMs = 200;

    /// <summary>
    /// 面板收起时下移的距离。用固定像素而非 ActualHeight：打开动画发生在测量之前，
    /// 那时 ActualHeight 还是 0，算出来的位移也是 0，面板会原地淡出。
    /// </summary>
    private const double PanelHiddenOffset = 300;

    private BillEditorViewModel? _editor;
    private bool _playing;

    public BillEditorOverlay()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_editor is not null) _editor.PropertyChanged -= OnEditorPropertyChanged;

        _editor = e.NewValue as BillEditorViewModel;
        if (_editor is not null) _editor.PropertyChanged += OnEditorPropertyChanged;

        // 换了 ViewModel 就是换了一次数据来源，面板先收起，别把上一页的打开状态带过来
        if (_editor?.IsOpen != true) Collapse();
    }

    /// <summary>
    /// 解除订阅。ViewModel 由 DI 以 Transient 注册，而 MainWindow 长期缓存页面，
    /// 不退订会让旧 ViewModel 一直被本控件的事件委托引用住。
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_editor is not null) _editor.PropertyChanged -= OnEditorPropertyChanged;

        _editor = null;
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BillEditorViewModel.IsOpen)) return;

        if (_editor?.IsOpen == true)
            PlayOpen();
        else if (Visibility == Visibility.Visible)
            PlayClose();
    }

    private void PlayOpen()
    {
        if (_playing) return;
        _playing = true;

        Visibility = Visibility.Visible;
        EditorScrim.Opacity = 0;
        EditorTranslate.Y = 24;

        // Visibility 刚从 Collapsed 切过来的元素在本帧还没有可用的渲染变换，动画必须等布局跑完
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 收尾用 OutBack 而不是 OutCubic：面板略微冲过头再回位。起始位移只有 24px，超调不会溢出被裁。
            LA.Builder().Ease(Easing.OutBack(1.4))
                .Move(EditorTranslate, 0, 0, PanelAnimationMs)
                .Play();

            LA.Builder().Ease(Easing.OutCubic)
                .Opacity(EditorScrim, 0.35, PanelAnimationMs)
                .OnComplete(() =>
                {
                    _playing = false;

                    // 焦点直接给描述框：点 + 号的目的就是要打字。路由事件仍会从 TextBox
                    // 冒泡经过面板，Esc / Ctrl+Enter 照常生效。
                    DescriptionBox.Focus();
                    Keyboard.Focus(DescriptionBox);
                })
                .Play();
        }), DispatcherPriority.Render);
    }

    private void PlayClose()
    {
        if (_playing) return;
        _playing = true;

        // 收起是退场，不需要回弹
        LA.Builder().Ease(Easing.OutPow(2))
            .Move(EditorTranslate, 0, PanelHiddenOffset, PanelAnimationMs)
            .Play();

        LA.Builder().Ease(Easing.OutCubic)
            .Opacity(EditorScrim, 0, PanelAnimationMs)
            .OnComplete(Collapse)
            .Play();
    }

    private void Collapse()
    {
        Visibility = Visibility.Collapsed;
        _playing = false;
    }

    /// <summary>点遮罩即取消。只处理落在遮罩自身的点击，面板内部的点击由面板吞掉。</summary>
    private void EditorScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, EditorScrim))
            _editor?.CancelCommand.Execute(null);
    }

    /// <summary>Esc 取消，Ctrl+Enter 保存。挂在本面板上，按键不经过面板时不生效。</summary>
    private void EditorPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editor is null) return;

        if (e.Key == Key.Escape)
        {
            _editor.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (_editor.SaveCommand.CanExecute(null))
                _editor.SaveCommand.Execute(null);

            e.Handled = true;
        }
    }
}
