using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using PaperCoin.Controls;
using PaperCoin.Service;
using PaperCoin.Services;
using PaperCoin.ViewModels;
using PaperCoin.Views;

namespace PaperCoin.UiSmokeTest;

/// <summary>
/// 手工驱动的 UI 冒烟测试。
/// 直接 new 出 View、量它、驱动它，用来暴露缺少的 StaticResource、失效的
/// RelativeSource、模板名字域解析失败、布局塌陷、绑定路径写错等问题。这些
/// 都不抛异常，只表现为界面空白或错位。
/// 用法: dotnet run（在 tools/UiSmokeTest 目录下）
/// </summary>
internal static class Program
{
    private const double WindowWidth = 420;
    private const double WindowHeight = 540;

    /// <summary>主内容区宽度 = 窗口 − 侧边栏 50</summary>
    private const double ContentWidth = WindowWidth - 50;

    [STAThread]
    private static int Main()
    {
        var app = new Application
        {
            // 默认 ShutdownMode 会在最后一个窗口关闭时退出进程。侧边栏测试要
            // Show/Close 一个真实 MainWindow，一关就会把测试宿主带停。
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        BootstrapResources(app);
        BootstrapServices();

        // 保证每轮从同一状态开始。
        ResetBillsData();

        int failures = 0;

        // 在 Dispatcher 上执行，await 续体的同步上下文才与真实应用一致。
        Application.Current.Dispatcher.Invoke(() =>
        {
            failures += Check("1. 主题字典 key 对齐与完整性", TestResourceKeys);
            failures += Check("2. 图标码位全部存在于字体", () => TestIcons(app));
            failures += Check("3. 侧边栏指示器随窗口尺寸重算", TestSidebarIndicator);
            failures += Check("4. TodayBill 布局体检", TestTodayBillLayout);
            failures += Check("5. TodayBill 保存链路（无 Reset、跨日期）", TestTodayBillSave);
            failures += Check("6. CalenderBill 布局 + 今天圆角 + 卡片悬浮", TestCalendar);
            failures += Check("7. Settings 布局 + 开关动画 + 下拉框交互", TestSettings);
            failures += Check("8. 主题三模式与解析", TestThemeModes);
            failures += Check("9. 纵向滚动条装饰样式", TestScrollBarDecoration);
            failures += Check("10. 空状态提示入场动效", TestEmptyHintAnimation);
            failures += Check("11. 连续保存串行化", TestSerializedSaves);
            failures += Check("12. 卡片入场与删除回流", TestCardMotion);
            failures += Check("13. 日历明细卡片入场", TestCalendarEntrance);
            failures += Check("14. 日期选择器跟着主题走", TestDatePickerTheme);
        });

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "== 全部通过 ==" : $"== {failures} 项失败 ==");
        return failures;
    }

    // ---- 1. 资源 ----

    private static void TestResourceKeys()
    {
        var dark = LoadDict("/Themes/Dark.xaml");
        var bright = LoadDict("/Themes/Bright.xaml");

        var dk = dark.Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();
        var bk = bright.Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();

        Report($"Dark keys = {dk.Count}, Bright keys = {bk.Count}");

        var onlyDark = dk.Except(bk).ToList();
        var onlyBright = bk.Except(dk).ToList();
        if (onlyDark.Count > 0) throw new Exception("仅 Dark 定义: " + string.Join(", ", onlyDark));
        if (onlyBright.Count > 0) throw new Exception("仅 Bright 定义: " + string.Join(", ", onlyBright));

        foreach (var key in new[]
                 {
                     "ColorCardBackground", "ColorCardBorder", "ColorCardActionBackground",
                     "ColorTextTitle", "ColorTextSecondary", "ColorPositive", "ColorNegative",
                     "ColorShadow", "ColorMainContentArea", "ColorAddNewBillBtn",
                     "ColorSideBarBtnActive", "ColorSideBarBtnOver", "ColorTitleTop",
                     "ColorSideBar", "ColorWindowShadow"
                 })
        {
            if (!dk.Contains(key)) throw new Exception($"Dark 缺少 key: {key}");
        }
    }

    // ---- 2. 图标 ----

    /// <summary>
    /// 直接解析 TTF 的 cmap 表，不经过 WPF 字体加载。
    /// <c>Typeface.TryGetGlyphTypeface</c> 在本测试环境会被字体解析策略挡掉。
    /// </summary>
    private static void TestIcons(Application app)
    {
        var icons = LoadDict("/Icon/AppIcons.xaml");

        var ttfPath = Path.Combine(RepoRoot()!, "PaperCoin", "Icon", "Iconfont.ttf");
        if (!File.Exists(ttfPath)) throw new Exception($"找不到字体: {ttfPath}");

        var codepoints = ReadCmap(File.ReadAllBytes(ttfPath));
        Report($"字体 cmap 映射码位 {codepoints.Count} 个, AppIcons 引用 {icons.Count} 个");

        // 表里有两类值：字体码位与矢量几何。必须分开统计，对几何调
        // char.ConvertToUtf32 会抛 InvalidCastException。
        var entries = icons.Keys.Cast<object>()
            .Select(k => (Key: k.ToString()!, Value: icons[k]))
            .ToList();

        var fontIcons = entries.Where(e => e.Value is string).ToList();
        var vectorIcons = entries.Where(e => e.Value is Geometry).ToList();
        var unknown = entries.Where(e => e.Value is not string and not Geometry).ToList();

        Report($"字体码位 {fontIcons.Count} 项, 矢量几何 {vectorIcons.Count} 项" +
               $"（{string.Join(", ", vectorIcons.Select(v => v.Key))}）");

        if (unknown.Count > 0)
            throw new Exception("图标表里有无法识别的条目类型: " +
                                string.Join(", ", unknown.Select(u => $"{u.Key}={u.Value?.GetType().Name}")));

        int bad = 0;
        foreach (var (key, value) in fontIcons.OrderBy(e => e.Key))
        {
            var cp = char.ConvertToUtf32((string)value, 0);
            if (!codepoints.Contains(cp))
            {
                Report($"{key,-16} U+{cp:X4}  缺失 <-- 会渲染成空白");
                bad++;
            }
        }

        if (bad > 0) throw new Exception($"{bad} 个图标码位不在字体里");

        foreach (var (key, value) in vectorIcons)
        {
            if (value is not Geometry geometry || geometry.IsEmpty())
                throw new Exception($"矢量图标 {key} 是空几何, 画不出任何东西");

            Report($"{key,-16} 矢量几何 bounds={geometry.Bounds}");
        }
    }

    /// <summary>解析 TTF cmap，取覆盖最全的一个子表</summary>
    private static HashSet<int> ReadCmap(byte[] data)
    {
        int numTables = ReadU16(data, 4);
        var tables = new Dictionary<string, int>();
        for (int i = 0; i < numTables; i++)
        {
            int off = 12 + i * 16;
            var tag = System.Text.Encoding.ASCII.GetString(data, off, 4);
            tables[tag] = ReadU32(data, off + 8);
        }

        int cmapOff = tables["cmap"];
        int n = ReadU16(data, cmapOff + 2);

        var best = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            int rec = cmapOff + 4 + i * 8;
            int subOff = cmapOff + ReadU32(data, rec + 4);
            int fmt = ReadU16(data, subOff);

            var set = fmt switch
            {
                4 => ParseFormat4(data, subOff),
                12 => ParseFormat12(data, subOff),
                _ => []
            };

            if (set.Count > best.Count) best = set;
        }

