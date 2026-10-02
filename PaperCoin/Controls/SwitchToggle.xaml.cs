using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LAE;

namespace PaperCoin.Controls;

/// <summary>
/// 带补间动画的开关。动画由代码后置直接驱动模板内的具名元素，不用模板 Trigger 里的 Storyboard
/// （后者的名称域解析时机不可控）。滑轨高亮色来自 DynamicResource，所以只对上层 Opacity 做动画：
/// 属性一旦跑过 ColorAnimation 就进入本地动画值优先级，主题资源无法再更新该属性。
/// </summary>
public partial class SwitchToggle : UserControl
{
    /// <summary>滑块可滑动的像素距离 = 轨宽 42 − 滑块 16 − 左右各 3 的留白</summary>
    private const double KnobTravel = 20;

    private const double SlideMs = 180;

    public SwitchToggle()
    {
        InitializeComponent();

        Loaded += (_, _) => ApplyState(animate: false);
        GotKeyboardFocus += (_, _) => FocusRing.Opacity = 1;
        LostKeyboardFocus += (_, _) => FocusRing.Opacity = 0;
    }

    // ---- 状态 ----

    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.Register(
            nameof(IsOn),
            typeof(bool),
            typeof(SwitchToggle),
            new PropertyMetadata(false, OnIsOnChanged));

    /// <summary>开关是否处于"开"</summary>
    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SwitchToggle toggle)
            toggle.ApplyState(animate: toggle.IsLoaded);
    }

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(
            nameof(Command),
            typeof(ICommand),
            typeof(SwitchToggle),
            new PropertyMetadata(null));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(
            nameof(CommandParameter),
            typeof(object),
            typeof(SwitchToggle),
            new PropertyMetadata(null));

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    // ---- 交互 ----

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Toggle();
        e.Handled = true;
    }

    private void Root_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) return;

        Toggle();
        e.Handled = true;
    }

    /// <summary>
    /// 触发切换。只发出命令，不自己改 IsOn：开关是受控的，状态属于 ViewModel，
    /// 控件先行翻转会在命令执行失败时留下错误的视觉状态。
    /// </summary>
    private void Toggle()
    {
        var command = Command;
        var parameter = CommandParameter;

        if (command?.CanExecute(parameter) == true)
            command.Execute(parameter);
    }

    // ---- 渲染 ----

    private void ApplyState(bool animate)
    {
        double targetX = IsOn ? KnobTravel : 0;
        double targetOpacity = IsOn ? 1 : 0;

        if (!animate)
        {
            // 初次进入不播动画，否则界面打开时所有开关都会滑动一次
            KnobShift.X = targetX;
            OnLayer.Opacity = targetOpacity;
            return;
        }

        // 滑块位移带轻微回弹
        LA.Builder().Ease(Easing.OutBack(1.6))
            .Move(KnobShift, targetX, 0, SlideMs)
            .Play();

        LA.Builder().Ease(Easing.OutCubic)
            .Opacity(OnLayer, targetOpacity, SlideMs)
            .Play();
    }
}
