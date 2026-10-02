using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LAE;

namespace PaperCoin.Views;

/// <summary>
/// 列表卡片的入场动效：每张卡片上浮 + 淡入，按显示顺序错开。
/// 入口只有 <see cref="TryPlay"/> 一个，每次进入页面只播一轮，容器重建时不会重复入场。
/// 挂 LayoutUpdated 是因为容器在测量阶段生成，且起点要在卡片第一次被画出来之前写好；列表还空着时不摘钩。
/// 约定行元素的 RenderTransform 必须是一个 TranslateTransform，缺变换时整行跳过。
/// </summary>
internal sealed class CardListEntrance
{
    /// <summary>单张卡片的入场时长</summary>
    private const double DurationMs = 360;

    /// <summary>相邻两张的时间差</summary>
    private const double StaggerMs = 70;

    /// <summary>错开的档数上限</summary>
    private const int MaxStagger = 8;

    /// <summary>入场起点比静止位置低多少</summary>
    private const double Rise = 14;

    private readonly ItemsControl _list;

    private bool _played;
    private bool _hooked;

    public CardListEntrance(ItemsControl list) => _list = list;

    /// <summary>这一轮（一次进入页面）是否已经播过</summary>
    public bool HasPlayed => _played;

    /// <summary>累计播放张数，供 tools/UiSmokeTest 断言动效真的接上了</summary>
    public int PlayCount { get; private set; }

    /// <summary>
    /// 复位，让下一次到货重新入场。页面实例会被 MainWindow 缓存，不在换页时复位，入场就只在本次进程第一次进入时看得到。
    /// </summary>
    public void Reset()
    {
        Unhook();
        _played = false;
    }

    /// <summary>数据可能到货时调用；容器还没生成时会一直挂着等下一次布局。</summary>
    public void TryPlay()
    {
        if (_played || _hooked) return;

        _hooked = true;
        _list.LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_played)
        {
            Unhook();
            return;
        }

        // 还没到货：保持挂载，等下一次布局
        if (_list.Items.Count == 0) return;

        var rows = Rows();

        // 有数据却没容器：这次布局没轮到这里，同样保持挂载
        if (rows.Count == 0) return;

        Unhook();
        _played = true;

        for (int i = 0; i < rows.Count; i++)
        {
            PlayRow(rows[i], i);
        }
    }

    private void PlayRow(FrameworkElement row, int index)
    {
        // 缺变换就整行跳过
        if (row.RenderTransform is not TranslateTransform shift) return;

        double delay = Math.Min(index, MaxStagger) * StaggerMs;

        // 起点先写死：等动画真正开始（有延迟的那几张要等几十到几百毫秒）才写，中间几帧会以终值闪一下再跳回起点
        row.Opacity = 0;
        shift.Y = Rise;

        // 两个动作各自带一次同样的延迟：Delay 只作用于下一个动作，只写一次的话后加入的那个会立刻开始，错开失效
        LA.Builder().Ease(Easing.OutCubic)
            .Delay(delay).Move(shift, 0, 0, DurationMs)
            .Delay(delay).Opacity(row, 1, DurationMs)
            .Play();

        PlayCount++;
    }

    private void Unhook()
    {
        if (!_hooked) return;

        _list.LayoutUpdated -= OnLayoutUpdated;
        _hooked = false;
    }

    /// <summary>按显示顺序取每个条目的行元素（数据模板的根）</summary>
    private List<FrameworkElement> Rows()
    {
        var rows = new List<FrameworkElement>();
        Collect(_list);
        return rows;

        void Collect(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);

                // 按生成器认条目容器：模板里的按钮也有 ContentPresenter，但不在生成器的映射里
                if (child is ContentPresenter presenter
                    && _list.ItemContainerGenerator.IndexFromContainer(presenter) >= 0)
                {
                    if (VisualTreeHelper.GetChildrenCount(presenter) > 0
                        && VisualTreeHelper.GetChild(presenter, 0) is FrameworkElement row)
                        rows.Add(row);

                    continue;
                }

                Collect(child);
            }
        }
    }
}