        return best;
    }

    private static HashSet<int> ParseFormat4(byte[] d, int b)
    {
        int segX2 = ReadU16(d, b + 6);
        int seg = segX2 / 2;
        int endPos = b + 14;
        int startPos = b + 16 + segX2;
        int deltaPos = b + 16 + 2 * segX2;
        int rangePos = b + 16 + 3 * segX2;

        var result = new HashSet<int>();
        for (int i = 0; i < seg; i++)
        {
            int end = ReadU16(d, endPos + i * 2);
            int start = ReadU16(d, startPos + i * 2);
            int delta = (short)ReadU16(d, deltaPos + i * 2);
            int rangeOff = ReadU16(d, rangePos + i * 2);

            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int g;
                if (rangeOff == 0)
                {
                    g = (c + delta) & 0xFFFF;
                }
                else
                {
                    int gi = rangePos + i * 2 + rangeOff + (c - start) * 2;
                    if (gi + 2 > d.Length) continue;
                    g = ReadU16(d, gi);
                    if (g != 0) g = (g + delta) & 0xFFFF;
                }

                if (g != 0) result.Add(c);
            }
        }
        return result;
    }

    private static HashSet<int> ParseFormat12(byte[] d, int b)
    {
        int groups = ReadU32(d, b + 12);
        var result = new HashSet<int>();
        for (int i = 0; i < groups; i++)
        {
            int g = b + 16 + i * 12;
            int start = ReadU32(d, g);
            int end = ReadU32(d, g + 4);
            for (int c = start; c <= end; c++) result.Add(c);
        }
        return result;
    }

    private static int ReadU16(byte[] d, int o) => (d[o] << 8) | d[o + 1];

    private static int ReadU32(byte[] d, int o)
        => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

    // ---- 3. 侧边栏指示器 ----

    private static void TestSidebarIndicator()
    {
        var window = new MainWindow
        {
            Width = WindowWidth,
            Height = WindowHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,   // 挪到屏幕外，仍参与真实布局
            Top = -10000
        };

        window.Show();
        Pump();

        // 本环境 LAE 逐帧插值不会推进，等首屏入场只会测出环境限制。改用尺寸
        // 往返触发布局驱动的 RepositionIndicator(animate:false) 把指示器落定。
        SettleIndicator(window);

        double navBefore = window.NavPanelHeight;
        double y1 = window.IndicatorY;
        Report($"落定后  NavPanel.H={navBefore:0.#}  指示器Y={y1:0.#}");

        if (Math.Abs(y1) < 0.5)
            throw new Exception("首屏指示器没有对准第一个按钮");

        window.Height = WindowHeight + 240;
        Pump();

        double y2 = window.IndicatorY;
        Report($"放大后 NavPanel.H={window.NavPanelHeight:0.#}  指示器Y={y2:0.#}");

        if (Math.Abs(window.NavPanelHeight - navBefore) < 1)
            throw new Exception("窗口变高后侧边栏高度没变, 测试前提不成立");

        if (Math.Abs(y2 - y1) < 1)
            throw new Exception("窗口变高后指示器没有跟随重算");

        window.Height = WindowHeight;
        Pump();
        double y3 = window.IndicatorY;
        Report($"还原后 指示器Y={y3:0.#}");

        if (Math.Abs(y3 - y1) > 1.5)
            throw new Exception($"还原尺寸后指示器没回到原位: {y3:0.#} vs {y1:0.#}");

        // 走真实点击路径（GoTo → MoveIndicatorTo(animate: true)）。换页会触发
        // 整棵可视树的布局，若布局复算把 transform.Y 直接写成终点，过渡会被
        // 就地掐掉，表现为瞬移。
        double beforeNav = window.IndicatorY;

        window.NavButtonForTest("CalenderBill").RaiseEvent(new RoutedEventArgs(
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump();

        double afterNav = window.IndicatorY;
        bool running = LAE.LAEngine.IsRunning(MainWindow.IndicatorAnimationName);
        Report($"点击日历后 指示器Y={afterNav:0.#}（点击前 {beforeNav:0.#}）过渡在跑={running}");

        if (!running)
            throw new Exception("切换导航没有向 LAE 注册指示器过渡（动效没接上）");

        // 落定后的真实终点
        SettleIndicator(window);
        double settled = window.IndicatorY;

        double travel = Math.Abs(settled - beforeNav);
        Report($"日历项落定位置 Y={settled:0.#}（位移 {travel:0.#}px）");

        if (travel < 20)
            throw new Exception($"切换导航前后目标几乎没变（{travel:0.#}px）, 测试前提不成立");

        if (Math.Abs(afterNav - beforeNav) >= travel * 0.5)
            throw new Exception(
                "指示器在切换后直接瞬移到了终点, 过渡动画被打断: " +
                $"点击后 {afterNav:0.#}, 起点 {beforeNav:0.#}, 终点 {settled:0.#}");

        window.Close();
        Pump();
    }

    /// <summary>
    /// 让指示器落定：用窗口高度往返触发布局复算，不等动画
    /// （本进程没有渲染循环，LAE 时钟不推进）。
    /// </summary>
    private static void SettleIndicator(MainWindow window)
    {
        window.Height = WindowHeight + 60;
        Pump();
        window.Height = WindowHeight;
        Pump();
    }

    // ---- 4. TodayBill 布局 ----

    private static void TestTodayBillLayout()
    {
        var vm = new TodayBillViewModel(NewRepository());
        var view = new TodayBill { DataContext = vm };
        Pump();
        Layout(view);

        AssertNoCollapse(view, "TodayBill(空)");

        var addBtn = FindField<Button>(view, "AddNewBill")
            ?? throw new Exception("找不到 + 号按钮");

        var addBounds = BoundsIn(view, addBtn);
        Report($"+ 号 = {addBounds.X:0.#},{addBounds.Y:0.#} {addBounds.Width:0.#}x{addBounds.Height:0.#}");
        AssertInside(view, addBounds, "+ 号按钮");

        for (int i = 0; i < 3; i++)
        {
            vm.AddCommand.Execute(null);
            vm.DraftDescription = $"测试条目 {i}";
            vm.DraftAmountText = $"-{i + 1}.5";
            vm.SaveCommand.Execute(null);
        }

        WaitUntil(() => vm.Entries.Count >= 3);
        Layout(view);

        Report($"条目数 = {vm.Entries.Count}");
        if (vm.Entries.Count != 3) throw new Exception($"应有 3 条, 实际 {vm.Entries.Count}");

        AssertNoCollapse(view, "TodayBill(3 条)");

        // 一个条目对应多层 Border（阴影层、删除按钮的模板边框），这里整批体检。
        var entryBorders = FindAllCore<Border>(view)
            .Where(b => b.DataContext is PaperCoin.Core.Models.BillEntry)
            .ToList();

        foreach (var card in entryBorders)
        {
            if (card.ActualHeight <= 1)
                throw new Exception($"卡片高度塌陷: {card.ActualHeight:0.#}");
        }

        // 真正的卡片面：带悬浮缩放变换的那一层
        var cards = entryBorders.Where(b => HoverScaleOf(b) is not null).ToList();
        Report($"卡片数 = {cards.Count}（绑定到流水的 Border 共 {entryBorders.Count} 层）");

        if (cards.Count != 3) throw new Exception($"应有 3 张卡片, 实际 {cards.Count}");

        // 删除按钮与金额不能压在一起。
        foreach (var card in cards)
        {
            AssertDeleteButtonClearOfAmount(card);
        }

        // 删除图标必须是矢量垃圾桶：这套 iconfont 里的 U+ED1B 是暂停图标。
        var expectedDeleteIcon = Application.Current!.FindResource("IconDeleteGeometry") as Geometry
            ?? throw new Exception("资源里没有 IconDeleteGeometry");

        foreach (var card in cards)
        {
            var paths = FindAllCore<System.Windows.Shapes.Path>(card).ToList();
            if (paths.Count == 0)
                throw new Exception("卡片上没有矢量图标（删除图标又退回字体码位了？）");

            if (!paths.Any(p => ReferenceEquals(p.Data, expectedDeleteIcon)))
                throw new Exception("卡片的矢量图标不是 IconDeleteGeometry");
        }

        // ScrollViewer 会把内容裁到视口，卡片放大到越界时两边会被切掉。
        AssertHoverFitsInViewport(view, cards, TodayBill.MaxHoverScale, "TodayBill");

        vm.AddCommand.Execute(null);
        Pump();
        Layout(view);

        var panel = FindField<Grid>(view, "EditorPanel")
            ?? throw new Exception("找不到编辑面板");

        if (panel.Visibility != Visibility.Visible) throw new Exception("编辑面板没有显示");

        var panelBounds = BoundsIn(view, panel);
        Report($"编辑面板 = {panelBounds.X:0.#},{panelBounds.Y:0.#} {panelBounds.Width:0.#}x{panelBounds.Height:0.#}");
        AssertInside(view, panelBounds, "编辑面板");

        // 遮罩必须铺满整个内容区，内缩的话四周会露出一圈没压暗的边。
        var scrim = FindField<Border>(view, "EditorScrim")
            ?? throw new Exception("找不到遮罩层 EditorScrim");

        var scrimBounds = BoundsIn(view, scrim);
        var viewBounds = BoundsIn(view, view);
        Report($"遮罩 = {scrimBounds.X:0.#},{scrimBounds.Y:0.#} " +
               $"{scrimBounds.Width:0.#}x{scrimBounds.Height:0.#}  " +
               $"内容区 = {viewBounds.Width:0.#}x{viewBounds.Height:0.#}");

        if (scrimBounds.X > viewBounds.X + 1
            || scrimBounds.Y > viewBounds.Y + 1
            || scrimBounds.Right < viewBounds.Right - 1
            || scrimBounds.Bottom < viewBounds.Bottom - 1)
            throw new Exception(
                $"遮罩没有铺满内容区, 四周会露出一圈未压暗的边: " +
                $"遮罩 左{scrimBounds.Left:0.#} 上{scrimBounds.Top:0.#} " +
                $"右{scrimBounds.Right:0.#} 下{scrimBounds.Bottom:0.#} / " +
                $"内容区 左{viewBounds.Left:0.#} 上{viewBounds.Top:0.#} " +
                $"右{viewBounds.Right:0.#} 下{viewBounds.Bottom:0.#}");

        foreach (var name in new[] { "DescriptionBox" })
        {
            var box = FindField<TextBox>(view, name)!;
            if (box.ActualWidth < 10 || box.ActualHeight < 10)
                throw new Exception($"{name} 尺寸异常: {box.ActualWidth:0.#}x{box.ActualHeight:0.#}");
        }

        // 日期框必须挂上主题模板：默认模板是白底，配上本项目接近白的前景就是
        // 白底白字。模板本身由第 14 项取像素验证，这里验证它被用上了。
        var datePicker = FindAllCore<DatePicker>(view).FirstOrDefault()
            ?? throw new Exception("编辑面板里找不到 DatePicker");

        if (!ReferenceEquals(datePicker.Style, Application.Current!.TryFindResource("DatePickerThemed")))
            throw new Exception("日期框没有使用 DatePickerThemed 模板（默认模板是白底白字）");

        DrainSaves();
        CleanupDate(DateTime.Today);
    }

    // ---- 5. 保存链路 ----

    private static void TestTodayBillSave()
    {
        var vm = new TodayBillViewModel(NewRepository());
        Pump();

        int resets = 0;
        vm.Entries.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                resets++;
        };

        var view = new TodayBill { DataContext = vm };
        Pump();
        Layout(view);

        vm.AddCommand.Execute(null);

        // 空草稿即无效，HasError 必须为 true（保存按钮据此禁用）；提示则要等到
        // 用户碰过字段再显示。
        if (!vm.HasError)
            throw new Exception("空草稿应当判为无效（否则保存按钮会亮着可用）");

        if (!string.IsNullOrEmpty(vm.ErrorMessage))
            throw new Exception($"刚打开面板就显示错误提示: {vm.ErrorMessage}");

        if (vm.SaveCommand.CanExecute(null))
            throw new Exception("空草稿时保存按钮应当不可用");

        // 用户开始输入后才给提示
        vm.DraftDescription = "有描述";
        Report($"只填描述时 提示='{vm.ErrorMessage}'");

        vm.DraftDescription = "冒烟";
        vm.DraftAmountText = "-1";
        Report($"填齐后 提示='{vm.ErrorMessage}' HasError={vm.HasError}");

        if (vm.HasError) throw new Exception("合法草稿时不应判为有错");
        if (!vm.SaveCommand.CanExecute(null)) throw new Exception("合法草稿时保存应可用");

        vm.SaveCommand.Execute(null);

        WaitUntil(() => vm.Entries.Count > 0);

        Report($"条目={vm.Entries.Count} Reset={resets} 提示='{vm.Notice}'");

        if (vm.Entries.Count != 1) throw new Exception($"保存后应有 1 条, 实际 {vm.Entries.Count}");
        if (resets > 0) throw new Exception($"同步过程产生了 {resets} 次 Reset（导致整表闪烁）");

        // 记一笔到历史日期：本页是今日账单，这条不该出现在列表里。
        int todayCountBefore = vm.Entries.Count;

        vm.AddCommand.Execute(null);
        vm.DraftDescription = "历史";
        vm.DraftAmountText = "-2";
        vm.DraftDate = DateTime.Today.AddDays(-10);
        vm.SaveCommand.Execute(null);

        // 不能等条目数回落：记到历史日期的条目本来就不在今日列表里，条目数前后
        // 一样，那个条件一开始就是假的。改等真正的异步结果（提示）。
        WaitUntil(() => vm.HasNotice, rounds: 100);

        Report($"记到历史日期后 条目={vm.Entries.Count}（记之前 {todayCountBefore}）提示='{vm.Notice}'");

        if (vm.Entries.Count != todayCountBefore)
            throw new Exception($"记到历史日期后本页条目数应回落, 实际 {vm.Entries.Count}");

        if (vm.Entries.Any(e => e.Description == "历史"))
            throw new Exception("历史日期的记录仍然显示在今日列表里");

        if (!vm.Entries.Any(e => e.Description == "冒烟"))
            throw new Exception("今天那条记录被误删了");

        if (!vm.HasNotice)
            throw new Exception($"记到历史日期后没有给出提示（提示='{vm.Notice}'）");

        DrainSaves();
        DrainSaves();
        CleanupDate(DateTime.Today);
        CleanupDate(DateTime.Today.AddDays(-10));
    }

    // ---- 6. 日历 ----

    private static void TestCalendar()
    {
        // 先落一条今天的流水：日历明细卡片只在有数据时才生成。用 TodayBill 的
        // 真实保存链路写盘，再让日历从磁盘读。
        var seed = new TodayBillViewModel(NewRepository());
        seed.AddCommand.Execute(null);
        seed.DraftDescription = "日历明细样张";
        seed.DraftAmountText = "-12.5";
        seed.DraftCategory = "测试";
        seed.SaveCommand.Execute(null);

        WaitUntil(() => seed.Entries.Count > 0);

        if (seed.Entries.Count == 0) throw new Exception("播种流水失败, 日历明细无法验证");

        var vm = new CalenderBillViewModel(NewRepository());
        var view = new CalenderBill { DataContext = vm };
        Pump();
        Layout(view);

        WaitUntil(() => vm.Entries.Count > 0);
        Layout(view);

        AssertNoCollapse(view, "CalenderBill(日)");

        var entryCards = FindAllCore<Border>(view)
            .Where(b => b.DataContext is PaperCoin.Core.Models.BillEntry)
            .ToList();

        Report($"日历明细卡片数 = {entryCards.Count}（流水 {vm.Entries.Count} 条）");
        if (entryCards.Count != vm.Entries.Count || entryCards.Count == 0)
            throw new Exception($"明细卡片数与流水数不符: {entryCards.Count} vs {vm.Entries.Count}");

        foreach (var entryCard in entryCards)
        {
            if (HoverScaleOf(entryCard) is null)
                throw new Exception("明细卡片缺少 ScaleTransform, 悬浮动画会抛异常");
            if (entryCard.ActualHeight <= 1)
                throw new Exception($"明细卡片高度塌陷: {entryCard.ActualHeight:0.#}");
        }

        int groupsBeforeEntry = LAE.LAEngine.ActiveGroupCount;

        view.HoverEntryCardForTest(entryCards[0], entering: true);

        // 前后两次取样必须同帧：Play() 同步注册，而 ActiveGroupCount 会随旧动画
        // 组跑完被移除而下降，中间夹一次 Pump 就可能让计数不变。
        int groupsAfterEntry = LAE.LAEngine.ActiveGroupCount;

        Report($"明细悬浮 动画组 {groupsBeforeEntry} -> {groupsAfterEntry}");
        if (groupsAfterEntry <= groupsBeforeEntry)
            throw new Exception("日历明细卡片悬浮没有向 LAE 注册动画 —— 动效没接上");

        Pump();

        view.HoverEntryCardForTest(entryCards[0], entering: false);
        Pump();

        // 悬浮放大同样不能越出滚动视口
        AssertHoverFitsInViewport(view, entryCards, CalenderBill.MaxHoverScale, "CalenderBill(日)");

        var todayBtn = FindButtonByContent(view, "今天")
            ?? throw new Exception("找不到'今天'按钮");

        if (todayBtn.Template is null) throw new Exception("'今天'按钮没有模板（拿不到圆角）");

        var todayBounds = BoundsIn(view, todayBtn);
        Report($"今天按钮 = {todayBounds.X:0.#},{todayBounds.Y:0.#} {todayBounds.Width:0.#}x{todayBounds.Height:0.#}");
        AssertInside(view, todayBounds, "'今天'按钮");

        // 标题不得与"今天"按钮重叠
        var title = FindAllCore<TextBlock>(view)
            .FirstOrDefault(t => t.Text.Contains('年') && t.Text.Contains('月'));
        if (title is not null)
        {
            var tb = BoundsIn(view, title);
            Report($"期间标题 = {tb.X:0.#}..{tb.Right:0.#}   今天按钮左边界 {todayBounds.X:0.#}");
            if (tb.Right > todayBounds.X + 1)
                throw new Exception($"期间标题与'今天'按钮重叠: 标题右 {tb.Right:0.#} > 按钮左 {todayBounds.X:0.#}");
        }

        vm.ShowMonthCommand.Execute(null);
        Pump();
        Layout(view);

        Report($"月视图格子 = {vm.Cells.Count}（应为 42）");
        if (vm.Cells.Count != 42) throw new Exception($"月视图应有 42 格, 实际 {vm.Cells.Count}");

        AssertNoCollapse(view, "CalenderBill(月)");

        vm.ShowYearCommand.Execute(null);
        Pump();
        Layout(view);

        Report($"年视图月份 = {vm.Months.Count}（应为 12）");
        if (vm.Months.Count != 12) throw new Exception($"年视图应有 12 个月, 实际 {vm.Months.Count}");

        var cards = FindAllCore<Border>(view)
            .Where(b => b.DataContext is MonthSummary)
            .ToList();

        Report($"月卡片数 = {cards.Count}");
        if (cards.Count != 12) throw new Exception($"应有 12 张月卡片, 实际 {cards.Count}");

        foreach (var card in cards)
        {
            if (card.RenderTransform is not ScaleTransform)
                throw new Exception("月卡片缺少 RenderTransform, 悬浮动画会抛异常");
            if (card.ActualHeight <= 1)
                throw new Exception($"月卡片高度塌陷: {card.ActualHeight:0.#}");
        }

        var scale = (ScaleTransform)cards[0].RenderTransform;
        double before = scale.ScaleX;

        // 本进程没有渲染循环，LAE 逐帧插值不会推进，数值断言只会测出环境限制。
        // 改用 LAE 自己的注册表计数：Play() 同步注册，与渲染无关。
        // 前后取样之间不能 Pump，否则旧组被移除会让增量看上去是 0。
        int groupsBefore = LAE.LAEngine.ActiveGroupCount;

        view.HoverCardForTest(cards[0], entering: true);

        int groupsAfterHover = LAE.LAEngine.ActiveGroupCount;
        Report($"悬浮前动画组={groupsBefore}, 悬浮后={groupsAfterHover}");
        Report($"  ScaleX={scale.ScaleX:0.###}（本环境时钟不推进, 数值不变属正常）");

        if (groupsAfterHover <= groupsBefore)
            throw new Exception("悬浮没有向 LAE 注册动画 —— 动效没接上");

        Pump();

        view.HoverCardForTest(cards[0], entering: false);
        Pump();

        Report($"移出后动画组={LAE.LAEngine.ActiveGroupCount}");

        AssertNoCollapse(view, "CalenderBill(年)");

        DrainSaves();
        CleanupDate(DateTime.Today);
    }

    // ---- 7. 设置 ----

    private static void TestSettings()
    {
        var config = new ConfigService();
        var theme = new ThemeManager(config);
        var vm = new SettingsViewModel(config, theme);
        var view = new Settings { DataContext = vm };
        Pump();
        Layout(view);

        AssertNoCollapse(view, "Settings");
        Report($"主题={vm.ThemeText} 自启={vm.AutoStart} 自更新={vm.AutoUpdate}");

        // 数据目录必须指向账单文件夹本身：用户点打开想看的是自己的账目，而不是
        // 还要再往里翻一层的应用目录。
        Report($"数据目录 = {vm.DataFolderText}");
        if (!string.Equals(vm.DataFolderText, PaperCoin.Core.Constants.BillFolder,
                StringComparison.Ordinal))
            throw new Exception(
                $"数据目录没有指向账单文件夹: {vm.DataFolderText} != {PaperCoin.Core.Constants.BillFolder}");

        var iconBlocks = FindAllCore<TextBlock>(view)
            .Where(t => t.FontFamily.Source.Contains("iconfont", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Report($"设置页图标数 = {iconBlocks.Count}");
        if (iconBlocks.Count < 5) throw new Exception($"设置页图标太少: {iconBlocks.Count}");

        // 每行标题不得被右侧控件挤没。
        foreach (var label in new[] { "主题", "开机自动启动", "自动更新", "更新源", "数据目录", "版本" })
        {
            var block = FindAllCore<TextBlock>(view).FirstOrDefault(t => t.Text == label);
            if (block is null) throw new Exception($"找不到标签: {label}");
            if (block.ActualWidth <= 1)
                throw new Exception($"标签被挤没了: {label}（宽 {block.ActualWidth:0.#}）");

            var b = BoundsIn(view, block);
            Report($"标签 {label,-6} 位置 {b.X:0.#}..{b.Right:0.#} 宽 {b.Width:0.#}");
        }

        // 每行右侧的交互控件必须完整落在行内
        foreach (var row in FindAllCore<Border>(view)
                     .Where(b => b.DataContext is SettingsViewModel))
        {
            var rb = BoundsIn(view, row);
            foreach (var ctl in FindAllCore<Button>(row).Cast<FrameworkElement>()
                         .Concat(FindAllCore<ComboBox>(row))
                         .Concat(FindAllCore<SwitchToggle>(row)))
            {
                var cb = BoundsIn(view, ctl);
                if (cb.Right > rb.Right + 1)
                    throw new Exception($"行内控件越界: {ctl.GetType().Name} 右 {cb.Right:0.#} > 行右 {rb.Right:0.#}");
            }
        }

        // 主题分段控件必须能完整排下三个选项
        var segmented = FindAllCore<RadioButton>(view).ToList();
        Report($"主题分段按钮数 = {segmented.Count}");
        if (segmented.Count != 3) throw new Exception($"主题应有 3 个选项, 实际 {segmented.Count}");

        double lastRight = segmented.Max(r => BoundsIn(view, r).Right);
        Report($"  最后一个分段右边界 = {lastRight:0.#}（内容区右边界 {ContentWidth:0.#}）");
        if (lastRight > ContentWidth + 1)
            throw new Exception($"主题分段控件超出内容区: {lastRight:0.#} > {ContentWidth:0.#}");

        var switches = FindAllCore<SwitchToggle>(view).ToList();
        Report($"开关数 = {switches.Count}");
        if (switches.Count != 2) throw new Exception($"应有 2 个开关, 实际 {switches.Count}");

        var toggle = switches[0];
        bool stateBefore = toggle.IsOn;
        double knobBefore = KnobX(toggle);
        Report($"开关 前: IsOn={stateBefore} KnobX={knobBefore:0.#}");

        ClickElement(toggle);
        Pump();

        double knobAfter = KnobX(toggle);
        Report($"开关 后: IsOn={toggle.IsOn} KnobX={knobAfter:0.#} VM={vm.AutoStart}");

        if (toggle.IsOn == stateBefore)
            throw new Exception("点击开关后状态没有变化（命令没通到 ViewModel）");
        if (Math.Abs(knobAfter - knobBefore) < 0.5)
            throw new Exception("状态变了但滑块没动（动画没跑）");

        ClickElement(toggle);
        Pump();
        Report($"开关已还原: IsOn={toggle.IsOn}");

        var combo = FindAllCore<ComboBox>(view).FirstOrDefault()
            ?? throw new Exception("找不到下拉框");

        if (combo.Template is null) throw new Exception("下拉框没有模板（会是灰色系统控件）");

        var comboBounds = BoundsIn(view, combo);
        Report($"下拉框 = {comboBounds.X:0.#},{comboBounds.Y:0.#} {comboBounds.Width:0.#}x{comboBounds.Height:0.#} 项数={combo.Items.Count}");
        Report($"  IsEnabled={combo.IsEnabled} IsHitTestVisible={combo.IsHitTestVisible} IsDropDownOpen={combo.IsDropDownOpen}");
        AssertInside(view, comboBounds, "下拉框");

        // 通过模板里的 ToggleButton 驱动，而不是直接赋 IsDropDownOpen：直接赋值
        // 会被 ComboBox 立刻算回 false，分不清是模板没接通还是环境里弹不出来。
        var comboToggle = FindAllCore<System.Windows.Controls.Primitives.ToggleButton>(combo).FirstOrDefault();
        if (comboToggle is null) throw new Exception("下拉框模板里没有 ToggleButton（绑定链断了）");

        Report($"  模板内 ToggleButton 找到, IsChecked(前)={comboToggle.IsChecked}");

        comboToggle.IsChecked = true;
        Pump();

        var popup = combo.Template.FindName("PART_Popup", combo) as System.Windows.Controls.Primitives.Popup;
        Report($"  设 IsChecked=true 后: ComboBox.IsDropDownOpen={combo.IsDropDownOpen} " +
               $"ToggleButton.IsChecked={comboToggle.IsChecked} PART_Popup={(popup is null ? "未找到" : popup.IsOpen.ToString())}");

        if (popup is null) throw new Exception("模板里找不到 PART_Popup（ComboBox 交互会失效）");

        // 后台无窗口的进程里 Popup 可能拿不到呈现源而根本开不了。不能凭这一点
        // 断定模板有问题，所以先直接开一次，区分模板缺陷与环境限制。
        popup.IsOpen = true;
        Pump();
        bool popupCanOpen = popup.IsOpen;
        popup.IsOpen = false;
        Pump();

        Report($"  直接开 Popup 成功? {popupCanOpen}");

        if (!popupCanOpen)
        {
            Report("  结论: 当前环境无法弹出 Popup, 展开行为无法在此验证（非模板缺陷）");
            Report("  改为静态校验模板结构是否满足 ComboBox 的契约");
            AssertComboTemplateContract(combo);
            return;
        }

        if (combo.IsDropDownOpen is false)
            throw new Exception("ToggleButton 已勾选但 ComboBox 没有展开 —— 模板绑定链断了");

        combo.SelectedIndex = combo.Items.Count - 1;
        Pump();
        Report($"选择后 = {combo.SelectedItem}");

        comboToggle.IsChecked = false;
        Pump();
    }

    /// <summary>
    /// ComboBox 模板的结构性契约：缺任何一条都会让控件内部逻辑静默失配。
    /// Popup 里的内容在没有真实弹出时不会进入可视树，所以这里查逻辑树。
    /// </summary>
    private static void AssertComboTemplateContract(ComboBox combo)
    {
        var names = new List<string>();
        CollectNames(combo, names);
        Report($"  模板内具名元素: {string.Join(", ", names)}");

        // ComboBox 内部就是这样取 PART_Popup 的
        if (combo.Template.FindName("PART_Popup", combo)
            is not System.Windows.Controls.Primitives.Popup popup)
            throw new Exception("模板缺少 PART_Popup（ComboBox 无法展开）");

        // ToggleButton.IsChecked 必须真的绑到 IsDropDownOpen
        var toggle = FindAllCore<System.Windows.Controls.Primitives.ToggleButton>(combo).FirstOrDefault();
        if (toggle is null) throw new Exception("模板缺少 ToggleButton（点标题栏没反应）");

        var binding = System.Windows.Data.BindingOperations.GetBinding(
            toggle, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty);

        Report($"  ToggleButton.IsChecked 绑定 = {binding?.Path.Path ?? "无（硬编码，交互会失效）"}");

        if (binding is null)
            throw new Exception("ToggleButton.IsChecked 没有绑定到 IsDropDownOpen");

        var presenter = FindInLogicalTree<System.Windows.Controls.ItemsPresenter>(popup.Child)
            ?? FindInLogicalTree<System.Windows.Controls.ItemsPresenter>(popup);

        if (presenter is null)
            throw new Exception("Popup 内没有 ItemsPresenter（展开后不会有内容）");

        Report("  Popup 内找到 ItemsPresenter");
    }

    private static T? FindInLogicalTree<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        if (root is T hit) return hit;
        if (root is not FrameworkElement fe) return null;

        foreach (var child in LogicalTreeHelper.GetChildren(fe))
        {
            var found = FindInLogicalTree<T>(child as DependencyObject);
            if (found is not null) return found;
        }
        return null;
    }

    private static void CollectNames(DependencyObject root, List<string> names)
    {
        if (root is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name)) names.Add(fe.Name);

        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            CollectNames(VisualTreeHelper.GetChild(root, i), names);
    }

    // ---- 8. 主题 ----

    private static void TestThemeModes()
    {
        var config = new ConfigService();
        var theme = new ThemeManager(config);

        Report($"系统当前主题 = {ThemeManager.GetSystemTheme()}");

        foreach (var mode in new[] { "System", "Bright", "Dark", "System" })
        {
            theme.ApplyMode(mode);
            Report($"  {mode,-7} -> 模式={theme.Mode,-7} 实际={theme.Resolved}");
        }

        theme.ApplyMode("System");
        if (theme.Resolved != ThemeManager.GetSystemTheme())
            throw new Exception("跟随系统解析不正确");

        theme.ApplyMode("乱写的值");
        if (theme.Resolved != "Dark") throw new Exception("非法模式没有回退到 Dark");

        theme.ApplyMode("Dark");
    }

    // ---- 9. 滚动条 ----

    /// <summary>
    /// 纵向滚动条必须用项目自己的装饰模板。造一个内容高于视口的真实滚动区来断言，
    /// 因为隐式样式没解析到或模板缺 PART_Track 都只会静默失效。
    /// </summary>
    private static void TestScrollBarDecoration()
    {
        var host = new Grid { Width = 220, Height = 120 };

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        scroller.Content = new Border { Height = 800, Background = Brushes.Transparent };
        host.Children.Add(scroller);

        host.Measure(new Size(220, 120));
        host.Arrange(new Rect(0, 0, 220, 120));
        host.UpdateLayout();

        var bar = FindAllCore<ScrollBar>(scroller)
                      .FirstOrDefault(b => b.Orientation == Orientation.Vertical)
                  ?? throw new Exception("滚动区里没有纵向滚动条");

        var defaultWidth = SystemParameters.VerticalScrollBarWidth;
        Report($"滚动条 {bar.ActualWidth:0.#}x{bar.ActualHeight:0.#} 可见性={bar.Visibility} " +
               $"宽度={bar.Width}（系统默认 {defaultWidth}）");

        if (bar.Visibility != Visibility.Visible)
            throw new Exception("内容超出视口, 但滚动条没有显示");

        // 认模板而不是认颜色
        var expected = Application.Current?.TryFindResource("ThemedVerticalScrollBar")
            as ControlTemplate;

        if (expected is null)
            throw new Exception("资源里没有 ThemedVerticalScrollBar, 滚动条装饰没有落地");

        if (!ReferenceEquals(bar.Template, expected))
            throw new Exception("滚动条没有使用本项目的装饰模板（仍是系统默认外观）");

        if (bar.Width >= defaultWidth)
            throw new Exception($"滚动条没有变细: {bar.Width} ≥ 系统默认 {defaultWidth}");

        // PART_Track 是模板契约，缺失时滚动条画得出来但拖不动
        if (bar.Template.FindName("PART_Track", bar) is not Track track)
            throw new Exception("滚动条模板缺少 PART_Track（滚动会静默失效）");

        // 轨道必须真的接到 ScrollBar 上，否则滑块会占满整条轨道
        Report($"轨道 Maximum={track.Maximum:0.#} ViewportSize={track.ViewportSize:0.#}");
        if (track.ViewportSize <= 0 || track.Maximum <= track.ViewportSize)
            throw new Exception("轨道没有从 ScrollBar 取到滚动范围（滑块会占满整条轨道）");

        var thumb = track.Thumb ?? throw new Exception("滚动条没有滑块");

        var pill = FindAllCore<Border>(thumb).FirstOrDefault(b => b.CornerRadius.TopLeft > 0)
            ?? throw new Exception("滑块没有圆角装饰");

        Report($"滑块 {thumb.ActualWidth:0.#}x{thumb.ActualHeight:0.#} 圆角={pill.CornerRadius.TopLeft}");

        if (thumb.ActualHeight >= track.ActualHeight)
            throw new Exception($"滑块占满了整条轨道, 比例没生效: {thumb.ActualHeight:0.#}");

        if (thumb.ActualHeight < 16)
            throw new Exception($"滑块太短, 不好抓: {thumb.ActualHeight:0.#}");

        // 轨道两端是两个翻页热区，不是带箭头图形的系统按钮
        var pageButtons = FindAllCore<RepeatButton>(track).ToList();
        Report($"轨道内翻页热区 {pageButtons.Count} 个");

        if (pageButtons.Count != 2)
            throw new Exception($"轨道内的翻页热区应为 2 个, 实际 {pageButtons.Count}");

        if (pageButtons.Any(b => FindAllCore<System.Windows.Shapes.Shape>(b).Any()))
            throw new Exception("翻页热区里还留着箭头图形（系统默认箭头没被换掉）");
    }

    // ---- 布局体检 ----

    /// <summary>
    /// 找可见且有内容却量出零尺寸的元素。
    /// 折叠容器里的子元素自身 <c>Visibility</c> 仍是 Visible 但量出来是 0，
    /// 属于正常情况，排除祖先折叠才不会有假阳性。
    /// </summary>
    private static void AssertNoCollapse(FrameworkElement root, string label)
    {
        var suspects = new List<string>();

        foreach (var fe in FindAllCore<FrameworkElement>(root))
        {
            if (!IsEffectivelyVisible(fe)) continue;

            string? content = fe switch
            {
                TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text) => tb.Text,
                Button b when b.Content is not null => b.Content.ToString(),
                _ => null
            };

            if (content is null) continue;

            if (fe.ActualWidth <= 0.01 || fe.ActualHeight <= 0.01)
                suspects.Add($"{fe.GetType().Name}(\"{content}\")");
        }

        if (suspects.Count > 0)
            throw new Exception($"[{label}] {suspects.Count} 个可见元素尺寸为 0: " +
                                string.Join(", ", suspects.Take(6)));

        var bounds = BoundsIn(root, root);
        Report($"[{label}] 内容 {bounds.Width:0.#} x {bounds.Height:0.#}");

        if (bounds.Width > ContentWidth + 1)
            throw new Exception($"[{label}] 内容宽度 {bounds.Width:0.#} 超出可用 {ContentWidth:0.#}");
    }

    /// <summary>沿可视树向上检查，任一祖先折叠则该元素实际不可见</summary>
    private static bool IsEffectivelyVisible(DependencyObject node)
    {
        var current = node;
        while (current is not null)
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
            current = VisualTreeHelper.GetParent(current);
        }
        return true;
    }

    /// <summary>
    /// 卡片放大到悬浮峰值后必须仍完整落在滚动视口内。
    /// 直接把 <see cref="ScaleTransform"/> 写成峰值再量，不等动画跑完：本环境
    /// 没有渲染循环，等动画只会测出环境限制，峰值是否越界与动画无关。
    /// </summary>
    private static void AssertHoverFitsInViewport(
        FrameworkElement root, IReadOnlyList<Border> cards, double peakScale, string label)
    {
        if (cards.Count == 0) throw new Exception($"[{label}] 没有可测的卡片");

        var card = cards[0];

        var scroll = FindAllCore<ScrollViewer>(root).FirstOrDefault(s => card.IsDescendantOf(s))
            ?? throw new Exception($"[{label}] 卡片不在任何 ScrollViewer 里, 测试前提不成立");

        var scale = HoverScaleOf(card)
            ?? throw new Exception($"[{label}] 卡片缺少 ScaleTransform, 悬浮动画会抛异常");

        double raw = card.ActualWidth;
        scale.ScaleX = peakScale;
        scale.ScaleY = peakScale;

        // 留白是按 ScrollViewer 的 ActualWidth 算的绑定，需要几次布局才落到元素上。
        for (int i = 0; i < 3; i++)
        {
            Layout(root);
            Pump();
        }

        var bounds = BoundsIn(scroll, card);
        Report($"[{label}] 卡片宽 {raw:0.#} 放大到 {peakScale:0.##} 后 " +
               $"左 {bounds.Left:0.##} 右 {bounds.Right:0.##}（视口 {scroll.ActualWidth:0.#}）");
        Report($"[{label}]   上 {bounds.Top:0.##} 下 {bounds.Bottom:0.##}" +
               $"（视口高 {scroll.ActualHeight:0.#}, 卡片高 {card.ActualHeight:0.#}）");

        scale.ScaleX = 1;
        scale.ScaleY = 1;
        Layout(root);

        if (bounds.Left < -0.5 || bounds.Right > scroll.ActualWidth + 0.5)
            throw new Exception(
                $"[{label}] 悬浮放大后卡片越出视口, 两边会被裁掉: " +
                $"左 {bounds.Left:0.##} 右 {bounds.Right:0.##} 视口 {scroll.ActualWidth:0.##}");

        // 纵向同理：列表首张卡片贴在视口上沿，放大后上边会被裁掉。横向留白按
        // 视口宽度算，纵向靠列表自身的上边距留出。
        if (bounds.Top < -0.5)
            throw new Exception(
                $"[{label}] 悬浮放大后卡片上边越出视口, 上边框会被裁掉: " +
                $"上 {bounds.Top:0.##}（视口高 {scroll.ActualHeight:0.##}）");

        if (bounds.Bottom > scroll.ActualHeight + 0.5)
            throw new Exception(
                $"[{label}] 悬浮放大后卡片下边越出视口: " +
                $"下 {bounds.Bottom:0.##}（视口高 {scroll.ActualHeight:0.##}）");
    }

    /// <summary>
    /// 取卡片上驱动悬浮的 <see cref="ScaleTransform"/>：裸的 ScaleTransform，
    /// 或 TransformGroup 的第一项。
    /// </summary>
    private static ScaleTransform? HoverScaleOf(FrameworkElement card)
    {
        if (card.RenderTransform is ScaleTransform direct) return direct;

        if (card.RenderTransform is TransformGroup group
            && group.Children.Count > 0
            && group.Children[0] is ScaleTransform first)
            return first;

        return null;
    }

    /// <summary>
    /// 删除按钮不得与金额文本重叠。断言几何关系而不是列号，换布局方式也不会
    /// 让这条失败。
    /// </summary>
    private static void AssertDeleteButtonClearOfAmount(Border card)
    {
        if (card.DataContext is not PaperCoin.Core.Models.BillEntry entry) return;

        var amount = FindAllCore<TextBlock>(card)
                         .FirstOrDefault(t => t.Text == entry.AmountText)
                     ?? throw new Exception($"找不到金额文本: {entry.AmountText}");

        var del = FindAllCore<Button>(card).FirstOrDefault(b => b.Name == "DeleteBtn")
                  ?? throw new Exception("找不到删除按钮 DeleteBtn");

        var amountBounds = BoundsIn(card, amount);
        var buttonBounds = BoundsIn(card, del);

        // 边缘正好相接不算重叠，所以先收掉半像素再判
        buttonBounds.Inflate(-0.5, -0.5);

        if (amountBounds.IntersectsWith(buttonBounds))
            throw new Exception(
                $"删除按钮压在金额上: 金额 {amountBounds.X:0.#},{amountBounds.Y:0.#} " +
                $"{amountBounds.Width:0.#}x{amountBounds.Height:0.#} / " +
                $"按钮 {buttonBounds.X:0.#},{buttonBounds.Y:0.#} " +
                $"{buttonBounds.Width:0.#}x{buttonBounds.Height:0.#}");
    }

    private static void AssertInside(FrameworkElement root, Rect child, string label)
    {
        var rootBounds = BoundsIn(root, root);

        if (child.Right > rootBounds.Right + 1)
            throw new Exception($"{label} 右侧越界: {child.Right:0.#} > {rootBounds.Right:0.#}");

        if (child.Left < rootBounds.Left - 1)
            throw new Exception($"{label} 左侧越界: {child.Left:0.#} < {rootBounds.Left:0.#}");
    }

    private static Rect BoundsIn(Visual root, Visual child)
    {
        var t = child.TransformToAncestor(root);
        var fe = (FrameworkElement)child;
        return t.TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
    }

    private static void ClickElement(UIElement element)
    {
        element.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0,
            System.Windows.Input.MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    }

    private static double KnobX(SwitchToggle toggle)
    {
        var knob = FindField<Border>(toggle, "Knob");
        return knob?.RenderTransform is TranslateTransform tt ? tt.X : double.NaN;
    }

    // ---- 基础设施 ----

    private static void BootstrapResources(Application app)
    {
        foreach (var source in new[]
                 {
                     "/Themes/Dark.xaml", "/Themes/Controls.xaml",
                     "/Icon/TitleIcon.xaml", "/Icon/SiderBarIcon.xaml",
                     "/Icon/ButtonIcon.xaml", "/Icon/AppIcons.xaml"
                 })
        {
            app.Resources.MergedDictionaries.Add(LoadDict(source));
        }

        app.Resources["Iconfont"] =
            new FontFamily("pack://application:,,,/PaperCoin;component/Icon/iconfont.ttf#iconfont");
    }

    /// <summary>
    /// 建一个与 App.OnStartup 等价的服务容器：MainWindow 换页会走
    /// <c>App.Services</c>，不装容器就直接空引用。
    /// </summary>
    private static void BootstrapServices()
    {
        var root = RepoRoot();
        if (root is not null) Environment.CurrentDirectory = root;

        var services = new ServiceCollection();

        var config = new ConfigService();
        var theme = new ThemeManager(config);

        services.AddSingleton(config);
        services.AddSingleton(theme);
        services.AddSingleton<BillRepository>();
        services.AddTransient<TodayBillViewModel>();
        services.AddTransient<CalenderBillViewModel>();
        services.AddTransient<SettingsViewModel>();

        PaperCoin.App.Services = services.BuildServiceProvider();
    }

    private static ResourceDictionary LoadDict(string relative)
        => new() { Source = new Uri($"pack://application:,,,/PaperCoin;component{relative}") };

    private static BillRepository NewRepository() => new();

    private static string? RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PaperCoin.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir;
    }

    private static void CleanupDate(DateTime date)
    {
        var dir = Path.Combine(
            PaperCoin.Core.Constants.BillFolder,
            date.Year.ToString(), date.Month.ToString(), date.Day.ToString());
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// 等到条件成立。每轮让出一点真实时间：<c>Pump</c> 本身几乎不耗时，
    /// 空转几百次也等不到一次磁盘 IO 完成；条件成立立刻返回。
    /// </summary>
    private static bool WaitUntil(Func<bool> condition, int rounds = 60, int sleepMs = 20)
    {
        for (int i = 0; i < rounds; i++)
        {
            if (condition()) return true;

            Pump();
            Thread.Sleep(sleepMs);
        }

        return condition();
    }

    /// <summary>
    /// 把还在飞的落盘排空。保存是即发即忘的，测试收尾会直接删账单目录，
    /// 不等它落完就删会让写和删撞在一起。
    /// </summary>
    private static void DrainSaves(int pumps = 40)
    {
        for (int i = 0; i < pumps; i++) Pump();
    }

    /// <summary>清空整个账单数据目录，保证每轮测试从同一状态开始</summary>
    private static void ResetBillsData()
    {
        var dir = PaperCoin.Core.Constants.BillFolder;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(ContentWidth, WindowHeight - 32));
        element.Arrange(new Rect(0, 0, ContentWidth, WindowHeight - 32));
        element.UpdateLayout();
    }

    private static void Pump()
    {
        for (int i = 0; i < 6; i++)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }

    private static T? FindField<T>(object owner, string name) where T : class
    {
        var field = owner.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);

        return field?.GetValue(owner) as T;
    }

    private static Button? FindButtonByContent(DependencyObject root, string content)
        => FindAllCore<Button>(root).FirstOrDefault(b => b.Content?.ToString() == content);

    private static IEnumerable<T> FindAllCore<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;

            foreach (var deep in FindAllCore<T>(child)) yield return deep;
        }
    }

    // ---- 10. 空状态提示入场 ----

    /// <summary>
    /// 流水为空时的提示要有入场动效，而不是跟着 Visibility 硬切。
    /// 断言播放次数而不是动画当时的数值：本环境渲染帧稀疏，读 Opacity 只会读到收尾值。
    /// </summary>
    private static void TestEmptyHintAnimation()
    {
        // ── TodayBill: 走"非空 -> 空"的真实路径(删掉最后一条) ──
        var vm = new TodayBillViewModel(NewRepository());
        var view = new TodayBill { DataContext = vm };
        Pump();
        Layout(view);

        vm.AddCommand.Execute(null);
        vm.DraftDescription = "空状态样张";
        vm.DraftAmountText = "-1";
        vm.SaveCommand.Execute(null);

        WaitUntil(() => vm.Entries.Count > 0);

        if (vm.Entries.Count != 1) throw new Exception($"播种失败, 条目数={vm.Entries.Count}");

        var todayHint = FindField<TextBlock>(view, "EmptyHint")
            ?? throw new Exception("TodayBill 找不到 EmptyHint");

        if (vm.IsEmpty) throw new Exception("测试前提不成立: ViewModel 认为列表还是空的");

        // 条目数变成 1 与 IsEmpty 通知发生在同一次续体里，而绑定把通知搬到
        // 目标属性上可能排在下一轮，不等就断言会读到上一帧。
        WaitUntil(() => todayHint.Visibility == Visibility.Collapsed);

        if (todayHint.Visibility != Visibility.Collapsed)
            throw new Exception($"还有数据时空提示仍然可见: {todayHint.Visibility}");

        int playsBefore = view.EmptyHintPlayCount;
        Report($"删除前 空提示播放次数={playsBefore} 可见性={todayHint.Visibility}");

        vm.RemoveCommand.Execute(vm.Entries[0]);

        WaitUntil(() => vm.Entries.Count == 0);

        // 等的是播放次数涨了，不是它可见了：提示的显示由绑定驱动，而入场是
        // 另一条 Render 优先级的回调，两者不同步，只等可见性会在入场还没起之前就往下走。
        WaitUntil(() => view.EmptyHintPlayCount > playsBefore);

        Layout(view);

        if (vm.Entries.Count != 0) throw new Exception($"删除后列表没有变空, 条目数={vm.Entries.Count}");
        if (todayHint.Visibility != Visibility.Visible)
            throw new Exception($"列表空了但提示没显示: {todayHint.Visibility}");

        var todayShift = FindField<TranslateTransform>(view, "EmptyHintShift")
            ?? throw new Exception("TodayBill 找不到 EmptyHintShift");

        Report($"删空后 播放次数={view.EmptyHintPlayCount} " +
               $"Opacity={todayHint.Opacity:0.###} Y={todayShift.Y:0.##} " +
               $"动画在跑={LAE.LAEngine.IsRunning(TodayBill.EmptyHintAnimationName)}");

        if (view.EmptyHintPlayCount <= playsBefore)
            throw new Exception("列表变空时没有播空状态入场动效");

        // 磁盘也必须真的空了。SaveAsync 按日期分组落盘，空集合一个分组都没有，
        // 只删界面不删文件的话，重启之后那条会回来。
        var dayDir = Path.Combine(
            PaperCoin.Core.Constants.BillFolder,
            DateTime.Today.Year.ToString(),
            DateTime.Today.Month.ToString(),
            DateTime.Today.Day.ToString());

        var dayFile = Path.Combine(dayDir, "bills.json");
        Report($"删空后 bills.json 还在? {File.Exists(dayFile)}; 日目录还在? {Directory.Exists(dayDir)}");

        if (File.Exists(dayFile))
            throw new Exception($"删掉最后一条后 {dayFile} 还在, 重启之后它会回来");

        // ── CalenderBill: 同一个"空"再从磁盘读一遍 ──
        // 不手动清目录：上面删完磁盘就该是空的，用全新仓库读盘正好验证删除确实落了盘。
        var cvm = new CalenderBillViewModel(NewRepository());
        var cal = new CalenderBill { DataContext = cvm };
        Pump();
        Layout(cal);

        WaitUntil(() => cal.EmptyHintPlayCount > 0);
        Layout(cal);

        var calHint = FindField<TextBlock>(cal, "EmptyHint")
            ?? throw new Exception("CalenderBill 找不到 EmptyHint");

        if (cvm.Entries.Count != 0)
            throw new Exception($"测试前提不成立: 日历明细应为空, 实际 {cvm.Entries.Count}");

        if (calHint.Visibility != Visibility.Visible)
            throw new Exception($"日历明细为空但提示没显示: {calHint.Visibility}");

        Report($"日历 空提示播放次数={cal.EmptyHintPlayCount} " +
               $"Opacity={calHint.Opacity:0.###} " +
               $"动画在跑={LAE.LAEngine.IsRunning(CalenderBill.EmptyHintAnimationName)}");

        if (cal.EmptyHintPlayCount == 0)
            throw new Exception("日历明细为空时没有播空状态入场动效");
    }

    // ---- 11. 连续保存串行化 ----

    /// <summary>
    /// 同一个文件上的并发保存必须被仓库串行化。直接在仓库上并发发起保存，
    /// 而不是走 ViewModel 连点保存：后者的两个写句柄能否真的重叠取决于那次写是否同步完成。
    /// 没有串行化时，后开的写句柄会吃 IOException（FileShare.Read 的共享冲突）。
    /// </summary>
    private static void TestSerializedSaves()
    {
        DrainSaves();
        CleanupDate(DateTime.Today);

        var repo = NewRepository();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var tasks = new List<Task>();
        for (int i = 0; i < 8; i++)
        {
            int n = i;
            tasks.Add(Task.Run(() => repo.SaveAsync(
            [
                new PaperCoin.Core.Models.BillEntry
                {
                    Description = $"并发保存 {n}",
                    Amount = -(n + 1),
                    Category = "测试",
                    Date = today
                }
            ])));
        }

        // WaitAll 遇到失败的任务会直接抛 AggregateException，把下面逐个报告的那段盖掉，
        // 所以这里只取有没有等完，失败详情交给下面。
        bool finished;
        try
        {
            finished = Task.WaitAll([.. tasks], 30_000);
        }
        catch (AggregateException)
        {
            finished = true;
        }

        var failures = tasks.Where(t => t.IsFaulted).ToList();
        foreach (var t in failures)
            Report($"  写入失败: {t.Exception?.GetBaseException().GetType().Name}: " +
                   $"{t.Exception?.GetBaseException().Message}");

        Report($"并发保存 {tasks.Count} 次: 全部完成={finished}, 失败={failures.Count}");

        if (!finished) throw new Exception("并发保存没有在 30 秒内完成");

        if (failures.Count > 0)
            throw new Exception($"并发保存之间有 {failures.Count} 次撞车 —— 写路径没有串行化");

        // 落盘结果必须是一个完整的 JSON 数组, 不能是被两个写句柄交错截断的残片
        var dayDir = Path.Combine(
            PaperCoin.Core.Constants.BillFolder,
            DateTime.Today.Year.ToString(),
            DateTime.Today.Month.ToString(),
            DateTime.Today.Day.ToString());

        var json = File.ReadAllText(Path.Combine(dayDir, "bills.json"));
        var count = System.Text.Json.JsonDocument.Parse(json).RootElement.GetArrayLength();

        Report($"磁盘上 {count} 条, 文件 {json.Length} 字节");
        if (count != 1)
            throw new Exception($"每次保存写的都是完整的一天, 最后一次应为 1 条, 实际 {count}");

        DrainSaves();
        CleanupDate(DateTime.Today);
    }

    // ---- 12. 卡片入场与删除回流 ----

    /// <summary>
    /// 卡片入场只在进入页面后的第一批播一次，删除时后面的卡片要滑上来。
    /// 断言播放计数而不是动画当时的数值：LAE 按真实时间推进，读 Opacity 或坐标可能读到终值。
    /// 计数只在 Play() 之后自增，删掉播放语句就掉回 0。
    /// </summary>
    private static void TestCardMotion()
    {
        DrainSaves();
        CleanupDate(DateTime.Today);

        // 先把 3 条写到磁盘，让待测视图的首屏加载一次到货 3 张卡片：应用启动或切回本页时，
        // ViewModel 就是一次拉回整天数据的。一条一条边存边看是另一条路径，那时只有第一张算入场。
        var seedVm = new TodayBillViewModel(NewRepository());

        for (int i = 0; i < 3; i++)
        {
            seedVm.AddCommand.Execute(null);
            seedVm.DraftDescription = $"动效样张 {i + 1}";
            seedVm.DraftAmountText = $"-{(i + 1) * 7}";
            seedVm.SaveCommand.Execute(null);
            for (int p = 0; p < 20; p++) Pump();
        }

        WaitUntil(() => seedVm.Entries.Count >= 3);

        if (seedVm.Entries.Count != 3)
            throw new Exception($"播种失败, 只有 {seedVm.Entries.Count} 条; 提示='{seedVm.Notice}'");

        // 待测视图: 它的 ViewModel 会从磁盘一次读回这 3 条
        var vm = new TodayBillViewModel(NewRepository());
        var view = new TodayBill { DataContext = vm };

        // 还没跑过任何一轮调度: 数据和容器都还没到, 不该有任何入场
        if (view.CardsEntrancePlayCount != 0)
            throw new Exception("视图刚建好就播了入场");

        Pump();
        Layout(view);

        WaitUntil(() => vm.Entries.Count >= 3);

        Layout(view);
        for (int i = 0; i < 10; i++) Pump();
        Layout(view);

        if (vm.Entries.Count != 3) throw new Exception($"应有 3 条, 实际 {vm.Entries.Count}");

        int afterFirstBatch = view.CardsEntrancePlayCount;
        Report($"首批入场播放张数 = {afterFirstBatch}");

        if (afterFirstBatch != 3)
            throw new Exception($"3 张卡片应各播一次入场, 实际 {afterFirstBatch} 次");

        // 错开顺序的前提：这一批必须是完整的，且按列表顺序，序号就是从这里来的。
        // 这两条与动画时钟无关，任何时候都能断言。
        var rows = CardRows(view);
        Report($"行数={rows.Count} 条目数={vm.Entries.Count}");

        if (rows.Count != vm.Entries.Count)
            throw new Exception($"行数与条目数不符: {rows.Count} vs {vm.Entries.Count}");

        for (int i = 0; i < rows.Count; i++)
        {
            if (!ReferenceEquals(rows[i].DataContext, vm.Entries[i]))
                throw new Exception($"第 {i + 1} 行的数据不是列表里的第 {i + 1} 条 —— 错开顺序会错位");
        }

        Report("行顺序与列表顺序一致");

        // 动画还在跑时断言进度从前往后递减（越靠前透明度越高）。
        // 本环境渲染帧稀疏，可能醒来时动画已经跑完（读到 1, 1, 1），那时这条没有信息量，只留报告。
        Thread.Sleep(150);
        Pump();

        var staggered = RowStates(view).Select(s => s.Opacity).ToList();
        Report($"错开检查 opacity = {string.Join(", ", staggered.Select(o => o.ToString("0.###")))}");

        if (staggered.Count > 0 && staggered[^1] < 0.999)
        {
            for (int i = 1; i < staggered.Count; i++)
            {
                if (staggered[i] > staggered[i - 1] + 0.001)
                    throw new Exception(
                        "入场顺序不对: 靠后的卡片比靠前的跑得还快 " +
                        $"({string.Join(", ", staggered.Select(o => o.ToString("0.###")))})");
            }
        }

        // 入场必须能落回静止态（动画被打断、或起点写了却没动画拉回来，都会让卡片停在半路），
        // 所以除了“播了没有”，还要断言“收得住”。
        if (!WaitForRest(view))
        {
            foreach (var (opacity, shift) in RowStates(view))
                Report($"  未落定: opacity={opacity:0.###} shift={shift:0.##}");

            throw new Exception("入场没有落回静止态");
        }

        Layout(view);
        foreach (var (opacity, shift) in RowStates(view))
            Report($"  静止态: opacity={opacity:0.###} shift={shift:0.##}");

        // 再存一笔：列表会变，但入场不能再放，否则每存一次整屏弹一下
        vm.AddCommand.Execute(null);
        vm.DraftDescription = "后来的一笔";
        vm.DraftAmountText = "-9";
        vm.SaveCommand.Execute(null);

        WaitUntil(() => vm.Entries.Count >= 4);
        Layout(view);
        for (int i = 0; i < 10; i++) Pump();
        Layout(view);

        Report($"保存第 4 笔之后 入场播放张数 = {view.CardsEntrancePlayCount}");

        if (view.CardsEntrancePlayCount != afterFirstBatch)
            throw new Exception(
                $"保存后重播了卡片入场({afterFirstBatch} -> {view.CardsEntrancePlayCount}), " +
                "整表会跟着弹一下");

        // ── 删除回流 ──
        var shifts = RowShifts(view);
        Report($"删除前各卡片的行位移 = {string.Join(", ", shifts.Select(y => y.ToString("0.#")))}");

        var firstBefore = shifts[0];
        int reflowBefore = view.CardReflowPlayCount;

        // 冻住动画时钟再删。回流位移是在布局回调里同步写入的，而动画每帧都会把它拉向 0，
        // 不冻住的话测到的值取决于那一轮磁盘 IO 花了多久，断言只能碰运气。
        LAE.LAEngine.Freeze();
        try
        {
            vm.RemoveCommand.Execute(vm.Entries[1]);      // 删中间那条

            Layout(view);                                  // 触发 LayoutUpdated
            Pump();

            var after = RowShifts(view);
            Report($"删除中间一条后 行位移 = {string.Join(", ", after.Select(y => y.ToString("0.#")))} " +
                   $"（回流播放 {view.CardReflowPlayCount} 次）");

            if (after.Count != 3) throw new Exception($"删除后应有 3 张卡片, 实际 {after.Count}");

            if (view.CardReflowPlayCount <= reflowBefore)
                throw new Exception("删除后没有播回流动效, 后面的卡片是直接跳上去的");

            // 被删卡片上方的那张不该动
            if (Math.Abs(after[0] - firstBefore) > 0.5)
                throw new Exception($"被删卡片上方的卡片也动了: {firstBefore:0.#} -> {after[0]:0.#}");

            // 下方那张应该先被摆回旧位置，位移应当接近整行高度而不是 0
            if (after[^1] < 30)
                throw new Exception(
                    $"后面的卡片没有从旧位置开始滑(位移 {after[^1]:0.#}), 等于直接跳到位了");

            Report($"最下面那张起始位移 = {after[^1]:0.#}（一行高约 63.5, 动画已归零则为 0）");
        }
        finally
        {
            LAE.LAEngine.Unfreeze();
        }

        // ── 切页面 ──
        // MainWindow 每次导航都会给页面换一个新的 ViewModel(见
        // UpdatePageDataContext), 所以"换 DataContext"就是"切回这一页"。
        // 换完之后必须重新播一次入场, 否则只有进程启动那一次看得到。
        int beforeSwitch = view.CardsEntrancePlayCount;

        var second = new TodayBillViewModel(NewRepository());
        view.DataContext = second;

        Pump();
        Layout(view);
        WaitUntil(() => second.Entries.Count > 0);
        Layout(view);
        for (int i = 0; i < 10; i++) Pump();
        Layout(view);

        Report($"切页面后 入场播放张数 = {beforeSwitch} -> {view.CardsEntrancePlayCount} " +
               $"（当前 {second.Entries.Count} 条）");

        if (view.CardsEntrancePlayCount <= beforeSwitch)
            throw new Exception("换页面之后没有重播卡片入场");

        DrainSaves();
        CleanupDate(DateTime.Today);
    }

    /// <summary>按显示顺序取每张卡片的行状态(不透明度 / 位移), 用于判断有没有落回静止态</summary>
    private static List<(double Opacity, double Shift)> RowStates(
        FrameworkElement view, string listName = "EntryList")
    {
        var result = new List<(double, double)>();
        foreach (var row in CardRows(view, listName))
        {
            if (row.RenderTransform is not TranslateTransform shift)
                throw new Exception("行格子缺少 TranslateTransform");

            result.Add((row.Opacity, shift.Y));
        }

        return result;
    }

    /// <summary>按显示顺序取每张卡片"行格子"上的纵向量(动画中间态)</summary>
    private static List<double> RowShifts(FrameworkElement view, string listName = "EntryList")
        => CardRows(view, listName)
            .Select(row => row.RenderTransform is TranslateTransform t
                ? t.Y
                : throw new Exception("行格子缺少 TranslateTransform"))
            .ToList();

    /// <summary>按显示顺序取每张卡片的行格子元素</summary>
    private static List<FrameworkElement> CardRows(FrameworkElement view, string listName = "EntryList")
    {
        var result = new List<FrameworkElement>();
        var list = FindField<ItemsControl>(view, listName)
            ?? throw new Exception($"找不到 {listName}");

        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            if (VisualTreeHelper.GetChildrenCount(cp) == 0) continue;
            if (VisualTreeHelper.GetChild(cp, 0) is not FrameworkElement row) continue;

            result.Add(row);
        }

        return result;
    }

    // ---- 13. 日历明细卡片入场 ----

    /// <summary>
    /// 日历页的明细卡片要有和今日页同一套入场，并且同样只在每次进入页面时播一轮。
    /// 两个页面共用 <c>CardListEntrance</c>，所以这里断言的是接上了没有、是不是只播一轮、行顺序对不对。
    /// </summary>
    private static void TestCalendarEntrance()
    {
        DrainSaves();
        CleanupDate(DateTime.Today);

        // 播种 3 条: 只有一条时看不出错开顺序
        var seedVm = new TodayBillViewModel(NewRepository());

        for (int i = 0; i < 3; i++)
        {
            seedVm.AddCommand.Execute(null);
            seedVm.DraftDescription = $"日历动效 {i + 1}";
            seedVm.DraftAmountText = $"-{(i + 1) * 3}";
            seedVm.SaveCommand.Execute(null);
            for (int p = 0; p < 20; p++) Pump();
        }

        WaitUntil(() => seedVm.Entries.Count >= 3);

        if (seedVm.Entries.Count != 3)
            throw new Exception($"播种失败, 只有 {seedVm.Entries.Count} 条; 提示='{seedVm.Notice}'");

        var vm = new CalenderBillViewModel(NewRepository());
        var view = new CalenderBill { DataContext = vm };

        if (view.EntryEntrancePlayCount != 0)
            throw new Exception("日历视图刚建好就播了入场");

        Pump();
        Layout(view);

        WaitUntil(() => vm.Entries.Count >= 3);

        Layout(view);
        for (int i = 0; i < 10; i++) Pump();
        Layout(view);

        if (vm.Entries.Count != 3) throw new Exception($"应有 3 条, 实际 {vm.Entries.Count}");

        int afterFirstBatch = view.EntryEntrancePlayCount;
        Report($"日历首批入场播放张数 = {afterFirstBatch}");

        if (afterFirstBatch != 3)
            throw new Exception($"3 张明细卡片应各播一次入场, 实际 {afterFirstBatch} 次");

        // 与今日页同样的两条地基: 这一批必须完整, 而且按列表顺序
        var rows = CardRows(view);
        Report($"日历行数={rows.Count} 条目数={vm.Entries.Count}");

        if (rows.Count != vm.Entries.Count)
            throw new Exception($"行数与条目数不符: {rows.Count} vs {vm.Entries.Count}");

        for (int i = 0; i < rows.Count; i++)
        {
            if (!ReferenceEquals(rows[i].DataContext, vm.Entries[i]))
                throw new Exception($"第 {i + 1} 行的数据不是列表里的第 {i + 1} 条");
        }

        // 落回静止态: 入场只是过渡, 不能留下偏移或半透明
        if (!WaitForRest(view))
        {
            foreach (var (opacity, shift) in RowStates(view))
                Report($"  日历未落定: opacity={opacity:0.###} shift={shift:0.##}");

            throw new Exception("日历入场没有落回静止态");
        }

        Layout(view);
        foreach (var (opacity, shift) in RowStates(view))
            Report($"  日历静止态: opacity={opacity:0.###} shift={shift:0.##}");

        // 换粒度是内容被换掉而不是入场，明细卡片不该再逐个弹一遍。
        // 那一次走的是容器落定 Settle，只是首屏那一批不再走它了。
        int beforeMode = view.EntryEntrancePlayCount;
        vm.ShowMonthCommand.Execute(null);
        Pump();
        Layout(view);

        Report($"换到月视图后 日历入场张数 = {beforeMode} -> {view.EntryEntrancePlayCount}");

        if (view.EntryEntrancePlayCount != beforeMode)
            throw new Exception("换粒度时重播了明细卡片入场, 整屏会跟着弹");

        // 翻页同理: 只落定, 不重播入场
        int beforeTurn = view.EntryEntrancePlayCount;
        vm.NextCommand.Execute(null);
        Pump();

        Report($"翻页后 日历入场张数 = {view.EntryEntrancePlayCount}（不应变化）");
        if (view.EntryEntrancePlayCount != beforeTurn)
            throw new Exception("翻页时重播了明细卡片入场");

        // 切回这一页(换 ViewModel)要重新播
        int beforeSwitch = view.EntryEntrancePlayCount;
        var second = new CalenderBillViewModel(NewRepository());
        view.DataContext = second;

        Pump();
        Layout(view);
        WaitUntil(() => second.Entries.Count > 0);
        Layout(view);
        for (int i = 0; i < 10; i++) Pump();
        Layout(view);

        Report($"切页面后 日历入场张数 = {beforeSwitch} -> {view.EntryEntrancePlayCount} " +
               $"（当前 {second.Entries.Count} 条）");

        if (view.EntryEntrancePlayCount <= beforeSwitch)
            throw new Exception("换页面之后日历没有重播明细卡片入场");

        DrainSaves();
        CleanupDate(DateTime.Today);
    }

    /// <summary>
    /// 等列表落回静止态（每张卡片都不透明、无偏移）。LAE 按真实经过时间推进，
    /// 而本环境渲染帧稀疏，单次长睡眠可能被来得晚、间隔又短的帧切碎，动画只前进几十毫秒。
    /// 所以改成多轮小睡，每轮泵一帧看一次。
    /// </summary>
    private static bool WaitForRest(FrameworkElement view, string listName = "EntryList")
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(400);
            Pump();

            if (RowStates(view, listName).All(s =>
                    Math.Abs(s.Opacity - 1) <= 0.001 && Math.Abs(s.Shift) <= 0.5))
                return true;
        }

        return false;
    }

    // ---- 14. 日期选择器跟着主题走 ----

    /// <summary>
    /// 记账面板里的日期框必须用主题底色，不能是系统的白。DatePicker 的默认模板把内层
    /// DatePickerTextBox 的底色写死成白色，而本项目的前景是接近白的 ColorTextTitle，会白底白字。
    /// 这里不只看模板部件齐不齐，还把它渲染出来取一个像素与主题卡片底色比对。
    /// </summary>
    private static void TestDatePickerTheme()
    {
        var style = Application.Current!.TryFindResource("DatePickerThemed") as Style
            ?? throw new Exception("资源里没有 DatePickerThemed");

        var picker = new DatePicker
        {
            Style = style,
            SelectedDate = new DateTime(2026, 3, 15)
        };

        const int width = 240;
        const int height = 40;

        // 先排一次版让模板应用上(FindName 需要模板名字域已经建好)
        picker.Measure(new Size(width, height));
        picker.Arrange(new Rect(0, 0, width, height));
        picker.UpdateLayout();

        if (picker.Template is null) throw new Exception("DatePicker 没有模板");

        // 模板部件是控件契约, 缺一个就是静默失效
        foreach (var name in new[] { "PART_TextBox", "PART_Button", "PART_Popup", "PART_Calendar" })
        {
            if (picker.Template.FindName(name, picker) is null)
                throw new Exception($"DatePicker 模板缺少 {name}（控件内部会静默失效）");
        }

        // 内层输入框真的接上了: 文本会跟着 SelectedDate 走
        Report($"日期框文本 = '{picker.Text}'");

        if (!picker.Text.Contains("2026") || !picker.Text.Contains("15"))
            throw new Exception($"日期框文本没有跟着 SelectedDate 走: '{picker.Text}'");

        // 底色: 取一个既避开文字、也避开右侧按钮的像素
        var expected = ((SolidColorBrush)Application.Current.FindResource("ColorCardBackground")).Color;
        var actual = PixelAt(picker, width, height, width - 60, height / 2);

        Report($"日期框底色 = #{actual.R:X2}{actual.G:X2}{actual.B:X2} " +
               $"（主题卡片底色 #{expected.R:X2}{expected.G:X2}{expected.B:X2}）");

        if (Math.Abs(actual.R - expected.R) > 2
            || Math.Abs(actual.G - expected.G) > 2
            || Math.Abs(actual.B - expected.B) > 2)
            throw new Exception(
                $"日期框底色不是主题卡片底色: 实际 #{actual.R:X2}{actual.G:X2}{actual.B:X2}, " +
                $"应为 #{expected.R:X2}{expected.G:X2}{expected.B:X2}（默认模板会写成白色）");
    }

    /// <summary>渲染一个元素并取某个像素的颜色, 用于断言"底色对不对"这类事</summary>
    private static Color PixelAt(FrameworkElement element, int width, int height, int x, int y)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(element);

        var pixel = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);

        // Pbgra32: 不透明时通道就是原色
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static int Check(string name, Action body)
    {
        Console.WriteLine($"[测试] {name}");
        try
        {
            body();
            Console.WriteLine("  结果: OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  结果: 失败 -> {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.WriteLine();
        }
    }

    private static void Report(string message) => Console.WriteLine("  " + message);
}
