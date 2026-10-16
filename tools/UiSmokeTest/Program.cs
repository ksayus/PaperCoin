using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
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

    /// <summary>截图模式：<c>--screenshots &lt;目录&gt;</c>。用来把四个页面与侧边栏指示器渲染成 PNG 肉眼核对 —— 布局塌陷、错位、图标缺失这类问题断言看不出来。</summary>
    private const string ScreenshotArgument = "--screenshots";

    /// <summary>跨进程探针模式的命令行开关，见 <see cref="Main"/></summary>
    private const string ColorProbeArgument = "--color-probe";

    /// <summary>
    /// 探针样本。刻意包含中文、大小写、带空格与空值：
    /// 哈希实现换一种写法时，只要其中一项变了，两次进程的输出就会不同。
    /// </summary>
    private static readonly string?[] ColorProbeCategories =
        [null, "", "餐饮", "交通", "购物", "居住", "娱乐", "医疗", "Food", "FOOD", " 餐饮 "];

    [STAThread]
    private static int Main()
    {
        // 跨进程探针模式：只打印"分类 → 颜色键"的映射就退出。
        // 第 20 项要靠它比较两次独立进程的输出 —— 颜色映射如果用了
        // string.GetHashCode，进程内两次调用一致、跨进程却不一样，
        // 同一个进程里无论怎么断言都发现不了。
        if (Environment.GetCommandLineArgs().Contains(ColorProbeArgument))
        {
            foreach (var name in ColorProbeCategories)
                Console.WriteLine($"{name}={PaperCoin.Core.CategoryPalette.ThemeKeyOf(name)}");

            return 0;
        }


        // 同一时刻只允许一个冒烟进程。测试之间共用 %APPDATA%\PaperCoin\Bills 与同一份
        // 编译产物，两个进程并发会互相删对方的数据、还会锁住 dll 让构建失败 ——
        // 后果不是"跑不起来"，而是**给出看起来像功能坏了的假失败**（实测出现过
        // "应有 3 条实际 8 条"这种）。所以宁可直接拒绝启动。
        using var exclusive = new Mutex(initiallyOwned: true, @"Local\PaperCoin-UiSmokeTest", out bool isOnlyOne);

        if (!isOnlyOne)
        {
            Console.WriteLine("已有另一个冒烟测试在跑（它与我共用同一份账单目录与编译产物），本次拒绝启动。");
            Console.WriteLine("并发跑只会得到互相污染的结果，请等它结束，或用 --screenshots 之外的模式串行执行。");
            return 2;
        }

        var app = new Application
        {
            // 默认 ShutdownMode 会在最后一个窗口关闭时退出进程。侧边栏测试要
            // Show/Close 一个真实 MainWindow，一关就会把测试宿主带停。
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        BootstrapResources(app);
        BootstrapServices();

        // 截图模式：渲染真实窗口存成 PNG 供肉眼核对，不跑断言。
        var argv = Environment.GetCommandLineArgs();
        int shotIndex = Array.IndexOf(argv, ScreenshotArgument);
        if (shotIndex >= 0)
        {
            var target = shotIndex + 1 < argv.Length
                ? argv[shotIndex + 1]
                : Path.Combine(Path.GetTempPath(), "papercoin-shots");

            ResetBillsData();
            CaptureScreenshots(target);
            return 0;
        }

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
            failures += Check("15. 主题切换的颜色过渡", TestThemeTransition);
            failures += Check("16. 自有程序集带产品版本", TestAssemblyVersions);
            failures += Check("17. 安装位置记录", TestInstallLocation);
            failures += Check("18. View 引用的图标资源键都已定义", TestIconReferences);
            failures += Check("19. 快捷方式图标修正", TestShortcutRepair);
            failures += Check("20. 分类色板：稳定、可解析", TestCategoryPalette);
            failures += Check("21. 仓库按分类汇总支出", TestCategoryExpenseAggregation);
            failures += Check("22. 统计口径：范围 / 排行 / 趋势 / 分类明细 / CSV", TestStatisticsAggregation);
            failures += Check("23. 圆环几何与占比一致", TestDonutGeometry);
            failures += Check("24. 统计页接入导航并播放展开动效", TestStatisticsPage);
            failures += Check("25. 统计 CSV 带 BOM", TestStatisticsCsv);
            failures += Check("26. 每次进页面都重播入场动画", TestEntranceReplaysOnEveryEntry);
            failures += Check("27. 分类明细从真实入口走通", TestCategoryDetailEndToEnd);
            failures += Check("28. 液态玻璃的背部模糊接对了", TestBackdropBlur);
            failures += Check("29. 三个入口都能看分类明细", TestCategoryEntryPoints);
            failures += Check("30. 每个 XAML 里同一名字范围不重名", TestNoDuplicateNamesInScope);
            failures += Check("31. 日历页能改和删账单", TestCalendarEditAndDelete);
        });

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "== 全部通过 ==" : $"== {failures} 项失败 ==");
        return failures;
    }

    // ---- 截图模式 ----

    /// <summary>
    /// 把四个页面与侧边栏指示器渲染成 PNG。
    /// <para>
    /// 断言能挡住"控件没了"，挡不住"控件在但位置不对、颜色不对、图标画不出来"。
    /// 这个模式专门补这一段：渲染的是真实窗口、真实主题、真实字体。
    /// </para>
    /// </summary>
    private static void CaptureScreenshots(string directory)
    {
        Directory.CreateDirectory(directory);

        // 造一份有多分类、跨月的数据，图表才有东西可画
        var repo = App.Services.GetRequiredService<BillRepository>();
        var expected = SeedStatisticsData(repo, DateOnly.FromDateTime(DateTime.Today));
        Report($"造数完成：本月支出 {expected.MonthExpense}，本年餐饮 {expected.YearFood}");

        var window = new MainWindow
        {
            Width = 480,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
        };

        window.Show();
        Pump();

        // 三种风格各截一轮：主题只有肉眼看得出来，断言管不了"材质对不对味"
        foreach (var (style, palette) in new[]
                 {
                     ("Classic", "Dark"),
                     ("Windows", "DarkWindows"),
                     ("Liquid", "DarkLiquid")
                 })
        {
            ApplyPaletteForShot(palette);
            Pump();

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var page in new[] { "TodayBill", "CalenderBill", "Statistics", "Settings" })
                {
                    window.NavigateForTest(page);
                    Pump();

                    SettleIndicator(window);
                    Pump();
                    SettleCharts(window);

                    Shot(window, Path.Combine(directory, $"{style}-{page}.png"), $"{style}/{page}");
                }

                // 主题与风格那两行在设置页顶部，单独截一张看清楚
                window.NavigateForTest("Settings");
                Pump();
                Shot(window, Path.Combine(directory, $"{style}-Settings.png"), $"{style}/Settings");
            });

            // 探针：读出主内容区与卡片底色的实际合成结果，确认这套调色板的明暗与预期一致。
            // 只看画刷的 ARGB 不够 —— 半透明画刷要与窗口底色合成后才知道实际观感。
            Application.Current.Dispatcher.Invoke(() =>
            {
                window.NavigateForTest("Statistics");
                Pump();

                int w = (int)Math.Ceiling(window.ActualWidth);
                int h = (int)Math.Ceiling(window.ActualHeight);

                // 取左侧导航栏（无卡片覆盖）与卡片内部两处的实际像素
                var nav = PixelAt(window, w, h, 25, h / 2);
                var card = PixelAt(window, w, h, w / 2, 120);

                Report($"    {style}: 侧边栏={Hex(nav)} 卡片={Hex(card)}");
            });

            Report($"风格 {style}（调色板 {palette}）截图完成");
        }

        // 恢复经典，后面的截图不受影响
        ApplyPaletteForShot("Dark");
        Pump();

        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var page in new[] { "TodayBill", "CalenderBill", "Statistics", "Settings" })
            {
                window.NavigateForTest(page);
                Pump();

                // 侧边栏指示器走的是布局驱动的落位，等它稳定
                SettleIndicator(window);
                Pump();

                // 截图宿主里 LAE 时钟不推进，走动画的话图表会停在起点（环的描边宽度是 0）。
                // 直接落到展开完成的状态，截出来的才是静态外观。
                SettleCharts(window);

                Shot(window, Path.Combine(directory, $"1-{page}.png"), $"页面 {page}");
                Report($"  {page}: 指示器 Y={window.IndicatorY:0.#}");

                // 统计页额外截一张"选中某个分类"的，看分类明细区块的实际内容
                if (window.FindPageForTest(page) is Statistics stats)
                {
                    stats.ScrollToCategoryDetailForTest();
                    Pump();
                    SettleCharts(window);

                    Shot(window, Path.Combine(directory, "1-Statistics-分类明细.png"), "分类明细");
                    Report($"  分类明细: {stats.CategoryDetailSnapshotForTest}");
                }
            }

            // 指示器在四个导航项上的落位，逐个截侧边栏
            foreach (var page in new[] { "TodayBill", "CalenderBill", "Statistics", "Settings" })
            {
                window.NavButtonForTest(page).RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Pump();
                SettleIndicator(window);
                Pump();

                Shot(window, Path.Combine(directory, $"2-指示器-{page}.png"), $"指示器 {page}");

                double target = window.IndicatorTargetFor(page);
                double actual = window.IndicatorY;

                Report($"  指示器 {page}: Y={actual:0.#} 目标={target:0.#} " +
                       $"偏差={Math.Abs(actual - target):0.#}");

                if (Math.Abs(actual - target) > 1.5)
                    throw new Exception(
                        $"指示器没有落在 {page} 项上: Y={actual:0.#}, 应为 {target:0.#}");
            }

            window.Close();
        });

        Report($"截图目录: {directory}");
    }

    /// <summary>
    /// 截图模式：把当前主题换成指定调色板。
    /// 走 ThemeRenderer 的原地改写（与运行时切主题同一条路径），而不是替换字典实例 ——
    /// 替换实例不会让已经解析过的 DynamicResource 重新解析。
    /// </summary>
    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static void ApplyPaletteForShot(string palette)
    {
        // 直接把目标画刷写进颜色字典，不经过 ThemeRenderer.Apply 的过渡。
        // Apply 会挂 300ms 的 ColorAnimation，而截图宿主里时钟不推进 ——
        // 截到的是过渡的起点（暗色底 + 深色文字），看着像主题没生效。
        var merged = Application.Current.Resources.MergedDictionaries;
        var index = PaperCoin.ThemeRenderer.IndexOfColorDictionary(merged);
        if (index < 0) throw new Exception("找不到颜色字典");

        var target = merged[index];
        var incoming = PaperCoin.ThemeRenderer.LoadTheme(palette);

        foreach (var key in incoming.Keys) target[key] = incoming[key];
        foreach (var stale in target.Keys.Cast<object>().Where(k => !incoming.Contains(k)).ToList())
            target.Remove(stale);
    }

    /// <summary>
    /// 让统计页的图表直接落到展开完成的状态。
    /// 截图宿主没有渲染循环，LAE 的时钟不推进：不这么做，圆环与柱状图都会停在动画起点，
    /// 截出来的图看不出任何问题，等于白截。
    /// </summary>
    private static void SettleCharts(MainWindow window)
    {
        if (window.FindPageForTest("Statistics") is not Statistics page) return;

        page.SettleChartsForTest();
        Pump();
        Report($"    {page.DonutSnapshot}");
    }

    /// <summary>
    /// 把窗口当前内容渲染成 PNG。
    /// <para>
    /// 渲染前显式走一遍 <c>Measure</c> + <c>Arrange</c>：只调 <c>UpdateLayout</c> 时，
    /// 若此刻没有新的布局趟次，RenderSize 会是 0，截出来是一张全透明的空图 ——
    /// 而且文件照样生成，看上去像"界面本来就是空的"。
    /// </para>
    /// </summary>
    private static void Shot(FrameworkElement element, string path, string label)
    {
        int width = (int)Math.Ceiling(element.Width);
        int height = (int)Math.Ceiling(element.Height);

        if (width <= 0) width = (int)Math.Ceiling(element.ActualWidth);
        if (height <= 0) height = (int)Math.Ceiling(element.ActualHeight);

        if (width <= 0 || height <= 0)
        {
            Report($"  {label}: 尺寸为 0，跳过");
            return;
        }

        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        // 直接渲染元素本身，**不要**套一层 VisualBrush：
        // VisualBrush 只画目标 OnRender 产出的内容，用 AddVisualChild 挂上去的子可视元素
        // （DonutChart 的弧线就是这种）不会被画进来 —— 截出来的圆环永远是空的。
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);

        Report($"  {label}: {width}×{height} -> {Path.GetFileName(path)}");
    }

    // ---- 1. 资源 ----

    /// <summary>
    /// 六本调色板字典的 key 必须完全一致。
    /// <para>
    /// 主题切换按 key 就地替换条目（见 ThemeRenderer）：某个风格少一个 key，
    /// 切过去时 <c>DynamicResource</c> 找不到就静默回退，表现是"某个控件颜色不对"
    /// 且没有任何报错。风格有 3 种、明暗 2 种，组合出 6 本，全都要对齐。
    /// </para>
    /// </summary>
    private static void TestResourceKeys()
    {
        // 调色板名与 ThemeManager.Compose 的规则一致：经典不带后缀
        string[] palettes =
            ["Dark", "Bright", "DarkWindows", "BrightWindows", "DarkLiquid", "BrightLiquid"];

        var maps = new Dictionary<string, ResourceDictionary>();
        foreach (var name in palettes)
            maps[name] = LoadDict($"/Themes/{name}.xaml");

        var reference = maps["Dark"].Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();
        Report($"调色板基准（Dark）{reference.Count} 个 key");

        foreach (var name in palettes)
        {
            var keys = maps[name].Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();

            if (keys.Count != reference.Count)
                Report($"  {name}: {keys.Count} 个 key");

            var missing = reference.Except(keys).ToList();
            var extra = keys.Except(reference).ToList();

            if (missing.Count > 0)
                throw new Exception($"{name} 缺少 key: " + string.Join(", ", missing));

            if (extra.Count > 0)
                throw new Exception($"{name} 多出 key: " + string.Join(", ", extra));
        }

        Report($"六个调色板 key 全部对齐（{string.Join(", ", palettes)}）");

        foreach (var key in new[]
                 {
                     "ColorCardBackground", "ColorCardBorder", "ColorCardActionBackground",
                     "ColorTextTitle", "ColorTextSecondary", "ColorPositive", "ColorNegative",
                     "ColorShadow", "ColorMainContentArea", "ColorAddNewBillBtn",
                     "ColorSideBarBtnActive", "ColorSideBarBtnOver", "ColorTitleTop",
                     "ColorSideBar", "ColorWindowShadow"
                 })
        {
            if (!reference.Contains(key)) throw new Exception($"Dark 缺少 key: {key}");
        }

        // 液态玻璃的玻璃感全靠半透明，全是 A=FF 就说明这套调色板没做出材质差异
        foreach (var name in new[] { "DarkLiquid", "BrightLiquid" })
        {
            var translucent = maps[name].Keys.Cast<object>()
                .Select(k => maps[name][k])
                .OfType<SolidColorBrush>()
                .Count(b => b.Color.A < 0xFF);

            Report($"  {name}: {translucent} 个半透明画刷");

            if (translucent < 5)
                throw new Exception($"{name} 只有 {translucent} 个半透明画刷，做不出玻璃材质");
        }

        // 每套调色板都要能读出字。半透明面板会与背板合成，光看画刷的 ARGB 判断不了可读性，
        // 必须按合成后的实际颜色算对比度 —— 这一条挡的正是"玻璃面板太透，
        // 暗色玻璃在浅底上糊成一片"这种只有肉眼才发现的问题。
        //
        // 玻璃风格走 DWM 系统背板时 ColorGlassBackdrop 是 Transparent，实际底色由系统按
        // 窗口背后的画面算出来，可能是任意明度。所以液态玻璃要同时按**最坏情形**验：
        // 底色全黑与底色中灰（模糊过的浅色桌面）两种，都得读得出来。
        foreach (var name in palettes)
        {
            var backdropBrush = maps[name]["ColorGlassBackdrop"] as SolidColorBrush
                ?? throw new Exception($"{name} 缺少 ColorGlassBackdrop");

            bool isGlass = name.EndsWith("Liquid", StringComparison.Ordinal);

            // 玻璃：背板是 Transparent，实际底色 = 系统背板的模糊结果 + DWM 色调（见 BackdropBlur）。
            // 最坏情形是窗口压在浅色内容上、模糊结果很亮，所以按"纯白模糊 + 该风格的色调"验。
            // 非玻璃：背板就是实际底色。
            // 两个极端都验：模糊结果几乎全黑（窗口压在深色内容上）、几乎全白（压在浅色内容上）
            Color[] bases = isGlass
                ? [CompositeOn(ScrimOf(maps[name]), Colors.Black),
                   CompositeOn(ScrimOf(maps[name]), Colors.White)]
                : [backdropBrush.Color];

            foreach (var backdrop in bases)
            {
                var surface = Composite(maps[name]["ColorMainContentArea"] as SolidColorBrush, backdrop);
                var card = Composite(maps[name]["ColorCardBackground"] as SolidColorBrush, surface);
                var title = (maps[name]["ColorTextTitle"] as SolidColorBrush)?.Color ?? Colors.Black;
                var secondary = (maps[name]["ColorTextSecondary"] as SolidColorBrush)?.Color ?? Colors.Black;

                double cardLum = Luminance(card);
                double titleRatio = Contrast(CompositeOn(title, card), card);
                double secondaryRatio = Contrast(CompositeOn(secondary, card), card);

                string label = isGlass ? $"{name}/{Hex(backdrop)}" : name;

                Report($"  {label,-26} 卡片合成={Hex(card)} 亮度={cardLum:0.00} " +
                       $"正文对比={titleRatio:0.0} 次要对比={secondaryRatio:0.0}");

                if (!isGlass)
                {
                    // 暗色风格合成后要明显偏暗，亮色风格要明显偏亮，否则主题名不副实
                    if (name.StartsWith("Dark", StringComparison.Ordinal) && cardLum > 0.45)
                        throw new Exception($"{name} 的卡片合成亮度 {cardLum:0.00} 偏高，暗色风格暗不下来");

                    if (name.StartsWith("Bright", StringComparison.Ordinal) && cardLum < 0.30)
                        throw new Exception($"{name} 的卡片合成亮度 {cardLum:0.00} 偏低，亮色风格亮不起来");
                }

                // 正文对比度。玻璃风格的门槛是 4.0 而不是 WCAG AA 的 4.5，这是材质本身的限制，
                // 不是懒得调：模糊结果可能是纯白，而"玻璃"又必须让模糊透出来 ——
                // 要让白字在纯白模糊上稳过 4.5:1，面板 Alpha 得高到几乎不透明，玻璃就不成立了。
                // 取 4.0：仍是清晰可读的正文，代价写在 README 里。其余风格照旧 4.5。
                double titleFloor = isGlass ? 4.0 : 4.5;

                if (titleRatio < titleFloor)
                    throw new Exception(
                        $"{label} 正文对比度只有 {titleRatio:0.0}:1，低于 {titleFloor}:1（底色 {Hex(backdrop)}）");

                if (secondaryRatio < 3.0)
                    throw new Exception(
                        $"{label} 次要文字对比度只有 {secondaryRatio:0.0}:1，低于 3:1（底色 {Hex(backdrop)}）");
            }
        }

        // 液态玻璃的背板必须是 Transparent：不透明的话 DWM 系统背板会被它盖住，
        // 真背部模糊就白设了，而且不会有任何报错。
        foreach (var name in new[] { "DarkLiquid", "BrightLiquid" })
        {
            var backdrop = (maps[name]["ColorGlassBackdrop"] as SolidColorBrush)?.Color ?? Colors.Black;

            if (backdrop.A != 0)
                throw new Exception(
                    $"{name} 的 ColorGlassBackdrop 不是全透明（A={backdrop.A}），会盖住 DWM 背板");
        }
    }

    /// <summary>
    /// 玻璃层之下、模糊层之上的明度层（ColorGlassScrim），从调色板里读出来用。
    /// 模糊结果可能是任意明度，所以对比度要按"scrim 压过纯黑模糊"与"压过纯白模糊"两个极端验。
    /// </summary>
    private static Color ScrimOf(ResourceDictionary palette)
        => (palette["ColorGlassScrim"] as SolidColorBrush)?.Color
           ?? throw new Exception("调色板缺少 ColorGlassScrim");

    /// <summary>把可能半透明的画刷按 alpha 合成到一个不透明底色上</summary>
    private static Color Composite(SolidColorBrush? brush, Color backdrop)
        => brush is null ? backdrop : CompositeOn(brush.Color, backdrop);

    /// <summary>src 以自身 alpha 叠加在 dst 上</summary>
    private static Color CompositeOn(Color src, Color dst)
    {
        double a = src.A / 255.0;

        return Color.FromRgb(
            (byte)Math.Round(src.R * a + dst.R * (1 - a)),
            (byte)Math.Round(src.G * a + dst.G * (1 - a)),
            (byte)Math.Round(src.B * a + dst.B * (1 - a)));
    }

    /// <summary>WCAG 相对亮度</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG 对比度</summary>
    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
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

    // ---- 18. View 引用的图标键 ----

    /// <summary>
    /// View 里的图标是 DynamicResource 引用：键写错不报错，只渲染成空白，
    /// 和缺字形一样安静。第 2 项只能验证表里有的码位，管不到引用侧，
    /// 所以这里把 View 里出现的每个图标键与 AppIcons 的定义对一遍。
    /// 字体族键 <c>Iconfont</c> 也在资源里，但不是图标，用首字母大写把它排除。
    /// </summary>
    private static void TestIconReferences()
    {
        var icons = LoadDict("/Icon/AppIcons.xaml");
        var defined = icons.Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();

        var root = RepoRoot();
        if (root is null) throw new Exception("找不到仓库根");

        var reference = new Regex(@"\{(?:Dynamic|Static)Resource\s+(Icon[A-Z][A-Za-z0-9_]*)\s*\}");

        var found = new List<(string Key, string File, int Line)>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, "PaperCoin"), "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in reference.Matches(text))
            {
                var line = text.Take(m.Index).Count(c => c == '\n') + 1;
                found.Add((m.Groups[1].Value, Path.GetFileName(file), line));
            }
        }

        Report($"View 里引用图标 {found.Count} 处, AppIcons 定义 {defined.Count} 个");

        var missing = found.Where(f => !defined.Contains(f.Key)).ToList();
        if (missing.Count > 0)
        {
            throw new Exception(
                "引用未定义的图标键: " +
                string.Join(", ", missing.Select(m => $"{m.File}:{m.Line} {m.Key}")));
        }

        // View 里确实用到了图标才算这条防线成立，别因为正则失效而永远通过。
        if (found.Count == 0) throw new Exception("没有扫描到任何图标引用, 正则可能已失效");
    }

    // ---- 19. 快捷方式图标修正 ----

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkTestObject
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkTest
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFileTest
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    private static string ReadShortcutIcon(string lnkPath)
    {
        var link = (IShellLinkTest)new ShellLinkTestObject();
        ((IPersistFileTest)link).Load(lnkPath, 0);
        var buffer = new System.Text.StringBuilder(260);
        link.GetIconLocation(buffer, buffer.Capacity, out _);
        return buffer.ToString();
    }

    private static string ReadShortcutTarget(string lnkPath)
    {
        var link = (IShellLinkTest)new ShellLinkTestObject();
        ((IPersistFileTest)link).Load(lnkPath, 0);
        var buffer = new System.Text.StringBuilder(260);
        link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
        return buffer.ToString();
    }

    /// <summary>
    /// 安装包装出来的快捷方式图标是坏的（MSI Icon 表放的是 .ico 字节却命名成 .exe），
    /// 由 <see cref="PaperCoin.Service.ShortcutRepair"/> 在启动时补正。
    /// 这里造一个图标指向别处的快捷方式，验证它真的被改对，并且第二次调用不再写盘。
    /// </summary>
    private static void TestShortcutRepair()
    {
        // 修正逻辑认的图标：优先程序目录里那份，没有就从内嵌资源落盘。
        var iconPath = PaperCoin.Service.ShortcutRepair.EnsureIconFile();
        if (!File.Exists(iconPath))
            throw new Exception($"图标文件没落盘: {iconPath}");
        Report($"使用的图标文件 = {iconPath} ({new FileInfo(iconPath).Length} 字节)");

        var dir = Path.Combine(Path.GetTempPath(), "PaperCoinShortcutTest");
        Directory.CreateDirectory(dir);
        var lnk = Path.Combine(dir, "PaperCoin.lnk");
        if (File.Exists(lnk)) File.Delete(lnk);

        try
        {
            // 造一个图标指向别处的快捷方式，模拟安装包留下的坏状态。
            // 目标特意设成"不是本程序"的值：修完之后目标必须变成本程序，
            // 绝不能变成图标文件的路径（实测踩过，那样快捷方式点不开程序）。
            var bogus = Path.Combine(dir, "bogus.exe");
            var originalTarget = Path.Combine(dir, "PaperCoinTarget.exe");
            var link = (IShellLinkTest)new ShellLinkTestObject();
            link.SetPath(originalTarget);
            link.SetIconLocation(bogus, 0);
            ((IPersistFileTest)link).Save(lnk, true);

            Report($"修正前 IconLocation = {ReadShortcutIcon(lnk)}");
            Report($"修正前 Target       = {ReadShortcutTarget(lnk)}");
            if (ReadShortcutIcon(lnk) != bogus)
                throw new Exception("测试前置失败：快捷方式图标没被设成伪造值");

            var changed = PaperCoin.Service.ShortcutRepair.Repair(lnk);
            var after = ReadShortcutIcon(lnk);
            var targetAfter = ReadShortcutTarget(lnk);
            var expectedTarget = Path.Combine(AppContext.BaseDirectory, "PaperCoin.exe");
            Report($"Repair 返回 = {changed}");
            Report($"修正后 IconLocation = {after}");
            Report($"修正后 Target       = {targetAfter}");

            if (!changed) throw new Exception("图标是错的，Repair 却没改写");
            if (!string.Equals(after, iconPath, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"修正后的图标不对: {after} != {iconPath}");
            if (!string.Equals(targetAfter, expectedTarget, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"修正后的目标不对: {targetAfter} != {expectedTarget}");

            // 内嵌资源得是同一个图标，否则打包时漏更新就看不出来。
            var embedded = typeof(PaperCoin.Service.ShortcutRepair).Assembly
                .GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("papercoin-icon.ico", StringComparison.OrdinalIgnoreCase));
            if (embedded is null) throw new Exception("程序集里没有内嵌图标资源");
            Report($"内嵌图标资源 = {embedded}");
            using (var s = typeof(PaperCoin.Service.ShortcutRepair).Assembly.GetManifestResourceStream(embedded)!)
            {
                if (s.Length != new FileInfo(iconPath).Length)
                    throw new Exception($"内嵌图标大小 {s.Length} 与落盘文件不一致");
            }

            // 幂等：图标已经对了就不该再写盘
            if (PaperCoin.Service.ShortcutRepair.Repair(lnk))
                throw new Exception("图标已经正确，Repair 却又写了一次（不幂等）");
            Report("第二次调用未写盘（幂等）");

            // 文件不存在时不该抛异常
            if (PaperCoin.Service.ShortcutRepair.Repair(Path.Combine(dir, "不存在.lnk")))
                throw new Exception("对不存在的快捷方式返回了 true");
            Report("不存在的快捷方式返回 false，未抛异常");

            // 顺带只读核对真实的桌面快捷方式：它是开发目录加载的，AppContext.BaseDirectory
            // 不是它的程序目录，所以这里绝不能调 Repair（会把人家的桌面改成测试宿主的路径）。
            var desktopLnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "PaperCoin.lnk");
            if (File.Exists(desktopLnk))
            {
                Report($"桌面快捷方式图标(只读) = {ReadShortcutIcon(desktopLnk)}");
            }
            else
            {
                Report("本机没有桌面快捷方式（正常，未安装）");
            }
        }
        finally
        {
            if (File.Exists(lnk)) File.Delete(lnk);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
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

        // 每一项都要落在**它自己**那个按钮上。
        // 只断言"指示器动了"是不够的：新增导航项漏进 RepositionIndicator 的 switch 默认分支时，
        // 点统计项会让指示器停在设置项上，而"动过了"依然成立 —— 这个 bug 真的发生过。
        foreach (var page in new[] { "TodayBill", "CalenderBill", "Statistics", "Settings" })
        {
            window.NavButtonForTest(page).RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump();
            SettleIndicator(window);

            double target = window.IndicatorTargetFor(page);
            double actual = window.IndicatorY;

            Report($"指示器 {page,-13} Y={actual,7:0.#}  目标={target,7:0.#}  偏差={Math.Abs(actual - target):0.#}");

            if (double.IsNaN(target))
                throw new Exception($"{page} 的目标位置算不出来（按钮或侧边栏高度为 0）");

            if (Math.Abs(actual - target) > 1.5)
                throw new Exception(
                    $"指示器没有落在 {page} 项上: Y={actual:0.#}, 应为 {target:0.#}");
        }

        // 四项的目标必须互不相同，否则上面的逐项断言会因为"重合"而互相掩盖
        var targets = new[] { "TodayBill", "CalenderBill", "Statistics", "Settings" }
            .Select(window.IndicatorTargetFor)
            .ToList();

        if (targets.Distinct().Count() != targets.Count)
            throw new Exception($"四个导航项的目标位置有重合: {string.Join(", ", targets.Select(t => t.ToString("0.#")))}");

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
            vm.Editor.DraftDescription = $"测试条目 {i}";
            vm.Editor.DraftAmountText = $"-{i + 1}.5";
            vm.Editor.SaveCommand.Execute(null);
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

        var overlay = FindField<BillEditorOverlay>(view, "Editor")
            ?? throw new Exception("找不到编辑面板 BillEditorOverlay");

        if (overlay.Visibility != Visibility.Visible) throw new Exception("编辑面板没有显示");

        var panel = FindField<Grid>(overlay, "EditorPanel")
            ?? throw new Exception("找不到编辑面板的 EditorPanel");

        var panelBounds = BoundsIn(view, panel);
        Report($"编辑面板 = {panelBounds.X:0.#},{panelBounds.Y:0.#} {panelBounds.Width:0.#}x{panelBounds.Height:0.#}");
        AssertInside(view, panelBounds, "编辑面板");

        // 遮罩必须铺满整个内容区，内缩的话四周会露出一圈没压暗的边。
        var scrim = FindField<Border>(overlay, "EditorScrim")
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
            var box = FindField<TextBox>(overlay, name)!;
            if (box.ActualWidth < 10 || box.ActualHeight < 10)
                throw new Exception($"{name} 尺寸异常: {box.ActualWidth:0.#}x{box.ActualHeight:0.#}");
        }

        // 分类框是可编辑下拉框。IsEditable 依赖模板里的 PART_EditableTextBox，少了这个具名部件
        // 会静默退化成只读下拉，所以只断言 IsEditable 抓不住这种情况，必须查模板部件。
        var categoryBox = FindField<ComboBox>(overlay, "CategoryBox")
            ?? throw new Exception("找不到分类下拉框");

        if (!categoryBox.IsEditable)
            throw new Exception("分类下拉框不可编辑");

        if (categoryBox.Template.FindName("PART_EditableTextBox", categoryBox) is not TextBox)
            throw new Exception("分类下拉框模板缺少 PART_EditableTextBox（IsEditable 会静默失效）");

        Report($"分类框 可编辑={categoryBox.IsEditable} 候选数={categoryBox.Items.Count}");

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
        if (!vm.Editor.HasError)
            throw new Exception("空草稿应当判为无效（否则保存按钮会亮着可用）");

        if (!string.IsNullOrEmpty(vm.Editor.ErrorMessage))
            throw new Exception($"刚打开面板就显示错误提示: {vm.Editor.ErrorMessage}");

        if (vm.Editor.SaveCommand.CanExecute(null))
            throw new Exception("空草稿时保存按钮应当不可用");

        // 用户开始输入后才给提示
        vm.Editor.DraftDescription = "有描述";
        Report($"只填描述时 提示='{vm.Editor.ErrorMessage}'");

        vm.Editor.DraftDescription = "冒烟";
        vm.Editor.DraftAmountText = "-1";
        Report($"填齐后 提示='{vm.Editor.ErrorMessage}' HasError={vm.Editor.HasError}");

        if (vm.Editor.HasError) throw new Exception("合法草稿时不应判为有错");
        if (!vm.Editor.SaveCommand.CanExecute(null)) throw new Exception("合法草稿时保存应可用");

        vm.Editor.SaveCommand.Execute(null);

        WaitUntil(() => vm.Entries.Count > 0);

        Report($"条目={vm.Entries.Count} Reset={resets} 提示='{vm.Notice}'");

        if (vm.Entries.Count != 1) throw new Exception($"保存后应有 1 条, 实际 {vm.Entries.Count}");
        if (resets > 0) throw new Exception($"同步过程产生了 {resets} 次 Reset（导致整表闪烁）");

        // 记一笔到历史日期：本页是今日账单，这条不该出现在列表里。
        int todayCountBefore = vm.Entries.Count;

        vm.AddCommand.Execute(null);
        vm.Editor.DraftDescription = "历史";
        vm.Editor.DraftAmountText = "-2";
        vm.Editor.DraftDate = DateTime.Today.AddDays(-10);
        vm.Editor.SaveCommand.Execute(null);

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

        // 回归：往同一天再记一笔，不能覆盖这一天已有的记录。写入是按日期分组整日覆盖的，
        // 一旦把“本次提交涉及哪几天”当成“这几天就只剩这些内容”，目标日期的旧记录就会被抹掉。
        var pastDate = DateTime.Today.AddDays(-10);
        int pastBefore = CountDayEntries(pastDate);

        if (pastBefore != 1)
            throw new Exception($"测试前提不成立: {pastDate:yyyy-MM-dd} 应有 1 条, 实际 {pastBefore}");

        vm.AddCommand.Execute(null);
        vm.Editor.DraftDescription = "历史二";
        vm.Editor.DraftAmountText = "-3";
        vm.Editor.DraftDate = pastDate;
        vm.Editor.SaveCommand.Execute(null);

        WaitUntil(() => CountDayEntries(pastDate) == pastBefore + 1, rounds: 100);

        int pastAfter = CountDayEntries(pastDate);
        Report($"同日再记一笔后 {pastDate:yyyy-MM-dd} 的记录数 = {pastAfter}（原 {pastBefore}）");

        if (pastAfter != pastBefore + 1)
            throw new Exception(
                $"往同一天记第二笔覆盖了既有记录: {pastDate:yyyy-MM-dd} 原有 {pastBefore} 条, 现在 {pastAfter} 条");

        // 分类候选来自账单里用过的分类：直接输入一个新分类并保存后它应当出现在候选里；
        // 再用一次同一个分类（带首尾空格）不该造出第二条。
        int todayBeforeCategory = CountDayEntries(DateTime.Today);

        vm.AddCommand.Execute(null);
        vm.Editor.DraftDescription = "分类样张一";
        vm.Editor.DraftAmountText = "-5";
        vm.Editor.DraftCategory = "餐饮";
        vm.Editor.SaveCommand.Execute(null);

        WaitUntil(() => vm.Editor.Categories.Contains("餐饮"), rounds: 100);

        if (!vm.Editor.Categories.Contains("餐饮"))
            throw new Exception($"新输入的分类没有进入候选: [{string.Join(", ", vm.Editor.Categories)}]");

        int categoriesAfterFirst = vm.Editor.Categories.Count;

        vm.AddCommand.Execute(null);
        vm.Editor.DraftDescription = "分类样张二";
        vm.Editor.DraftAmountText = "-6";
        vm.Editor.DraftCategory = "  餐饮  ";
        vm.Editor.SaveCommand.Execute(null);

        WaitUntil(() => CountDayEntries(DateTime.Today) == todayBeforeCategory + 2, rounds: 100);
        Pump();

        Report($"分类候选 = [{string.Join(", ", vm.Editor.Categories)}]" +
               $"（用过一个新分类后 {categoriesAfterFirst} 项）");

        if (CountDayEntries(DateTime.Today) != todayBeforeCategory + 2)
            throw new Exception("带分类的两笔没有都落到磁盘");

        if (vm.Editor.Categories.Count != categoriesAfterFirst)
            throw new Exception($"重复使用同一个分类造出了重复候选: [{string.Join(", ", vm.Editor.Categories)}]");

        if (vm.Editor.Categories.Count(c => string.Equals(c, "餐饮", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new Exception("同一分类在候选里出现了多次");

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
        seed.Editor.DraftDescription = "日历明细样张";
        seed.Editor.DraftAmountText = "-12.5";
        seed.Editor.DraftCategory = "测试";
        seed.Editor.SaveCommand.Execute(null);

        WaitUntil(() => seed.Entries.Count > 0);

        if (seed.Entries.Count == 0) throw new Exception("播种流水失败, 日历明细无法验证");

        var vm = new CalenderBillViewModel(NewRepository());
        var view = new CalenderBill { DataContext = vm };
        Pump();
        Layout(view);

        WaitUntil(() => vm.Entries.Count > 0);
        Layout(view);

        AssertNoCollapse(view, "CalenderBill(日)");

        // 按名字认卡片，不按"DataContext 是 BillEntry 的 Border"：
        // DataContext 沿可视树继承，模板里任何新的 Border 都会命中，
        // 加一个分类色条就会让计数翻倍。
        var entryCards = FindAllCore<Border>(view)
            .Where(b => b.Name == "EntryCard")
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

        // + 号在三种粒度下都显示：默认日期由 CalenderBillViewModel.DraftDate 对每种粒度
        // 各给一个唯一答案（日=定位日；月 / 年=当天落在范围内就用当天，否则范围起始日），
        // 所以"记到哪一天"不再有歧义，按钮不必再藏起来。
        var addBtn = FindField<Button>(view, "AddNewBill")
            ?? throw new Exception("日历页找不到 + 号按钮");

        foreach (var (label, setMode) in new (string, ICommand)[]
                 {
                     ("日视图", vm.ShowDayCommand),
                     ("月视图", vm.ShowMonthCommand),
                     ("年视图", vm.ShowYearCommand)
                 })
        {
            setMode.Execute(null);
            Pump();
            Layout(view);

            if (addBtn.Visibility != Visibility.Visible)
                throw new Exception($"{label}下 + 号按钮应当可见, 实际 {addBtn.Visibility}");
        }

        vm.ShowDayCommand.Execute(null);
        Pump();
        Layout(view);

        vm.AddCommand.Execute(null);
        Pump();

        if (vm.Editor.DraftDate?.Date != DateTime.Today)
            throw new Exception($"日历 + 号没有把草稿日期设为当前定位: {vm.Editor.DraftDate}");

        int todayBefore = CountDayEntries(DateTime.Today);

        vm.Editor.DraftDescription = "日历补记";
        vm.Editor.DraftAmountText = "-4";
        vm.Editor.SaveCommand.Execute(null);

        WaitUntil(() => CountDayEntries(DateTime.Today) == todayBefore + 1, rounds: 100);

        Report($"日历页记一笔后 今天记录数 = {CountDayEntries(DateTime.Today)}（原 {todayBefore}）");

        if (CountDayEntries(DateTime.Today) != todayBefore + 1)
            throw new Exception("日历页记的一笔没有落到磁盘");

        DrainSaves();
        CleanupDate(DateTime.Today);
    }

    // ---- 7. 设置 ----

    /// <summary>Run 键路径：与 SettingsViewModel 里自动启动写的是同一个位置。</summary>
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 自动启动开关会真实写 HKCU 的 Run 项，值是当前进程路径 —— 测试宿主自己。
    /// 所以每次都要先存后还原，否则跑一次测试就会给系统留下一条开机启动
    /// UiSmokeTest.exe 的垃圾项（中途抛异常时更会留下）。
    /// </summary>
    private static void TestSettings()
    {
        var saved = ReadRunValue();
        if (saved is not null)
            Report($"Run 项 PaperCoin 原值 = {saved}");
        try
        {
            TestSettingsCore();
        }
        finally
        {
            RestoreRunValue(saved);
            Report($"Run 项 PaperCoin 已还原 = {ReadRunValue() ?? "(不存在)"}");
        }
    }

    private static string? ReadRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue("PaperCoin") as string;
    }

    private static void RestoreRunValue(string? saved)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null) throw new Exception($"打不开 Run 键: HKCU\\{RunKeyPath}");

        if (saved is null) key.DeleteValue("PaperCoin", throwOnMissingValue: false);
        else key.SetValue("PaperCoin", saved, RegistryValueKind.String);
    }

    private static void TestSettingsCore()
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

        // 设置页有两组分段控件：明暗 3 个 + 风格 3 个，共 6 个。
        // 两组各自独立，GroupName 不能撞 —— 撞了会互相取消选中。
        var segmented = FindAllCore<RadioButton>(view).ToList();
        Report($"分段按钮数 = {segmented.Count}");

        if (segmented.Count != 6)
            throw new Exception($"应有 6 个分段按钮（明暗 3 + 风格 3）, 实际 {segmented.Count}");

        foreach (var group in segmented.GroupBy(r => r.GroupName))
        {
            Report($"  GroupName={group.Key} 有 {group.Count()} 项");

            if (group.Count() != 3)
                throw new Exception($"分组 {group.Key} 应有 3 项, 实际 {group.Count()}");
        }

        // 两组各自必须恰好选中一个：分组名写错时会出现两组同时选中或都不选中
        foreach (var group in segmented.GroupBy(r => r.GroupName))
        {
            int checkedCount = group.Count(r => r.IsChecked == true);

            if (checkedCount != 1)
                throw new Exception($"分组 {group.Key} 选中了 {checkedCount} 项, 应当正好 1 项");
        }

        double lastRight = segmented.Max(r => BoundsIn(view, r).Right);
        Report($"  最后一个分段右边界 = {lastRight:0.#}（内容区右边界 {ContentWidth:0.#}）");
        if (lastRight > ContentWidth + 1)
            throw new Exception($"分段控件超出内容区: {lastRight:0.#} > {ContentWidth:0.#}");

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
            Report($"  {mode,-7} -> 模式={theme.Mode,-7} 实际={theme.Resolved,-7} 调色板={theme.Palette}");
        }

        theme.ApplyMode("System");
        if (theme.Resolved != ThemeManager.GetSystemTheme())
            throw new Exception("跟随系统解析不正确");

        theme.ApplyMode("乱写的值");
        if (theme.Resolved != "Dark") throw new Exception("非法模式没有回退到 Dark");

        theme.ApplyMode("Dark");

        // ---- 风格是独立维度：明暗 2 档 × 风格 3 种 = 6 本调色板，全部要能装载 ----
        string[] styles = [ThemeManager.StyleClassic, ThemeManager.StyleWindows, ThemeManager.StyleLiquid];

        foreach (var brightness in new[] { "Dark", "Bright" })
        {
            theme.ApplyMode(brightness);

            foreach (var style in styles)
            {
                theme.ApplyStyle(style);

                var expected = style == ThemeManager.StyleClassic ? brightness : brightness + style;
                Report($"  {brightness,-7} + {style,-8} -> {theme.Palette}");

                if (theme.Palette != expected)
                    throw new Exception($"{brightness}+{style} 解析成了 {theme.Palette}，应为 {expected}");

                // 名字得真的对应一本字典，装不起来会在切主题那一刻抛异常
                var dict = LoadDict($"/Themes/{theme.Palette}.xaml");
                if (!dict.Contains("ColorMainContentArea"))
                    throw new Exception($"{theme.Palette} 不是一本有效的颜色字典");
            }
        }

        // 跟随系统 + 风格：亮度跟着系统走，风格保持不变
        theme.ApplyMode("System");
        theme.ApplyStyle(ThemeManager.StyleLiquid);
        var systemBrightness = ThemeManager.GetSystemTheme();

        if (theme.Palette != systemBrightness + ThemeManager.StyleLiquid)
            throw new Exception($"跟随系统 + 液态玻璃 解析成了 {theme.Palette}");

        Report($"  跟随系统 + 液态玻璃 -> {theme.Palette}");

        // 非法风格回退到经典
        theme.ApplyStyle("乱写的风格");
        if (theme.Style != ThemeManager.StyleClassic)
            throw new Exception($"非法风格没有回退到经典，实际 {theme.Style}");

        // 复位。注意不能只调 ApplyStyle / ApplyMode —— 它们在"值没变"时**不写盘**，
        // 而这个实例的 Style 是构造时从配置读的；磁盘上被别的实例改成 Liquid 时，
        // 这里的 ApplyStyle(Classic) 会因为"内存里已经是 Classic"而静默不写，
        // 结果是配置留在 Liquid，下一轮由别的项察觉（表现为"某一项莫名失败"）。
        // 所以复位直接落盘。
        theme.ApplyStyle(ThemeManager.StyleClassic);
        theme.ApplyMode("Dark");

        if (theme.Palette != "Dark")
            throw new Exception($"复位后调色板应为 Dark，实际 {theme.Palette}");

        var restoreService = new ConfigService();
        var restoreConfig = restoreService.GetAppConfig();
        restoreConfig.App.Theme = "Dark";
        restoreConfig.App.ThemeStyle = ThemeManager.StyleClassic;
        restoreService.SaveAppConfig(restoreConfig);

        var restored = new ConfigService().GetAppConfig().App;
        Report($"  复位后配置：Theme={restored.Theme} ThemeStyle={restored.ThemeStyle}");

        if (restored.ThemeStyle != ThemeManager.StyleClassic || restored.Theme != "Dark")
            throw new Exception($"配置没还原干净：Theme={restored.Theme} ThemeStyle={restored.ThemeStyle}");
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
        services.AddTransient<StatisticsViewModel>();
        services.AddTransient<SettingsViewModel>();

        PaperCoin.App.Services = services.BuildServiceProvider();
    }

    private static ResourceDictionary LoadDict(string relative)
        => new() { Source = new Uri($"pack://application:,,,/PaperCoin;component{relative}") };

    private static BillRepository NewRepository() => new();

    /// <summary>从测试程序集位置向上找仓库根：认解决方案文件，两种扩展名都接受。</summary>
    private static string? RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null
               && !File.Exists(Path.Combine(dir, "PaperCoin.sln"))
               && !File.Exists(Path.Combine(dir, "PaperCoin.slnx")))
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

    /// <summary>读某一天的账单文件，返回记录条数；文件不存在时返回 0。</summary>
    private static int CountDayEntries(DateTime date)
    {
        var file = Path.Combine(
            PaperCoin.Core.Constants.BillFolder,
            date.Year.ToString(), date.Month.ToString(), date.Day.ToString(),
            "bills.json");

        if (!File.Exists(file)) return 0;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        return System.Text.Json.JsonSerializer
            .Deserialize<List<PaperCoin.Core.Models.BillEntry>>(File.ReadAllText(file), options)
            ?.Count ?? 0;
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

    /// <summary>
    /// 清空整个账单数据目录，保证每轮测试从同一状态开始。
    /// <para>
    /// 磁盘清掉之后**必须把单例仓库的内存索引也失效掉**：<see cref="BillRepository"/> 注册为
    /// 单例并被多项测试共用，只删磁盘的话它的索引里还留着上一批数据，后面的项会读到
    /// 自己没种过的条目（表现为"条数莫名其妙多出来"）。
    /// </para>
    /// </summary>
    private static void ResetBillsData()
    {
        var dir = PaperCoin.Core.Constants.BillFolder;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        App.Services?.GetService<BillRepository>()?.InvalidateIndex();
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

    /// <summary>按 x:Name 深度优先查找可视后代</summary>
    private static T? FindVisualByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match && match.Name == name) return match;

            var nested = FindVisualByName<T>(child, name);
            if (nested is not null) return nested;
        }

        return null;
    }

    /// <summary>按类型深度优先收集可视后代，用于断言"某类元素真的画出来了"</summary>
    private static void CollectVisuals<T>(DependencyObject root, List<T> into) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match) into.Add(match);

            CollectVisuals(child, into);
        }
    }

    /// <summary>
    /// 等一个任务完成，同时继续泵消息循环。
    /// <para>
    /// 整个套件跑在 <c>Application.Current.Dispatcher.Invoke</c> 里，也就是 UI 线程上。
    /// 直接 <c>.GetAwaiter().GetResult()</c> 会**死锁**：仓库的 await 续体要回到 UI 线程的
    /// 同步上下文，而 UI 线程正卡在阻塞等待上。先决条件是任务已经开始（不是刚 new 出来
    /// 还没跑到第一个 await 的），所以这里最后补一次泵，让刚启动的任务有机会推进。
    /// </para>
    /// </summary>
    private static void WaitFor(Task task, int timeoutMs = 30_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (!task.IsCompleted && sw.ElapsedMilliseconds < timeoutMs)
            Pump();

        if (task.IsFaulted)
            throw task.Exception!.GetBaseException();

        if (!task.IsCompleted)
            throw new Exception($"等待任务超时（{timeoutMs}ms）");
    }

    /// <summary>等一批任务完成，同样继续泵消息循环</summary>
    private static void WaitFor(IEnumerable<Task> tasks, int timeoutMs = 30_000)
        => WaitFor(Task.WhenAll(tasks), timeoutMs);

    /// <summary>等一个带返回值的任务完成并取回结果，同样继续泵消息循环</summary>
    private static T RoundTrip<T>(Task<T> task, int timeoutMs = 30_000)
    {
        WaitFor(task, timeoutMs);
        return task.Result;
    }

    private static T? FindField<T>(object owner, string name) where T : class    {
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
        vm.Editor.DraftDescription = "空状态样张";
        vm.Editor.DraftAmountText = "-1";
        vm.Editor.SaveCommand.Execute(null);

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

        // 任务失败时 WhenAll 会抛，那是预期的信号；这里只关心有没有等完，
        // 失败详情交给下面逐个报告。
        bool finished;
        try
        {
            WaitFor(tasks, 30_000);
            finished = true;
        }
        catch (Exception)
        {
            finished = tasks.All(t => t.IsCompleted);
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
            seedVm.Editor.DraftDescription = $"动效样张 {i + 1}";
            seedVm.Editor.DraftAmountText = $"-{(i + 1) * 7}";
            seedVm.Editor.SaveCommand.Execute(null);
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
        vm.Editor.DraftDescription = "后来的一笔";
        vm.Editor.DraftAmountText = "-9";
        vm.Editor.SaveCommand.Execute(null);

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
            seedVm.Editor.DraftDescription = $"日历动效 {i + 1}";
            seedVm.Editor.DraftAmountText = $"-{(i + 1) * 3}";
            seedVm.Editor.SaveCommand.Execute(null);
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

    // ---- 15. 主题切换的颜色过渡 ----

    /// <summary>
    /// 主题切换要走颜色插值，而不是把字典条目直接换成现成的静态画刷。
    /// 本环境渲染循环不推进，动画时钟不走，所以断言的是"过渡确实被建起来了"：
    /// 画刷未被冻结、带活动动画、基准值等于目标色，且 DynamicResource 消费者拿到的就是同一个实例。
    /// 这几条任何一条不成立，界面上的过渡都是假的。
    /// </summary>
    private static void TestThemeTransition()
    {
        var merged = Application.Current!.Resources.MergedDictionaries;
        var index = PaperCoin.ThemeRenderer.IndexOfColorDictionary(merged);
        if (index < 0) throw new Exception("找不到颜色字典");

        var colors = merged[index];

        // 先落到一个确定的静态状态
        PaperCoin.ThemeRenderer.Apply("Dark");
        ClearBrushAnimations(colors);

        // 消费者必须挂进一个真正显示出来的窗口：资源失效通知只沿已加载的元素传播，
        // 游离元素即使解析过也不会重解析，那样测出来的"没生效"是宿主问题而不是过渡问题。
        var border = new Border();
        border.SetResourceReference(Border.BackgroundProperty, "ColorMainContentArea");

        var host = new Grid();
        host.Children.Add(border);

        var window = new Window
        {
            Width = 120,
            Height = 80,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Content = host
        };

        window.Show();
        Pump();

        var before = border.Background as SolidColorBrush;
        Report($"  切换前消费者画刷 = {before?.Color.ToString() ?? "null"}");

        if (before is null)
            throw new Exception("消费者一开始就没有解析到画刷，测试宿主不成立");

        try
        {
            PaperCoin.ThemeRenderer.Apply("Bright");
            Pump();

            var main = (SolidColorBrush)colors["ColorMainContentArea"];
            var shadow = (SolidColorBrush)colors["ColorShadow"];

            Report($"ColorMainContentArea: Color={main.Color} IsFrozen={main.IsFrozen} " +
                   $"HasAnimatedProperties={main.HasAnimatedProperties}");
            Report($"ColorShadow: Color={shadow.Color} Opacity={shadow.Opacity}");

            if (main.IsFrozen)
                throw new Exception("过渡画刷被冻结了，动画不可能生效");

            if (!main.HasAnimatedProperties)
                throw new Exception("主题切换没有建立颜色过渡（画刷上没有活动动画）");

            // FillBehavior.Stop 会让动画结束时落回基准值，所以基准值必须等于目标色，否则会闪回旧颜色
            var colorBase = main.GetAnimationBaseValue(SolidColorBrush.ColorProperty);
            if (colorBase is not Color baseColor || baseColor != Color.FromRgb(0xFF, 0xFF, 0xFF))
                throw new Exception($"颜色过渡的基准值不是目标色，动画结束会闪回旧值: {colorBase}");

            // 两个主题的阴影透明度不同，只插值颜色会看到硬跳
            var opacityBase = shadow.GetAnimationBaseValue(SolidColorBrush.OpacityProperty);
            if (opacityBase is not double opacity || Math.Abs(opacity - 0.30) > 0.001)
                throw new Exception($"阴影透明度没有参与过渡，基准值应为 0.30: {opacityBase}");

            if (!ReferenceEquals(border.Background, main))
                throw new Exception(
                    $"消费者没有重新解析到过渡画刷，界面不会跟着动: " +
                    $"{(border.Background as SolidColorBrush)?.Color.ToString() ?? "null"} vs {main.Color}");

            Report($"  基准值 颜色={colorBase} 透明度={opacityBase}  消费者同实例=True");
        }
        finally
        {
            window.Close();
            Pump();
        }

        // 摘掉动画应当落回基准值，也就是新主题的目标色
        PaperCoin.ThemeRenderer.Apply("Dark");
        ClearBrushAnimations(colors);

        var restored = (SolidColorBrush)colors["ColorMainContentArea"];
        Report($"  复位后 ColorMainContentArea = {restored.Color}（应为 Dark 的目标色）");

        if (restored.Color != Color.FromRgb(0x1B, 0x25, 0x33))
            throw new Exception($"摘除动画后颜色没有落回目标值: {restored.Color}");
    }

    // ---- 16. 自有程序集带产品版本 ----

    /// <summary>
    /// 自有程序集必须带上与产品一致的版本号。MSI 安装时按文件版本决定要不要覆盖：
    /// 新版本不大于已安装版本就直接跳过，结果是安装目录里新旧 dll 混装 ——
    /// 新的 PaperCoin.dll 调用旧 PaperCoin.Service.dll 里不存在的方法，运行时才炸 MissingMethodException。
    /// 这条断言把"某个项目忘了设版本"挡在构建期，而不是等用户装上之后。
    /// </summary>
    private static void TestAssemblyVersions()
    {
        var expected = PaperCoin.Core.Constants.AppVersion;

        var assemblies = new (string Name, System.Reflection.Assembly Assembly)[]
        {
            ("PaperCoin", typeof(TodayBill).Assembly),
            ("PaperCoin.Core", typeof(PaperCoin.Core.Constants).Assembly),
            ("PaperCoin.Service", typeof(PaperCoin.Service.BillRepository).Assembly)
        };

        foreach (var (name, assembly) in assemblies)
        {
            var fileVersion = System.Diagnostics.FileVersionInfo
                .GetVersionInfo(assembly.Location).FileVersion;

            Report($"{name}: 程序集版本={assembly.GetName().Version} 文件版本={fileVersion}");

            if (fileVersion != expected + ".0")
                throw new Exception(
                    $"{name} 的文件版本是 {fileVersion}，应为 {expected}.0。" +
                    "版本不递增时 MSI 不会覆盖已安装的旧文件，会造成新旧 dll 混装");
        }

        // 安装项目的 ProductVersion 是第三个版本载体：漏改它，MSI 会按错的版本号判断要不要覆盖文件
        var root = RepoRoot();
        var vdproj = root is null ? null : Path.Combine(root, "PaperCoin.Setup", "PaperCoin.Setup.vdproj");

        if (vdproj is null || !File.Exists(vdproj))
        {
            Report("找不到 PaperCoin.Setup.vdproj，跳过安装项目版本比对");
            return;
        }

        // VS 读不带 BOM 的 vdproj 时会退回按项目里声明的 CodePage（936）解释，
        // 于是第一个中文字串就成乱码、项目直接打不开 —— 而 MSBuild 容忍这种情况，
        // 所以只会在 IDE 里炸。用普通文本编辑器或脚本改写这个文件时很容易把 BOM 弄丢，这里挡住。
        var header = new byte[3];
        using (var stream = File.OpenRead(vdproj))
        {
            if (stream.Read(header, 0, 3) < 3
                || header[0] != 0xEF || header[1] != 0xBB || header[2] != 0xBF)
                throw new Exception(
                    "PaperCoin.Setup.vdproj 缺少 UTF-8 BOM，Visual Studio 会打不开这个项目" +
                    "（MSBuild 不报错，所以只有 IDE 会炸）");
        }

        Report("vdproj 带 UTF-8 BOM");

        var match = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(vdproj), "\"ProductVersion\"\\s*=\\s*\"8:([0-9.]+)\"");

        var installerVersion = match.Success ? match.Groups[1].Value : "(未找到)";
        Report($"安装项目 ProductVersion = {installerVersion}");

        if (installerVersion != expected)
            throw new Exception(
                $"安装项目的 ProductVersion 是 {installerVersion}，自有程序集是 {expected}。" +
                "发布时要和 Directory.Build.props 的 <Version> 一起改");

        // ProductCode 不能与本机已安装的**不同版本**撞车。
        // 撞了的表现是"装不上"：Windows Installer 认定 ProductCode 相同就是同一个产品，
        // 而文件版本又相等（同一个版本号），覆盖也会被跳过 —— 于是双击安装毫无反应。
        // 这个错在 1.3.5 上真发生过：重建同一个版本号时沿用了旧的 ProductCode。
        var productMatch = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(vdproj), "\"ProductCode\"\\s*=\\s*\"8:(\\{[0-9A-Fa-f-]+\\})\"");

        if (!productMatch.Success)
        {
            Report("vdproj 里读不到 ProductCode，跳过与本机已安装版本比对");
            return;
        }

        var productCode = productMatch.Groups[1].Value.ToUpperInvariant();
        Report($"安装项目 ProductCode = {productCode}");

        foreach (var installed in FindInstalledPaperCoinProducts())
        {
            if (!string.Equals(installed.ProductCode, productCode, StringComparison.OrdinalIgnoreCase))
                continue;

            if (installed.Version == expected)
            {
                Report($"  本机已装同版本 {installed.Version}，ProductCode 相同（正常：同一个产品）");
                continue;
            }

            throw new Exception(
                $"ProductCode {productCode} 已经被本机安装的 {installed.Version} 占用，" +
                $"而这次要出的是 {expected}。Windows Installer 会认定\"同一个产品已安装\"，" +
                "文件版本又相等，于是装不上。发布新版本时必须换一个新的 ProductCode");
        }

        Report("  ProductCode 未被本机的其它版本占用");
    }

    /// <summary>本机已安装的 PaperCoin 产品（从卸载注册表读）</summary>
    private static List<(string ProductCode, string Version)> FindInstalledPaperCoinProducts()
    {
        var result = new List<(string, string)>();

        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var sub in roots)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var root = baseKey.OpenSubKey(sub);
                    if (root is null) continue;

                    foreach (var name in root.GetSubKeyNames())
                    {
                        using var entry = root.OpenSubKey(name);
                        if (entry?.GetValue("DisplayName") as string != "PaperCoin") continue;
                        if (name.StartsWith('{') && name.EndsWith('}'))
                            result.Add((name, entry.GetValue("DisplayVersion") as string ?? ""));
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    // 读不到某个 hive 不算失败：能读到的部分已经足够做这项检查
                }
            }
        }

        return result;
    }

    // ---- 17. 安装位置记录 ----

    /// <summary>
    /// 程序必须把安装位置写进注册表。安装包的 "Locator" 节点搜的就是这个键，搜到才沿用旧路径；
    /// 键名或值名对不上、或写不进去，升级就会装到第二个目录。
    /// <para>
    /// 1.3.0 起写的是 HKLM（每机器安装），并且**必须提权**才写得进去。测试进程通常不是
    /// 管理员，所以这里分两条路：能写就写进去核对（并原样还原，测试进程的基目录是 bin 目录，
    /// 留下去会让下一次升级真的装错地方），写不进去就只核对"键与值名"这条与安装包的约定，
    /// 并明确报告"因权限跳过了写入核对"，而不是假装通过。
    /// </para>
    /// </summary>
    private static void TestInstallLocation()
    {
        const string keyPath = PaperCoin.Service.InstallLocation.KeyPath;
        const string valueName = PaperCoin.Service.InstallLocation.ValueName;

        if (string.IsNullOrEmpty(keyPath) || string.IsNullOrEmpty(valueName))
            throw new Exception("键路径或值名是空的，安装包搜不到任何东西");

        bool elevated = IsElevated();

        using var probe = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
        var original = probe?.GetValue(valueName) as string;

        Report($"HKCU 旧键（1.2.x 遗留，1.3.0 起迁移用）: " +
               $"{(Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath) is null ? "无" : "有")}");
        Report($"HKLM\\{keyPath}\\{valueName} 当前 = {original ?? "(空)"}");
        Report($"进程是否提权: {elevated}");

        if (!elevated)
        {
            // 不提权就只能核对约定本身。Code 侧的 Read() 优先 HKLM、回落 HKCU 这条逻辑
            // 由 1.2.x 升级兼容性保证，见 InstallLocation.Read 的注释。
            Report("未提权，跳过写入核对（Record 写 HKLM，普通权限写不进去）");
            return;
        }

        try
        {
            PaperCoin.Service.InstallLocation.Record();

            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath)
                ?? throw new Exception($"没有写入注册表键 HKLM\\{keyPath}");

            var recorded = key.GetValue(valueName) as string;

            if (string.IsNullOrEmpty(recorded))
                throw new Exception("安装位置没有写进注册表，升级时安装包搜不到旧路径");

            var expected = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

            if (!string.Equals(recorded, expected, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"记录的位置与实际不符: {recorded} vs {expected}");

            // Read() 必须优先给 HKLM 的值：升级时安装包搜的也是 HKLM
            if (!string.Equals(PaperCoin.Service.InstallLocation.Read(), recorded, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Read() 没有优先返回 HKLM 记录的位置");

            Report($"写入并读回成功: {recorded}");
        }
        finally
        {
            // 测试进程的基目录是 bin 目录，这个值必须还原，否则下一次升级会装到那儿去
            if (original is not null)
            {
                using var restore = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath);
                restore.SetValue(valueName, original);
                Report($"已还原为 {original}");
            }
            else
            {
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
                Report("测试前没有这个值，已清理");
            }
        }
    }

    /// <summary>当前进程是否以管理员身份运行</summary>
    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>摘掉字典里所有画刷的过渡动画，让资源回到静态值</summary>
    private static void ClearBrushAnimations(ResourceDictionary colors)
    {
        foreach (var key in colors.Keys.Cast<object>().ToList())
        {
            if (colors[key] is not SolidColorBrush brush) continue;

            // 静态画刷（字典装进去时被冻结）本来就没有动画，对它们 BeginAnimation 会抛异常
            if (brush.IsFrozen) continue;

            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.BeginAnimation(SolidColorBrush.OpacityProperty, null);
        }
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

    // ---- 20. 分类色板 ----

    /// <summary>
    /// 分类到颜色的映射必须是**稳定**的：同一个分类在任何范围、任何一次启动里都得是同一个颜色。
    /// 用 string.GetHashCode 实现时进程内看着正常、重启后整套配色会换一遍，
    /// 所以这里连"同一次运行里两次调用一致"和"大小写/空格不改变结果"一起断言。
    /// </summary>
    private static void TestCategoryPalette()
    {
        var samples = new[] { "餐饮", "交通", "购物", "居住", "娱乐", "医疗", "工资", "other", "OTHER", " 餐饮 " };

        foreach (var name in samples)
        {
            var first = PaperCoin.Core.CategoryPalette.ThemeKeyOf(name);
            var second = PaperCoin.Core.CategoryPalette.ThemeKeyOf(name);

            if (!string.Equals(first, second, StringComparison.Ordinal))
                throw new Exception($"同一分类两次调用返回不同颜色键: {name} -> {first} / {second}");
        }

        // 大小写与首尾空格必须归一到同一个键，否则账单里 "Food" 与 "food" 会分成两种颜色
        var lower = PaperCoin.Core.CategoryPalette.ThemeKeyOf("food");
        var upper = PaperCoin.Core.CategoryPalette.ThemeKeyOf("FOOD");
        var padded = PaperCoin.Core.CategoryPalette.ThemeKeyOf("  food  ");

        if (lower != upper || lower != padded)
            throw new Exception($"大小写或空格改变了分类颜色: food={lower} FOOD={upper} \"  food  \"={padded}");

        // 空分类固定落在未分类色，不参与哈希
        foreach (var blank in new string?[] { null, "", "   " })
        {
            var key = PaperCoin.Core.CategoryPalette.ThemeKeyOf(blank);
            if (key != PaperCoin.Core.CategoryPalette.ThemeKeyOfSlot(PaperCoin.Core.CategoryPalette.SlotCount))
                throw new Exception($"空白分类没有落到未分类色: {key}");
        }

        if (PaperCoin.Core.CategoryPalette.SlotCount <= 1)
            throw new Exception("槽位数退化成了 1，所有分类会同色");

        var allKeys = PaperCoin.Core.CategoryPalette.AllThemeKeys;
        Report($"槽位 {PaperCoin.Core.CategoryPalette.SlotCount} 个, 颜色键 {allKeys.Count} 个: {string.Join(", ", allKeys)}");

        // 每个颜色键都必须在两本主题字典里可解析。CategoryPalette 只产出键名，解析失败时
        // DynamicResource 与转换器都是静默回退，症状是"某个分类没有颜色"且没有任何报错。
        var dark = LoadDict("/Themes/Dark.xaml");
        var bright = LoadDict("/Themes/Bright.xaml");

        var missing = new List<string>();
        foreach (var key in allKeys)
        {
            if (dark[key] is not SolidColorBrush) missing.Add($"Dark.{key}");
            if (bright[key] is not SolidColorBrush) missing.Add($"Bright.{key}");
        }

        if (missing.Count > 0)
            throw new Exception("主题字典缺少分类颜色: " + string.Join(", ", missing));

        // 颜色必须互不相同：撞色会让圆环上相邻两段分不出来
        var distinct = allKeys.Select(k => ((SolidColorBrush)dark[k]).Color).Distinct().Count();
        Report($"Dark 下 {allKeys.Count} 个键对应 {distinct} 种颜色");

        if (distinct != allKeys.Count)
            throw new Exception($"分类颜色有重复: {allKeys.Count} 个键只有 {distinct} 种颜色");

        // 跨进程稳定性。同一进程里两次调用一致并不说明问题：
        // 用 string.GetHashCode 实现时进程内稳定、每次启动却换一套配色，
        // 那时用户看到的是"重启一次，同一个分类换了个颜色"。
        AssertStableAcrossProcesses();
    }

    /// <summary>
    /// 起两次独立进程，比对"分类 → 颜色键"的映射。两次必须完全一致。
    /// <para>
    /// 这是唯一能抓住 <c>string.GetHashCode</c> 那种每进程随机化的办法：
    /// 它在同一个进程里是稳定的，本地断言天然看不见。
    /// </para>
    /// </summary>
    private static void AssertStableAcrossProcesses()
    {
        var first = RunColorProbe();
        var second = RunColorProbe();

        Report($"跨进程探针: 第一次 {first.Count} 行, 第二次 {second.Count} 行");

        if (first.Count == 0)
            throw new Exception("跨进程探针没有输出，探针模式可能没生效");

        if (first.Count != second.Count)
            throw new Exception($"两次探针输出行数不同: {first.Count} vs {second.Count}");

        for (int i = 0; i < first.Count; i++)
        {
            if (first[i] != second[i])
                throw new Exception(
                    $"同一分类在两次进程里拿到了不同颜色（哈希不稳定）: {first[i]} vs {second[i]}");
        }
    }

    /// <summary>跑一次探针子进程，返回它的每一行输出</summary>
    private static List<string> RunColorProbe()
    {
        // 用当前可执行文件本身：测试宿主是 exe，探针模式在 Main 开头就返回了，
        // 不会再起窗口、也不会碰账单数据。
        var exe = Environment.ProcessPath
            ?? throw new Exception("取不到当前进程的可执行文件路径");

        var startInfo = new System.Diagnostics.ProcessStartInfo(exe, ColorProbeArgument)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new Exception("无法启动探针子进程");

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);

        if (process.ExitCode != 0)
            throw new Exception($"探针子进程退出码 {process.ExitCode}: {process.StandardError.ReadToEnd()}");

        return [.. output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())];
    }

    // ---- 21. 仓库按分类汇总支出 ----

    /// <summary>
    /// 汇总必须**只算支出**并把金额取成正数。收入混进来会让"消费占比"这件事直接失真，
    /// 而且因为金额是正数、混进来时看不出来，所以这条断言要带上收入数据一起跑。
    /// </summary>
    private static void TestCategoryExpenseAggregation()
    {
        DrainSaves();
        ResetBillsData();

        var repo = NewRepository();

        var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-60));
        var to = DateOnly.FromDateTime(DateTime.Today);
        var before = from.AddDays(-1);
        var after = to.AddDays(1);

        var entries = new List<PaperCoin.Core.Models.BillEntry>
        {
            // 同一分类、两条记录：必须合并成一条，金额与笔数都累加
            Entry(from, "午饭", -50m, "餐饮"),
            Entry(from.AddDays(3), "晚饭", -30m, "餐饮"),
            // 大小写不同的同一分类：必须归并
            Entry(from.AddDays(5), "打车", -20m, "交通"),
            Entry(from.AddDays(6), "地铁", -10m, "交通"),
            // 收入：绝不能被算进支出
            Entry(from.AddDays(7), "工资", 1000m, "工资"),
            // 没有分类
            Entry(from.AddDays(8), "忘了记", -5m, null),
            // 边界外：起始日之前、结束日之后，都不该进来
            Entry(before, "范围前", -999m, "范围外"),
            Entry(after, "范围后", -999m, "范围外")
        };

        WaitFor(entries.GroupBy(e => e.Date).Select(g => repo.SaveAsync(g.ToList())));

        DrainSaves();

        var result = RoundTrip(repo.GetCategoryExpenseAsync(from, to));

        Report("汇总: " + string.Join(" | ",
            result.Select(r => $"{r.Category}={r.Expense:0.##}({r.Count}笔)")));

        if (result.Any(r => r.Category == "工资"))
            throw new Exception("收入分类 工资 混进了支出汇总");

        if (result.Any(r => r.Category == "范围外"))
            throw new Exception("日期区间外的记录混进了汇总（首尾两天之外的没被排除）");

        var food = result.FirstOrDefault(r => r.Category == "餐饮")
            ?? throw new Exception("找不到 餐饮 的汇总");

        if (food.Expense != 80m)
            throw new Exception($"餐饮 金额应为 80（−50 与 −30 的绝对值之和），实际 {food.Expense}");

        if (food.Count != 2)
            throw new Exception($"餐饮 笔数应为 2，实际 {food.Count}");

        var traffic = result.FirstOrDefault(r => r.Category == "交通")
            ?? throw new Exception("找不到 交通 的汇总");

        if (traffic.Expense != 30m)
            throw new Exception($"交通 金额应为 30，实际 {traffic.Expense}");

        if (result.Any(r => r.Expense <= 0))
            throw new Exception("汇总金额必须全为正数（取绝对值），出现了非正数");

        if (result[0].Category != "餐饮")
            throw new Exception($"没有按金额降序排: 第一条是 {result[0].Category}");

        // 空分类要归到"未分类"，不能丢掉
        var blank = result.FirstOrDefault(r => r.Category == PaperCoin.Core.CategoryPalette.Uncategorized)
            ?? throw new Exception("没有分类的流水被丢掉了");

        if (blank.Expense != 5m)
            throw new Exception($"未分类金额应为 5，实际 {blank.Expense}");

        // 索引版本号必须随写入前进：统计页靠它判断要不要重算，不动的话永远显示旧数字
        int revision = repo.Revision;
        WaitFor(repo.SaveAsync([Entry(to, "新增一笔", -1m, "测试")]));
        DrainSaves();

        if (repo.Revision == revision)
            throw new Exception("写入之后索引版本号没有前进，统计页会一直显示旧数据");

        Report($"索引版本号 {revision} -> {repo.Revision}");

        DrainSaves();
        ResetBillsData();
    }

    // ---- 22. 统计口径 ----

    /// <summary>
    /// 统计页的三份派生数据（圆环 / 排行 / 趋势）必须来自同一次聚合，口径一致。
    /// 这里跨越两个月造数，好让"本月"与"本年"真的选出不同结果 ——
    /// 数据全落在同一个月时，范围切换写错也看不出来。
    /// </summary>
    private static void TestStatisticsAggregation()
    {
        DrainSaves();
        ResetBillsData();

        var repo = NewRepository();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var expected = SeedStatisticsData(repo, today);

        var vm = new StatisticsViewModel(repo);
        WaitFor(vm.ReloadNowAsync());

        // 默认范围是本月
        if (!vm.IsMonthRange)
            throw new Exception("统计页默认范围不是本月");

        Report($"范围 {vm.RangeText}: 支出 {vm.TotalExpenseText} 收入 {vm.TotalIncomeText} {vm.EntryCountText}");
        Report("排行: " + string.Join(" | ",
            vm.RankRows.Select(r => $"{r.Label}={r.Amount:0.##}({r.ShareText})")));

        var monthFood = vm.RankRows.FirstOrDefault(r => r.Label == "餐饮")
            ?? throw new Exception("本月排行里没有 餐饮");

        if (monthFood.Amount != expected.MonthFood)
            throw new Exception($"本月餐饮应为 {expected.MonthFood}，实际 {monthFood.Amount}");

        if (monthFood.Count != expected.MonthFoodCount)
            throw new Exception($"本月餐饮笔数应为 {expected.MonthFoodCount}，实际 {monthFood.Count}");

        if (vm.TotalExpense != expected.MonthExpense)
            throw new Exception($"本月支出合计应为 {expected.MonthExpense}，实际 {vm.TotalExpense}");

        if (vm.TotalIncome != expected.MonthIncome)
            throw new Exception($"本月收入合计应为 {expected.MonthIncome}，实际 {vm.TotalIncome}");

        // 排行必须降序，且第一条是金额最大的
        for (int i = 1; i < vm.RankRows.Count; i++)
        {
            if (vm.RankRows[i - 1].Amount < vm.RankRows[i].Amount)
                throw new Exception($"排行没有按金额降序: 第 {i} 行 {vm.RankRows[i].Label} 比前一行大");
        }

        if (vm.RankRows[0].BarFraction != 1.0)
            throw new Exception($"第一名的条形占比应为 1.0，实际 {vm.RankRows[0].BarFraction}");

        // 图例不能超过限制，且必须是圆环的前几段
        if (vm.LegendRows.Count > 5)
            throw new Exception($"图例超过 5 行: {vm.LegendRows.Count}");

        if (vm.LegendRows.Count > vm.Segments.Count)
            throw new Exception("图例行数多于圆环分段数");

        for (int i = 0; i < vm.LegendRows.Count; i++)
        {
            if (!ReferenceEquals(vm.LegendRows[i], vm.Segments[i]))
                throw new Exception($"图例第 {i} 行不是圆环的同一段，颜色会对不上");
        }

        // 占比之和必须接近 1
        double share = vm.RankRows.Sum(r => r.Share);
        if (Math.Abs(share - 1.0) > 0.001)
            throw new Exception($"排行占比之和应为 1，实际 {share:0.####}");

        // 切到"本年"：应看到 12 个月前的另一笔餐饮
        vm.ShowYearCommand.Execute(null);
        WaitFor(vm.ReloadNowAsync());

        Report($"范围 {vm.RangeText}: 支出 {vm.TotalExpenseText}，餐饮 {vm.RankRows.First(r => r.Label == "餐饮").Amount:0.##}");

        if (!vm.IsYearRange)
            throw new Exception("切换到本年后 IsYearRange 仍为假");

        var yearFood = vm.RankRows.First(r => r.Label == "餐饮");

        if (yearFood.Amount != expected.YearFood)
            throw new Exception($"本年餐饮应为 {expected.YearFood}（本月 + 去年的），实际 {yearFood.Amount}");

        // 趋势与范围无关，永远是 12 个月
        if (vm.TrendBars.Count != 12)
            throw new Exception($"趋势柱应为 12 根，实际 {vm.TrendBars.Count}");

        if (vm.TrendBars.Any(b => b.HeightFraction < 0 || b.HeightFraction > 1))
            throw new Exception("趋势柱高度占比超出 [0,1]");

        if (vm.TrendBars[^1].Amount != expected.MonthExpense)
            throw new Exception($"趋势最后一根应是本月支出 {expected.MonthExpense}，实际 {vm.TrendBars[^1].Amount}");

        Report($"趋势最高: {vm.PeakMonthText}");

        // 分类明细：选中"餐饮"应列出它**全部时间**的流水，不受当前范围限制
        Report($"分类清单: {string.Join(", ", vm.Categories)}");

        if (!vm.Categories.Contains("餐饮"))
            throw new Exception("分类清单里没有 餐饮");

        if (!vm.Categories.Contains("交通"))
            throw new Exception("分类清单里没有 交通");

        vm.ShowCategoryCommand.Execute("餐饮");
        Pump();

        if (!vm.HasCategorySelection || vm.SelectedCategory != "餐饮")
            throw new Exception("选中餐饮后 SelectedCategory 不对");

        // 本年范围内餐饮 2 笔，但明细是全部时间，应当还有去年那一笔
        if (vm.CategoryEntries.Count != expected.AllFoodCount)
            throw new Exception(
                $"餐饮明细应为全部时间的 {expected.AllFoodCount} 笔（不受范围限制），实际 {vm.CategoryEntries.Count}");

        if (vm.CategoryEntries.Any(e => PaperCoin.Core.CategoryPalette.Normalize(e.Category) != "餐饮"))
            throw new Exception("分类明细里混进了别的分类");

        // 列表按日期倒序，最近的在前
        for (int i = 1; i < vm.CategoryEntries.Count; i++)
        {
            if (vm.CategoryEntries[i - 1].Date < vm.CategoryEntries[i].Date)
                throw new Exception($"分类明细没有按日期倒序: 第 {i} 行比前一行新");
        }

        Report($"分类明细 {vm.CategoryDetailTitle}: {vm.CategorySummaryText}");

        if (!vm.CategorySummaryText.Contains($"{expected.AllFoodCount} 笔"))
            throw new Exception($"分类汇总文案里没有笔数: {vm.CategorySummaryText}");

        // 换时间范围不应把选中的分类踢掉 —— 明细本来就不受范围限制
        vm.ShowMonthCommand.Execute(null);
        WaitFor(vm.ReloadNowAsync());

        if (vm.SelectedCategory != "餐饮")
            throw new Exception("切换时间范围后分类明细的选中项被清掉了");

        if (vm.CategoryEntries.Count != expected.AllFoodCount)
            throw new Exception(
                $"切换范围后餐饮明细变了: {vm.CategoryEntries.Count}，应当仍是全部时间的 {expected.AllFoodCount} 笔");

        vm.ClearCategoryCommand.Execute(null);

        if (vm.HasCategorySelection)
            throw new Exception("清除后仍有选中的分类");

        DrainSaves();
        ResetBillsData();
    }

    // ---- 26. 每次进页面都重播入场动画 ----

    /// <summary>
    /// 切走再切回，每个页面的入场动画都必须重新播一轮。
    /// <para>
    /// 页面实例被 MainWindow 长期缓存，控件不会重建 —— 所有"只播一次"的守卫标志
    /// 都必须显式复位，漏掉任何一个，那个动画就只在本次进程第一次进页面时看得到。
    /// 这类问题不会报错，只是动画悄悄没了。
    /// </para>
    /// </summary>
    private static void TestEntranceReplaysOnEveryEntry()
    {
        DrainSaves();
        ResetBillsData();

        // 空数据：空提示入场才是这条链路上会走的动画，也最容易漏复位
        var window = new MainWindow
        {
            Width = WindowWidth,
            Height = WindowHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
        };

        window.Show();
        Pump();

        // MainWindow 的页面是按需创建的：没导航过的页面还不在缓存里，取不到。
        // 先各走一遍，再开始计数。
        window.NavigateForTest("CalenderBill");
        Pump();
        window.NavigateForTest("TodayBill");
        Pump();

        var today = window.FindPageForTest("TodayBill") as TodayBill
            ?? throw new Exception("找不到今日页");

        var calendar = window.FindPageForTest("CalenderBill") as CalenderBill
            ?? throw new Exception("找不到日历页");

        // ---- 今日页的空提示 ----
        window.NavigateForTest("TodayBill");
        Pump();
        for (int i = 0; i < 8; i++) Pump();

        int todayFirst = today.EmptyHintPlayCount;
        Report($"今日页第一次进场：空提示播了 {todayFirst} 次");

        if (todayFirst == 0)
            throw new Exception("今日页第一次进场时空提示就没播，测试前提不成立");

        window.NavigateForTest("Settings");
        Pump();
        window.NavigateForTest("TodayBill");
        Pump();
        for (int i = 0; i < 8; i++) Pump();

        Report($"今日页第二次进场：空提示 {todayFirst} -> {today.EmptyHintPlayCount}");

        if (today.EmptyHintPlayCount <= todayFirst)
            throw new Exception(
                $"第二次进今日页时空提示没有重播入场（{todayFirst} -> {today.EmptyHintPlayCount}）");

        // ---- 日历页的空提示 ----
        window.NavigateForTest("CalenderBill");
        Pump();
        for (int i = 0; i < 8; i++) Pump();

        int calFirst = calendar.EmptyHintPlayCount;
        Report($"日历页第一次进场：空提示播了 {calFirst} 次");

        if (calFirst == 0)
            throw new Exception("日历页第一次进场时空提示就没播，测试前提不成立");

        window.NavigateForTest("Settings");
        Pump();
        window.NavigateForTest("CalenderBill");
        Pump();
        for (int i = 0; i < 8; i++) Pump();

        Report($"日历页第二次进场：空提示 {calFirst} -> {calendar.EmptyHintPlayCount}");

        if (calendar.EmptyHintPlayCount <= calFirst)
            throw new Exception(
                $"第二次进日历页时空提示没有重播入场（{calFirst} -> {calendar.EmptyHintPlayCount}）");

        // ---- 日历页年视图的整片入场 ----
        // 这个标志原来只在"本次进程第一次进年视图"时置一次，换页从不复位
        window.NavigateForTest("CalenderBill");
        Pump();

        if (calendar.DataContext is not CalenderBillViewModel vm)
            throw new Exception("日历页的 DataContext 不是 CalenderBillViewModel");

        // 日视图下不该播：年视图的整片入场只在真的处于年视图时才播，
        // 否则切页时就会凭空多播一轮。
        int beforeYear = calendar.YearEntrancePlayCount;

        if (beforeYear != 0)
            throw new Exception($"还在日视图就播了年视图整片入场（{beforeYear} 次）");

        vm.ShowYearCommand.Execute(null);
        Pump();
        for (int i = 0; i < 8; i++) Pump();

        int yearFirst = calendar.YearEntrancePlayCount;
        Report($"日视图下整片播放 {beforeYear} 次；切到年视图后 {yearFirst} 次");

        if (yearFirst != 1)
            throw new Exception($"切到年视图后整片入场播了 {yearFirst} 次，应当正好 1 次");

        // 切走再回来，重新进年视图。走真实导航路径：MainWindow 换页会把页面摘掉再挂回来，
        // 进场动画的复位就挂在 Loaded 上。自己造一个 DataContext 测不出这条路径。
        window.NavigateForTest("Settings");
        Pump();
        window.NavigateForTest("CalenderBill");
        Pump();

        if (calendar.DataContext is CalenderBillViewModel vm2 && !vm2.IsYearMode)
        {
            vm2.ShowYearCommand.Execute(null);
            Pump();
        }

        for (int i = 0; i < 8; i++) Pump();

        Report($"年视图第二次入场：整片 {yearFirst} -> {calendar.YearEntrancePlayCount}");

        if (yearFirst > 0 && calendar.YearEntrancePlayCount <= yearFirst)
            throw new Exception(
                $"第二次进年视图时整片入场没有重播（{yearFirst} -> {calendar.YearEntrancePlayCount}）");

        window.Close();
        Pump();

        DrainSaves();
        ResetBillsData();
    }

    // ---- 31. 日历页的增删改 ----

    /// <summary>
    /// 日历页要像今日页那样能改、能删。三种粒度（日 / 月 / 年）的明细列表是同一个控件，
    /// 所以三个粒度都要验一遍 —— 月 / 年视图下列表混了多天，删除时最容易出错：
    /// 用当前列表整片回写会把别的日期一起覆盖掉。
    /// </summary>
    private static void TestCalendarEditAndDelete()
    {
        DrainSaves();
        ResetBillsData();

        var repo = App.Services.GetRequiredService<BillRepository>();
        var today = DateOnly.FromDateTime(DateTime.Today);

        // 四天各一笔：删一天里的一条时，其它天的必须原样还在。
        // 注意日视图只显示"当天"，所以四个日期都要落在本月 / 本年里才验得到月、年视图。
        var dates = new[] { today, today.AddDays(-1), today.AddDays(-2), today.AddDays(-3) };

        var seed = new List<PaperCoin.Core.Models.BillEntry>();
        for (int i = 0; i < dates.Length; i++)
            seed.Add(Entry(dates[i], $"日历第{i + 1}笔", -(10m + i), "餐饮"));

        // 今天单独多来两笔：日视图只装当天，一条看不出编辑与删除的差别
        seed.Add(Entry(today, "今天第二笔", -20m, "交通"));
        seed.Add(Entry(today, "今天第三笔", -30m, "购物"));

        int todayCount = seed.Count(e => e.Date == today);

        WaitFor(seed.GroupBy(e => e.Date).Select(g => repo.SaveAsync(g.ToList())));
        DrainSaves();

        var vm = new CalenderBillViewModel(repo);
        var view = new CalenderBill { DataContext = vm };

        Pump();
        Layout(view);
        WaitUntil(() => vm.Entries.Count >= dates.Length);
        Layout(view);
        for (int i = 0; i < 10; i++) Pump();
        Layout(view);

        // 日视图只装当天，所以这里比的是"今天有几笔"，不是种子总数
        if (vm.Entries.Count != todayCount)
            throw new Exception($"日视图应有 {todayCount} 条（当天的），实际 {vm.Entries.Count}");

        // ---- 改：日视图点卡片走 EditCommand ----
        var first = vm.Entries[0];

        Report($"[1] 日视图 {vm.Entries.Count} 条（当天），准备改 '{first.Description}'");

        if (!vm.EditCommand.CanExecute(first))
            throw new Exception("[1] EditCommand 对一条真实流水不可执行");

        vm.EditCommand.Execute(first);
        Pump();

        if (!vm.Editor.IsOpen)
            throw new Exception("[1] 点卡片之后编辑面板没有打开");

        if (vm.Editor.DraftDescription != first.Description)
            throw new Exception(
                $"[1] 面板没带上这条的数据：草稿='{vm.Editor.DraftDescription}'，应为 '{first.Description}'");

        Report($"[1] 面板已打开，草稿='{vm.Editor.DraftDescription}'，日期={vm.Editor.DraftDate:yyyy-MM-dd}");

        // 改描述与金额后保存
        vm.Editor.DraftDescription = "日历改过的";
        vm.Editor.DraftAmountText = "-999";
        vm.Editor.SaveCommand.Execute(null);

        for (int i = 0; i < 30; i++) Pump();
        WaitUntil(() => vm.Entries.Any(e => e.Description == "日历改过的"));
        Layout(view);

        var edited = vm.Entries.FirstOrDefault(e => e.Description == "日历改过的")
            ?? throw new Exception("[1] 保存后列表里找不到改过的描述");

        Report($"[1] 改完：'{edited.Description}' 金额={edited.Amount}，列表 {vm.Entries.Count} 条");

        if (edited.Amount != -999m)
            throw new Exception($"[1] 金额没改成：{edited.Amount}");

        if (vm.Entries.Count != todayCount)
            throw new Exception($"[1] 编辑不该改变条数：{vm.Entries.Count} 应为 {todayCount}");

        // ---- 改：点卡片真的事件路径（按下 + 抬起）----
        var card = EntryCardOf(view, edited) ?? throw new Exception("[2] 找不到这张卡片的元素");
        var beforeOpen = vm.Editor.IsOpen;

        view.PressEntryCardForTest(card, mouseInside: true);

        Report($"[2] 按下+抬起之后 面板从 {beforeOpen} 变成 {vm.Editor.IsOpen}");

        if (!vm.Editor.IsOpen)
            throw new Exception("[2] 卡片上的按下+抬起没有打开编辑面板（真实鼠标路径不通）");

        if (vm.Editor.DraftDescription != "日历改过的")
            throw new Exception($"[2] 面板草稿不对：'{vm.Editor.DraftDescription}'");

        vm.Editor.Close();
        Pump();

        // ---- 删：日视图删一条 ----
        int countBefore = vm.Entries.Count;
        var toDelete = vm.Entries[0];

        vm.RemoveCommand.Execute(toDelete);
        for (int i = 0; i < 30; i++) Pump();
        WaitUntil(() => vm.Entries.Count == countBefore - 1);
        Layout(view);

        Report($"[3] 日视图删 '{toDelete.Description}'：{countBefore} -> {vm.Entries.Count} 条");

        if (vm.Entries.Count != countBefore - 1)
            throw new Exception($"[3] 删完条数不对：{vm.Entries.Count}，应为 {countBefore - 1}");

        // 磁盘上也要真的没了
        var onDisk = WaitForLoadDay(repo, toDelete.Date);

        if (onDisk.Any(e => e.Id == toDelete.Id))
            throw new Exception("[3] 界面删掉了但磁盘上还在");

        // 别的日期一条都不能少
        foreach (var d in dates.Where(d => d != toDelete.Date))
        {
            var others = WaitForLoadDay(repo, d);

            if (others.Count != 1)
                throw new Exception($"[3] 删 {toDelete.Date:yyyy-MM-dd} 的流水时把 {d:yyyy-MM-dd} 也弄坏了：" +
                                    $"{others.Count} 条，应为 1 条");
        }

        Report("[3] 其它日期未受影响");

        // ---- 删：月视图（列表混多天，最危险的场景）----
        vm.ShowMonthCommand.Execute(null);
        for (int i = 0; i < 20; i++) Pump();
        Layout(view);
        WaitUntil(() => vm.IsMonthMode);

        int monthBefore = vm.Entries.Count;
        var monthTarget = vm.Entries.FirstOrDefault(e => e.Date != toDelete.Date)
            ?? throw new Exception("[4] 月视图里找不到可删的条目");

        Report($"[4] 月视图 {monthBefore} 条（跨 {vm.Entries.Select(e => e.Date).Distinct().Count()} 天），" +
               $"准备删 '{monthTarget.Description}' @ {monthTarget.Date:yyyy-MM-dd}");

        vm.RemoveCommand.Execute(monthTarget);
        for (int i = 0; i < 30; i++) Pump();
        WaitUntil(() => vm.Entries.Count == monthBefore - 1);
        Layout(view);

        Report($"[4] 月视图删完：{monthBefore} -> {vm.Entries.Count} 条，格子合计={vm.Total}");

        if (vm.Entries.Count != monthBefore - 1)
            throw new Exception($"[4] 月视图删完条数不对：{vm.Entries.Count}，应为 {monthBefore - 1}");

        var monthDisk = WaitForLoadDay(repo, monthTarget.Date);

        if (monthDisk.Any(e => e.Id == monthTarget.Id))
            throw new Exception("[4] 月视图删掉了但磁盘上还在");

        // 月视图删除最容易踩的坑：把看不到的日期一起覆盖掉
        foreach (var d in dates)
        {
            if (d == monthTarget.Date || d == toDelete.Date) continue;

            var others = WaitForLoadDay(repo, d);

            if (others.Count != 1)
                throw new Exception(
                    $"[4] 月视图删 {monthTarget.Date:yyyy-MM-dd} 的流水时把 {d:yyyy-MM-dd} 也弄坏了：" +
                    $"{others.Count} 条，应为 1 条（用列表整片回写就会这样）");
        }

        Report("[4] 月视图删除未波及其它日期");

        // 月视图的格子合计要跟着变，不然被删的那笔还留在格子里
        var cellSum = vm.Cells.Where(c => c.IsCurrentMonth).Sum(c => c.Total);

        if (cellSum != vm.Total)
            throw new Exception($"[4] 月视图格子合计 {cellSum} 与明细合计 {vm.Total} 不一致，删完没重算");

        // ---- 年视图也能删 ----
        vm.ShowYearCommand.Execute(null);
        for (int i = 0; i < 20; i++) Pump();
        Layout(view);
        WaitUntil(() => vm.IsYearMode);

        int yearBefore = vm.Entries.Count;

        if (yearBefore == 0)
            throw new Exception("[5] 年视图里没有条目");

        var yearTarget = vm.Entries[0];

        vm.RemoveCommand.Execute(yearTarget);
        for (int i = 0; i < 30; i++) Pump();
        WaitUntil(() => vm.Entries.Count == yearBefore - 1);
        Layout(view);

        Report($"[5] 年视图删 '{yearTarget.Description}'：{yearBefore} -> {vm.Entries.Count} 条");

        if (vm.Entries.Count != yearBefore - 1)
            throw new Exception($"[5] 年视图删完条数不对：{vm.Entries.Count}，应为 {yearBefore - 1}");

        // ---- 新增：三种粒度都要能打开面板且有确定的默认日期 ----
        foreach (var (mode, setMode) in new (string, ICommand)[]
                 {
                     ("日视图", vm.ShowDayCommand),
                     ("月视图", vm.ShowMonthCommand),
                     ("年视图", vm.ShowYearCommand)
                 })
        {
            setMode.Execute(null);
            for (int i = 0; i < 20; i++) Pump();
            Layout(view);

            if (vm.Editor.IsOpen) vm.Editor.Close();
            Pump();

            if (!vm.AddCommand.CanExecute(null))
                throw new Exception($"[6] {mode} 的新增按钮不可执行");

            vm.AddCommand.Execute(null);
            Pump();

            if (!vm.Editor.IsOpen)
                throw new Exception($"[6] {mode} 点 + 没有打开面板");

            var draft = vm.Editor.DraftDate;

            Report($"[6] {mode} 新增默认日期 = {draft:yyyy-MM-dd}");

            if (draft is null)
                throw new Exception($"[6] {mode} 的新增没有默认日期，用户不知道会记到哪一天");

            var d2 = DateOnly.FromDateTime(draft.Value);

            bool inScope = mode switch
            {
                "日视图" => d2 == today,
                "月视图" => d2.Year == today.Year && d2.Month == today.Month,
                _ => d2.Year == today.Year
            };

            if (!inScope)
                throw new Exception($"[6] {mode} 的默认日期 {d2:yyyy-MM-dd} 落在当前范围之外");

            vm.Editor.Close();
            Pump();
        }

        // ---- 导航到**别的**月 / 年再新增：默认日期要落在看到的那个范围里 ----
        // 一直在当月里测是测不出问题的：那时"锚点所在的月"与"今天的月"是同一个，
        // 用错参照也看不出来。翻走一页才区分得开。
        vm.ShowMonthCommand.Execute(null);
        for (int i = 0; i < 20; i++) Pump();

        vm.PreviousCommand.Execute(null);
        for (int i = 0; i < 30; i++) Pump();
        Layout(view);

        // 关键：**跳到当天不在的范围**去。原地翻一个月时"锚点所在的月"与"今天的月"只差一个月，
        // 任何"返回锚点"的实现都仍在范围内，断言区分不出对错。
        // 跳到两年多前，两种实现的结果就分开了：对的是 2024-01-01，错的是今天。
        var farMonth = DateOnly.FromDateTime(DateTime.Today).AddMonths(-20);
        vm.GoToForTestAsync(farMonth).GetAwaiter().GetResult();
        for (int i = 0; i < 20; i++) Pump();
        Layout(view);

        var otherMonth = farMonth;

        Report($"[8] 跳到 {vm.PeriodTitle}（当天是 {DateTime.Today:yyyy-MM}，不在这个范围里）");
        Report($"[8] 当前定位 = {otherMonth:yyyy-MM}");

        if (vm.Editor.IsOpen) vm.Editor.Close();
        vm.AddCommand.Execute(null);
        Pump();

        var draftMonth = vm.Editor.DraftDate is { } dm ? DateOnly.FromDateTime(dm) : (DateOnly?)null;

        Report($"[8] 在 {otherMonth:yyyy-MM} 里新增，默认日期 = {draftMonth:yyyy-MM-dd}");

        if (draftMonth is null)
            throw new Exception("[8] 月视图新增没有默认日期");

        if (draftMonth.Value.Year != otherMonth.Year || draftMonth.Value.Month != otherMonth.Month)
            throw new Exception(
                $"[8] 月视图的默认日期 {draftMonth:yyyy-MM-dd} 没落在用户正在看的 {otherMonth:yyyy-MM} 里，" +
                "而是用了今天所在的月 —— 保存下去会记到看不见的地方");

        // 当天不在这个月里，回退值应当是**这个月的 1 号**，不是锚点那一天
        if (draftMonth.Value.Day != 1)
            throw new Exception(
                $"[8] 月视图在当天不属于该月时，默认日期应当是当月 1 号，实际 {draftMonth:yyyy-MM-dd}");

        vm.Editor.Close();
        Pump();

        // 年视图同理
        vm.ShowYearCommand.Execute(null);
        for (int i = 0; i < 20; i++) Pump();

        DateOnly farYear = new(DateTime.Today.Year - 3, 6, 15);
        vm.GoToForTestAsync(farYear).GetAwaiter().GetResult();
        for (int i = 0; i < 20; i++) Pump();
        Layout(view);

        var otherYear = farYear.Year;

        if (vm.Editor.IsOpen) vm.Editor.Close();
        vm.AddCommand.Execute(null);
        Pump();

        var draftYear = vm.Editor.DraftDate is { } dy ? DateOnly.FromDateTime(dy) : (DateOnly?)null;

        Report($"[8] 在 {otherYear} 年里新增，默认日期 = {draftYear:yyyy-MM-dd}");

        if (draftYear is null || draftYear.Value.Year != otherYear)
            throw new Exception(
                $"[8] 年视图的默认日期 {draftYear:yyyy-MM-dd} 没落在用户正在看的 {otherYear} 年里");

        if (draftYear.Value.Month != 1 || draftYear.Value.Day != 1)
            throw new Exception(
                $"[8] 年视图在当天不属于该年时，默认日期应当是 1 月 1 日，实际 {draftYear:yyyy-MM-dd}");

        vm.Editor.Close();
        Pump();

        // ---- 删空之后列表要干净 ----
        vm.ShowDayCommand.Execute(null);
        for (int i = 0; i < 20; i++) Pump();
        Layout(view);

        while (vm.Entries.Count > 0)
        {
            vm.RemoveCommand.Execute(vm.Entries[0]);
            for (int i = 0; i < 20; i++) Pump();
        }

        Layout(view);

        Report($"[7] 删空当天：{vm.Entries.Count} 条，IsEmpty={vm.IsEmpty}，合计={vm.Total}");

        if (!vm.IsEmpty || vm.Total != 0)
            throw new Exception($"[7] 删空之后状态不对：IsEmpty={vm.IsEmpty} 合计={vm.Total}");

        DrainSaves();
        ResetBillsData();
    }

    /// <summary>
    /// 按数据找出某条流水对应的卡片元素。
    /// 自己递归而不用 CollectVisuals：这个是给"没挂进窗口的独立视图"用的，
    /// 那种视图走不了需要可视树根的那套收集。
    /// </summary>
    private static FrameworkElement? EntryCardOf(DependencyObject root, PaperCoin.Core.Models.BillEntry entry)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is Border b && b.Name == "EntryCard" && ReferenceEquals(b.DataContext, entry))
                return b;

            var found = EntryCardOf(child, entry);
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>载入某一天，等到不再变化</summary>
    private static IReadOnlyList<PaperCoin.Core.Models.BillEntry> WaitForLoadDay(
        BillRepository repo, DateOnly date)
    {
        IReadOnlyList<PaperCoin.Core.Models.BillEntry> result = [];

        WaitUntil(() =>
        {
            result = repo.LoadDayAsync(date).GetAwaiter().GetResult();
            return true;
        });

        return result;
    }

    // ---- 30. 同一名字范围内不重名 ----

    /// <summary>
    /// 一个 XAML 文件里，**同一个名字范围**内不允许出现两个同名的 x:Name。
    /// <para>
    /// 这一条守的是一个真实事故：某次构建复用了上一次失败构建残留的 <c>*.g.cs</c>，
    /// 编出来的 MainWindow 里 <c>GlassBackdrop</c> 注册了两遍，程序一启动就
    /// <c>XamlParseException</c> —— 而全部单元测试与截图测试都是绿的，因为它们
    /// 从不构造 <c>Application</c>，走的不是"点图标启动"那条路径。安装包装出来才发现。
    /// </para>
    /// <para>
    /// 直接做 XAML 解析测不出这件事：WPF 按 URI 缓存 BAML，第二次加载同一文件会复用它
    /// 已经建好的名字范围，重名不再报错（实测）。所以这里按 XAML 的规则自己走一遍 ——
    /// 模板（<c>ControlTemplate</c> / <c>DataTemplate</c> / <c>ItemsPanelTemplate</c>）各自
    /// 是一个独立的名字范围，模板之间可以重名（Controls.xaml 里有 8 个 Bd 就是合法的）。
    /// </para>
    /// </summary>
    private static void TestNoDuplicateNamesInScope()
    {
        var root = RepoRoot() ?? throw new Exception("找不到仓库根目录");
        var files = Directory.GetFiles(Path.Combine(root, "PaperCoin"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Report($"扫描 {files.Count} 个 XAML 文件");

        int checkedScopes = 0;

        foreach (var file in files)
        {
            var doc = new XmlDocument();
            doc.Load(file);

            // 外层作为一个名字范围，文档里出现的每个模板各自再开一个
            var scopes = new List<XmlNode> { doc.DocumentElement! };
            CollectTemplateNodes(doc.DocumentElement!, scopes);

            foreach (var scope in scopes)
            {
                var seen = new Dictionary<string, string>(StringComparer.Ordinal);

                // 模板节点本身**不属于**外层范围：它自己开一个范围，名字从它的子树开始算。
                // 不排掉的话，Controls.xaml 里那些各自模板内的同名元素会被误判成重名。
                foreach (var node in ScopeMembersOf(scope))
                {
                    string? name = node.Attributes?["Name"]?.Value
                        ?? node.Attributes?["x:Name"]?.Value;

                    // 命名空间前缀在 XmlDocument 里不走 Attributes["x:Name"]，单独找一遍
                    name ??= node.Attributes?.Cast<XmlAttribute>()
                        .FirstOrDefault(a => a.LocalName == "Name" && a.Prefix == "x")?.Value;

                    if (string.IsNullOrEmpty(name)) continue;

                    string line = node is IXmlLineInfo info && info.HasLineInfo()
                        ? $"第 {info.LineNumber} 行"
                        : "位置未知";

                    if (seen.TryGetValue(name, out var first))
                    {
                        throw new Exception(
                            $"{Path.GetFileName(file)} 的同一个名字范围内，" +
                            $"名称 '{name}' 注册了两遍（{first} 与 {line}）。" +
                            "这会让 XAML 在加载时抛 XamlParseException，程序一启动就崩");
                    }

                    seen[name] = line;
                }

                checkedScopes++;
            }
        }

        Report($"检查了 {checkedScopes} 个名字范围，没有重名");
    }

    /// <summary>收集文档里所有会新开名字范围的模板节点</summary>
    private static void CollectTemplateNodes(XmlNode node, List<XmlNode> into)
    {
        foreach (var child in DescendantsOf(node))
        {
            if (child.LocalName is "ControlTemplate" or "DataTemplate" or "ItemsPanelTemplate")
                into.Add(child);

            CollectTemplateNodes(child, into);
        }
    }

    /// <summary>
    /// 取该名字范围的成员：递归所有子孙，但**不越过模板边界** ——
    /// 模板会另开一个名字范围，它的子孙不算在当前范围里。
    /// </summary>
    private static IEnumerable<XmlNode> ScopeMembersOf(XmlNode node)
    {
        foreach (var child in DescendantsOf(node, stopAtTemplates: true))
            yield return child;
    }

    /// <summary>递归取所有子孙元素</summary>
    private static IEnumerable<XmlNode> DescendantsOf(XmlNode node, bool stopAtTemplates = false)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element) continue;

            yield return child;

            // 模板自己开范围，这里不再往里走
            if (stopAtTemplates && child.LocalName is "ControlTemplate" or "DataTemplate" or "ItemsPanelTemplate")
                continue;

            foreach (var deeper in DescendantsOf(child, stopAtTemplates)) yield return deeper;
        }
    }

    /// <summary>旧的递归版本（保留给模板收集用）</summary>
    private static IEnumerable<XmlNode> AllDescendantsOf(XmlNode node)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element) continue;

            yield return child;

            foreach (var deeper in DescendantsOf(child)) yield return deeper;
        }
    }

    // ---- 29. 分类明细的三个入口 ----

    /// <summary>
    /// 分类明细有三个入口：下拉框选、点排行行、点图例。三条必须等价。
    /// <para>
    /// 第 27 项只驱动了命令，绕开了"用户在选择器里选"这条路径 —— 而那条路径正是坏的：
    /// <c>SelectedItem</c> 绑 OneWay 到一个 private setter 的属性，绑定回写被静默丢弃。
    /// 所以这里**直接给 ComboBox.SelectedItem 赋值**，走真实的选择器路径。
    /// </para>
    /// <para>
    /// 顺带守住"查看全部 / 收起"：那两下会重建所有行容器，把排行条的宽度重置回 0，
    /// 没有重播的话条子颜色整片消失。
    /// </para>
    /// </summary>
    private static void TestCategoryEntryPoints()
    {
        DrainSaves();
        ResetBillsData();

        var repo = App.Services.GetRequiredService<BillRepository>();
        SeedStatisticsData(repo, DateOnly.FromDateTime(DateTime.Today));
        SeedExtraCategories(repo, DateOnly.FromDateTime(DateTime.Today));

        var window = new MainWindow
        {
            Width = WindowWidth,
            Height = WindowHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
        };

        window.Show();
        Pump();

        window.NavigateForTest("Statistics");
        Pump();
        for (int i = 0; i < 10; i++) Pump();

        var page = window.FindPageForTest("Statistics") as Statistics
            ?? throw new Exception("找不到统计页");

        if (page.DataContext is not StatisticsViewModel vm)
            throw new Exception("统计页 DataContext 不对");

        var picker = FindVisualByName<ComboBox>(page, "CategoryPicker")
            ?? throw new Exception("找不到分类下拉框");

        if (picker.Items.Count < 2)
            throw new Exception($"下拉框只有 {picker.Items.Count} 个分类，测不了");

        // ---- 入口 1：从下拉框选（真实选择器路径）----
        var wanted = (string)picker.Items[1];

        // 给 SelectedItem 赋值就等于用户在选择器里点了一下：绑定要能把它写回 ViewModel
        picker.SelectedItem = wanted;
        Pump();
        page.UpdateLayout();

        Report($"[1] 下拉框选 '{wanted}'：SelectedCategory={vm.SelectedCategory ?? "(null)"}，明细 {vm.CategoryEntries.Count} 条");
        Report($"    下拉框自身 SelectedItem={picker.SelectedItem as string ?? "(null)"}");

        if (vm.SelectedCategory != wanted)
            throw new Exception(
                $"[1] 从下拉框选没有生效：SelectedCategory={vm.SelectedCategory ?? "(null)"}，应为 {wanted}。" +
                "多半是 SelectedItem 绑定写不回去（OneWay 或 setter 不可写）");

        if (vm.CategoryEntries.Count == 0)
            throw new Exception($"[1] 从下拉框选中 '{wanted}' 之后明细是空的");

        // 明细要真的画出来，不只是数据层对
        var texts = new List<TextBlock>();
        CollectVisuals(page, texts);

        var wantedTexts = vm.CategoryEntries.Select(e => e.Description).ToList();
        int shown = wantedTexts.Count(d => texts.Any(x => x.Text == d));

        Report($"[1] 画到界面：{shown}/{wantedTexts.Count} 条");

        if (shown != wantedTexts.Count)
            throw new Exception($"[1] 从下拉框选的明细没有全部画出来：{shown}/{wantedTexts.Count}");

        // 清掉，回到未选状态
        vm.ClearCategoryCommand.Execute(null);
        Pump();
        page.UpdateLayout();

        if (vm.CategoryEntries.Count != 0)
            throw new Exception("[1] 清除后明细还在");

        // ---- 入口 2：点排行行 ----
        var rowButton = page.FindRankRowForTest()
            ?? throw new Exception("[2] 找不到排行行");

        rowButton.Command.Execute(rowButton.CommandParameter);
        Pump();
        page.UpdateLayout();

        Report($"[2] 点排行行：{vm.SelectedCategory}，明细 {vm.CategoryEntries.Count} 条");

        if (vm.CategoryEntries.Count == 0)
            throw new Exception("[2] 点排行行之后明细是空的");

        // 下拉框要跟着同步（同一个属性，两个方向都得一致）
        if (picker.SelectedItem as string != vm.SelectedCategory)
            throw new Exception(
                $"[2] 点排行行后下拉框没同步：选择器={picker.SelectedItem as string ?? "(null)"}，ViewModel={vm.SelectedCategory}");

        vm.ClearCategoryCommand.Execute(null);
        Pump();

        // ---- 入口 3：点图例 ----
        // 图例是圆环下面那组 LegendItem 按钮，没有 x:Name，按样式找
        var allButtons = new List<Button>();
        CollectVisuals(page, allButtons);

        var legendButton = allButtons.FirstOrDefault(b =>
            b.DataContext is DonutSegment && b.CommandParameter is string);

        if (legendButton is not null)
        {
            legendButton.Command.Execute(legendButton.CommandParameter);
            Pump();
            page.UpdateLayout();

            Report($"[3] 点图例：{vm.SelectedCategory}，明细 {vm.CategoryEntries.Count} 条");

            if (vm.CategoryEntries.Count == 0)
                throw new Exception("[3] 点图例之后明细是空的");

            vm.ClearCategoryCommand.Execute(null);
            Pump();
        }
        else
        {
            Report("[3] 图例没有可点的行，跳过");
        }

        // ---- 查看全部 / 收起：排行条不能因此消失 ----
        if (!vm.HasMoreRanks)
            throw new Exception("[4] 造的数据不足以出现'查看全部'，测试前提不成立");

        // 宿主里 LAE 时钟不推进，条子停在 delay 期间、宽度必然是 0，量不到几何。
        // 所以拆成两件各自可测的事：
        //   (a) 每次展开/收起之后，排行条动画**被重新启动**（用调用次数，不用完成回调数）
        //   (b) 容器重建后几何仍算得对（落终值再量）
        int collapsedRows = page.RankRowCountForTest;
        int runsBeforeExpand = page.RankBarsRunCount;

        page.SettleRankBarsForTest();
        page.UpdateLayout();

        var collapsedWidths = page.RankBarWidthsForTest;
        Report($"[4] 收起态：{collapsedRows} 行，条宽 [{string.Join(", ", collapsedWidths.Select(w => w.ToString("0.#")))}]");

        if (collapsedWidths.Count == 0 || collapsedWidths.All(w => w <= 0))
            throw new Exception("[4] 落终值后收起态的排行条宽度仍是 0，几何算错了");

        vm.ShowAllCategoriesCommand.Execute(null);
        Pump();
        for (int i = 0; i < 10; i++) Pump();
        page.UpdateLayout();

        int expandedRows = page.RankRowCountForTest;

        Report($"[5] 查看全部：{collapsedRows} -> {expandedRows} 行，" +
               $"排行条动画启动次数 {runsBeforeExpand} -> {page.RankBarsRunCount}");

        if (expandedRows <= collapsedRows)
            throw new Exception($"[5] 查看全部之后行数没增加：{collapsedRows} -> {expandedRows}");

        // 这是本轮的真 bug：容器重建把 RankBar 的宽度重置回模板里的 0，不重播就整片没颜色
        if (page.RankBarsRunCount <= runsBeforeExpand)
            throw new Exception(
                "[5] 查看全部之后排行条动画没有重新启动 —— 容器重建把宽度重置回 0 了，" +
                "没有重播的话前景条宽度一直是 0，看起来就是颜色没了");

        page.SettleRankBarsForTest();
        page.UpdateLayout();

        var expandedWidths = page.RankBarWidthsForTest;
        Report($"[5] 落终值后条宽 [{string.Join(", ", expandedWidths.Select(w => w.ToString("0.#")))}]");

        if (expandedWidths.Count != expandedRows)
            throw new Exception($"[5] 落了 {expandedWidths.Count} 条，但有 {expandedRows} 行");

        if (expandedWidths.All(w => w <= 0))
            throw new Exception("[5] 展开后所有条宽都是 0，几何算错了");

        // 背景条是"整行那层淡色"，它必须铺满才有底色
        var tracks = page.RankBarTrackWidthsForTest;
        Report($"[5] 背景条宽度 {tracks.Count} 条，示例 [{string.Join(", ", tracks.Take(3).Select(w => w.ToString("0.#")))}]");

        if (tracks.Count != expandedRows)
            throw new Exception($"[5] 背景条 {tracks.Count} 条，应与 {expandedRows} 行一一对应");

        if (tracks.Any(w => w <= 0))
            throw new Exception("[5] 有背景条宽度是 0，那一行连淡色底都没有");

        int runsBeforeCollapse = page.RankBarsRunCount;

        vm.ShowTopCategoriesCommand.Execute(null);
        Pump();
        for (int i = 0; i < 10; i++) Pump();
        page.UpdateLayout();

        Report($"[6] 收起：{expandedRows} -> {page.RankRowCountForTest} 行，" +
               $"排行条动画启动次数 {runsBeforeCollapse} -> {page.RankBarsRunCount}");

        if (page.RankRowCountForTest != collapsedRows)
            throw new Exception($"[6] 收起之后行数不对：{page.RankRowCountForTest}，应为 {collapsedRows}");

        if (page.RankBarsRunCount <= runsBeforeCollapse)
            throw new Exception("[6] 收起之后排行条动画没有重新启动，颜色又没了");

        page.SettleRankBarsForTest();
        page.UpdateLayout();

        var backWidths = page.RankBarWidthsForTest;
        Report($"[6] 落终值后条宽 [{string.Join(", ", backWidths.Select(w => w.ToString("0.#")))}]");

        if (backWidths.Count != collapsedRows || backWidths.All(w => w <= 0))
            throw new Exception($"[6] 收起后条数 {backWidths.Count}（应为 {collapsedRows}）或宽度不对");

        window.Close();
        Pump();

        DrainSaves();
        ResetBillsData();
    }

    // ---- 28. 液态玻璃的背部模糊 ----

    /// <summary>
    /// 真背部模糊由 DWM 系统背板提供，而系统背板只对**非分层窗口**生效。
    /// 这一项守两件事：窗口分层与否的判定对不对，以及背板属性是不是真的设下去了。
    /// <para>
    /// 模糊本身在测试宿主里看不见（那要有真实桌面在窗口背后），所以这里断言的是
    /// "配置正确"而不是"画面对"。画面对不对只能靠截图与实机确认。
    /// </para>
    /// </summary>
    private static void TestBackdropBlur()
    {
        Report($"系统背板可用 = {BackdropBlur.IsSupported}（Windows 11 build 22000 起）");

        // 判定的两个输入：本机支不支持 + 保存的风格是不是液态玻璃
        Report($"  SavedStyleNeedsLayeredWindow(支持={true})  -> {ThemeManager.SavedStyleNeedsLayeredWindow(true)}");
        Report($"  SavedStyleNeedsLayeredWindow(支持={false}) -> {ThemeManager.SavedStyleNeedsLayeredWindow(false)}");

        // 不支持系统背板时必须退回分层窗口：那时液态玻璃用不透明材质，
        // 走非分层只会白白丢掉圆角与阴影
        if (!ThemeManager.SavedStyleNeedsLayeredWindow(false))
            throw new Exception("本机不支持系统背板时仍判定为非分层窗口，会白白丢掉圆角与阴影");

        // 判定读的是磁盘配置，所以要显式写两种风格各验一遍，
        // 不能假定"当前配置恰好是经典" —— 那样只会顺带测出别的项有没有还原干净。
        var config = new ConfigService();
        var saved = config.GetAppConfig().App.ThemeStyle;

        try
        {
            foreach (var (style, expectLayered) in new[]
                     {
                         (ThemeManager.StyleClassic, true),
                         (ThemeManager.StyleWindows, true),
                         (ThemeManager.StyleLiquid, false)
                     })
            {
                var cfg = config.GetAppConfig();
                cfg.App.ThemeStyle = style;
                config.SaveAppConfig(cfg);

                // 让 ThemeManager 走一次真实的加载路径，顺便确认它没把值改写掉
                _ = new ThemeManager(config);

                bool layered = ThemeManager.SavedStyleNeedsLayeredWindow(true);
                Report($"  风格={style,-8} -> 分层窗口={layered}（应为 {expectLayered}）");

                if (layered != expectLayered)
                    throw new Exception($"风格 {style} 的窗口分层判定是 {layered}，应为 {expectLayered}");
            }
        }
        finally
        {
            var cfg = config.GetAppConfig();
            cfg.App.ThemeStyle = saved;
            config.SaveAppConfig(cfg);

            // 写完立刻回读：这个文件在测试里被多处写，不核对的话污染会以
            // "后面某一项莫名失败"的形式出现，很难定位到源头
            var check = new ConfigService().GetAppConfig().App;
            Report($"  第 28 项收尾：Theme={check.Theme} ThemeStyle={check.ThemeStyle}（还原为 {saved}）");

            if (check.ThemeStyle != saved)
                throw new Exception($"第 28 项没能还原 ThemeStyle：{check.ThemeStyle} != {saved}");
        }

        // 真去设一次背板，确认 P/Invoke 通、属性被接受
        var window = new Window
        {
            Width = 320,
            Height = 240,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,
            Background = Brushes.Black,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Content = new Grid()
        };

        window.Show();
        Pump();

        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Report($"  非分层窗口句柄 = 0x{handle.ToInt64():X}");

        if (handle == IntPtr.Zero)
            throw new Exception("拿不到窗口句柄");

        if (!BackdropBlur.IsSupported)
        {
            Report("  本机不支持系统背板，跳过背板属性断言");
            window.Close();
            Pump();
            return;
        }

        bool applied = BackdropBlur.Apply(handle, dark: true);
        Report($"  Apply -> {applied}，类型={BackdropBlur.AppliedBackdropType}，色调=0x{BackdropBlur.AppliedTint:X8}");

        if (!applied)
            throw new Exception("DWM 拒绝了系统背板，液态玻璃拿不到背部模糊");

        // 3 = Acrylic（TransientWindow），4 = Mica Alt，2 = Mica
        if (BackdropBlur.AppliedBackdropType is not (2 or 3 or 4))
            throw new Exception($"背板类型是 {BackdropBlur.AppliedBackdropType}，不是已知的系统背板");

        // DWM 色调只是"能设就设"：部分 Windows 11 版本会拒（本机 build 26300 就拒）。
        // 明度由 ColorGlassScrim 保证，所以这里只如实报告，不据此判定失败。
        Report($"  DWM 色调：接受={BackdropBlur.AppliedTintAccepted}（被拒不等于玻璃不可用，明度看 scrim）");

        // 但 scrim 必须在：它才是明度的唯一保证
        foreach (var name2 in new[] { "DarkLiquid", "BrightLiquid" })
        {
            var scrim = (LoadDict($"/Themes/{name2}.xaml")["ColorGlassScrim"] as SolidColorBrush)?.Color
                ?? throw new Exception($"{name2} 缺少 ColorGlassScrim");

            Report($"  {name2} scrim = {Hex(scrim)}（A={scrim.A}）");

            if (scrim.A < 0x66)
                throw new Exception($"{name2} 的 scrim 太淡（A={scrim.A}），压不住模糊结果，白字会读不清");
        }

        // 分级结构：测试宿主里的窗口只用来验属性，验完就收
        BackdropBlur.Clear(handle);
        Report($"  Clear -> 类型={BackdropBlur.AppliedBackdropType}");

        if (BackdropBlur.AppliedBackdropType != 1)
            throw new Exception("Clear 之后背板类型没有回到 None");

        window.Close();
        Pump();
    }

    // ---- 27. 分类明细从真实入口走通 ----

    /// <summary>
    /// 按用户的真实操作路径把分类明细走一遍：进统计页 → 点排行行 → 看明细列表。
    /// <para>
    /// 第 24 项是"在已经就绪的页面上驱动命令"，绕开了进场时序；这一项刻意从建窗口开始，
    /// 每一步都断言，好把"哪一环断了"直接定位出来，而不是只报一个"没有明细"。
    /// </para>
    /// </summary>
    private static void TestCategoryDetailEndToEnd()
    {
        DrainSaves();
        ResetBillsData();

        var repo = App.Services.GetRequiredService<BillRepository>();
        var expected = SeedStatisticsData(repo, DateOnly.FromDateTime(DateTime.Today));

        var window = new MainWindow
        {
            Width = WindowWidth,
            Height = WindowHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
        };

        window.Show();
        Pump();

        // 1. 进统计页
        window.NavigateForTest("Statistics");
        Pump();
        for (int i = 0; i < 10; i++) Pump();

        if (window.FindPageForTest("Statistics") is not Statistics page)
            throw new Exception("[1] 统计页没挂上");

        if (page.DataContext is not StatisticsViewModel vm)
            throw new Exception("[1] 统计页 DataContext 不对");

        Report($"[1] 进页面：圆环 {vm.Segments.Count} 段，排行 {vm.RankRows.Count} 行，分类清单 {vm.Categories.Count} 项");

        if (vm.RankRows.Count == 0)
            throw new Exception("[1] 排行是空的，造的数据没进去");

        // 2. 点排行第一行（走按钮上真正绑定的那个命令，不自己调 VM）
        var rowButton = page.FindRankRowForTest()
            ?? throw new Exception("[2] 找不到排行行按钮");

        if (rowButton.Command is null)
            throw new Exception("[2] 排行行按钮没有绑定命令");

        var label = rowButton.CommandParameter as string
            ?? throw new Exception("[2] 排行行按钮的参数不是分类名");

        Report($"[2] 点排行行：分类='{label}'，可执行={rowButton.Command.CanExecute(rowButton.CommandParameter)}");

        rowButton.Command.Execute(rowButton.CommandParameter);
        Pump();
        for (int i = 0; i < 6; i++) Pump();

        // 3. 选中状态
        if (vm.SelectedCategory != label)
            throw new Exception($"[3] 选中没生效：SelectedCategory={vm.SelectedCategory ?? "(null)"}，应为 {label}");

        Report($"[3] 选中生效：{vm.SelectedCategory}，HasCategorySelection={vm.HasCategorySelection}");

        // 4. 明细数据
        if (vm.CategoryEntries.Count == 0)
            throw new Exception(
                $"[4] 明细数据是空的。该分类在全部时间里应当有 " +
                $"{expected.AllEntryCount} 条里的若干条；检查 AllEntries 有没有被载入");

        Report($"[4] 明细数据：{vm.CategoryEntries.Count} 条 —— {vm.CategorySummaryText}");

        // 5. 明细画到界面上
        page.UpdateLayout();

        var texts = new List<TextBlock>();
        CollectVisuals(page, texts);

        var wanted = vm.CategoryEntries.Select(e => e.Description).ToList();
        var shown = wanted.Where(d => texts.Any(t => t.Text == d)).ToList();

        Report($"[5] 画到界面：{shown.Count}/{wanted.Count} 条 —— {string.Join(", ", shown)}");

        if (shown.Count != wanted.Count)
            throw new Exception($"[5] 明细没有全部画出来：{shown.Count}/{wanted.Count}");

        // 6. 下拉框也要跟着选中
        var picker = FindVisualByName<ComboBox>(page, "CategoryPicker")
            ?? throw new Exception("[6] 找不到分类下拉框");

        if (picker.SelectedItem as string != label)
            throw new Exception($"[6] 下拉框没跟着选中：{(picker.SelectedItem as string) ?? "(null)"}，应为 {label}");

        Report($"[6] 下拉框选中：{picker.SelectedItem}");

        // 7. 清掉之后明细要收起来
        vm.ClearCategoryCommand.Execute(null);
        Pump();
        page.UpdateLayout();

        if (vm.CategoryEntries.Count != 0)
            throw new Exception($"[7] 清除后明细数据还在：{vm.CategoryEntries.Count} 条");

        Report("[7] 清除后明细已收起");

        // 8. 从下拉框选另一个分类也要能出明细
        if (vm.Categories.Count > 1)
        {
            var other = vm.Categories[1];
            vm.ShowCategoryCommand.Execute(other);
            Pump();
            page.UpdateLayout();

            if (vm.CategoryEntries.Count == 0)
                throw new Exception($"[8] 从命令选 '{other}' 之后明细是空的");

            Report($"[8] 换到 '{other}'：{vm.CategoryEntries.Count} 条");
        }

        window.Close();
        Pump();

        DrainSaves();
        ResetBillsData();
    }

    // ---- 23. 圆环几何 ----

    /// <summary>
    /// 弧线的角度必须与占比成正比。刻意选"不平均"的占比（50/30/20），
    /// 因为平均分配的实现也能通过等分数据的断言。
    /// </summary>
    private static void TestDonutGeometry()
    {
        var segments = new List<DonutSegment>
        {
            new() { Label = "A", Amount = 50m, Fraction = 0.5, ColorKey = "CategoryColor01", Count = 1 },
            new() { Label = "B", Amount = 30m, Fraction = 0.3, ColorKey = "CategoryColor02", Count = 1 },
            new() { Label = "C", Amount = 20m, Fraction = 0.2, ColorKey = "CategoryColor03", Count = 1 }
        };

        var window = new Window
        {
            Width = 200,
            Height = 200,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
        };

        var chart = new PaperCoin.Controls.DonutChart
        {
            Width = 150,
            Height = 150,
            RingThickness = 26,
            ItemsSource = segments
        };

        window.Content = chart;
        window.Show();
        Pump();
        chart.UpdateLayout();

        var sweeps = chart.SegmentSweeps;
        Report($"诊断: ActualSize={chart.ActualWidth:0.#}×{chart.ActualHeight:0.#} " +
               $"VisualChildren={VisualTreeHelper.GetChildrenCount(chart)} RenderSize={chart.RenderSize.Width:0.#}×{chart.RenderSize.Height:0.#}");
        Report($"诊断: {chart.LastRebuildDiagnostic}");
        Report("弧角(度): " + string.Join(", ", sweeps.Select(s => (s * 180 / Math.PI).ToString("0.0"))));

        if (sweeps.Count != 3)
            throw new Exception($"应画出 3 段弧，实际 {sweeps.Count}");

        // 留缝从每段末尾扣掉，所以总角度略小于 360；用比例而不是绝对值断言
        double total = sweeps.Sum();

        if (total <= 0 || total > Math.PI * 2 + 0.001)
            throw new Exception($"弧角总和异常: {total:0.###} 弧度");

        double[] share = [sweeps[0] / total, sweeps[1] / total, sweeps[2] / total];

        Report($"归一化占比: {share[0]:P1} / {share[1]:P1} / {share[2]:P1}");

        if (Math.Abs(share[0] - 0.5) > 0.02 || Math.Abs(share[1] - 0.3) > 0.02 || Math.Abs(share[2] - 0.2) > 0.02)
            throw new Exception("扇区角度与占比不符（可能是平均分配）");

        // 每一段都得真的拿到画刷：颜色是 DynamicResource，键写错时静默无描边
        var paths = new List<System.Windows.Shapes.Path>();
        CollectVisuals(chart, paths);

        if (paths.Count != 3)
            throw new Exception($"可视子元素里的弧线数不是 3，实际 {paths.Count}");

        foreach (var path in paths)
        {
            if (path.Stroke is not SolidColorBrush brush || brush.Color.A == 0)
                throw new Exception("弧线没有解析出描边画刷，分类颜色键在主题字典里找不到");
        }

        chart.RevealAll();
        Pump();

        if (!LAE.LAEngine.IsRunning("StatisticsDonut"))
            throw new Exception("圆环展开没有向 LAE 注册（动效没接上）");

        // 几何算对不代表画得出来：描边宽度为 0、画刷透明、笔宽为 0 都会让它"存在但看不见"。
        // 这里直接渲染出来数像素 —— 沿圆心那条横带扫一遍，数明显偏离背景色的像素。
        chart.SnapToRevealed();
        Pump();
        chart.UpdateLayout();

        int colored = 0;
        for (int y = 60; y < 90; y++)
        {
            for (int x = 0; x < 150; x++)
            {
                var pixel = PixelAt(chart, 150, 150, x, y);
                if (pixel.R < 200 || pixel.G < 200 || pixel.B < 200) colored++;
            }
        }

        Report($"圆心横带上的非背景像素 = {colored}");

        if (colored < 40)
            throw new Exception($"圆环渲染出来是空的（只有 {colored} 个非背景像素），弧线没画到界面上");

        // 用一个**真实窗口里**的宿主来取色：DonutChart 的弧线是用 AddVisualChild 挂上去的，
        // 控件必须挂在活的渲染树上才会被画出来 —— 游离的 Grid（不 Show）渲染出来永远是空的，
        // 那会得出"环没画出来"的错误结论。
        var proofHost = new Grid { Width = 200, Height = 200, Background = Brushes.White };
        proofHost.Children.Add(new PaperCoin.Controls.DonutChart
        {
            Width = 200,
            Height = 200,
            RingThickness = 26,
            AnimationFrozen = true,
            ItemsSource = segments
        });

        var proofWindow = new Window
        {
            Width = 200,
            Height = 200,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            Content = proofHost
        };

        proofWindow.Show();
        Pump();
        proofHost.UpdateLayout();

        // 半径 87、笔宽 26：环体在半径 74~100 之间，到圆心 74 之内是空的。
        // 取 12 点方向（环上）与正中（洞里）两点，断言前者有色、后者是底色。
        var onRing = PixelAt(proofHost, 200, 200, 100, 15);
        var inHole = PixelAt(proofHost, 200, 200, 100, 100);

        Report($"环上像素(100,15) = #{onRing.R:X2}{onRing.G:X2}{onRing.B:X2}");
        Report($"洞中像素(100,100) = #{inHole.R:X2}{inHole.G:X2}{inHole.B:X2}");

        // 弧线是自绘控件自己挂上去的可视子元素。挂上去不等于画得出来：
        // 没有被 Arrange 过的子元素 RenderSize 是 0，渲染时会被直接跳过。
        var ringPaths = new List<System.Windows.Shapes.Path>();
        CollectVisuals(proofHost, ringPaths);

        foreach (var p in ringPaths)
        {
            var bounds = p.Data?.Bounds ?? Rect.Empty;
            Report($"  弧线 RenderSize={p.RenderSize.Width:0.#}×{p.RenderSize.Height:0.#} " +
                   $"几何Bounds={bounds.Width:0.#}×{bounds.Height:0.#} 笔宽={p.StrokeThickness:0.#}");
        }

        proofWindow.Close();
        Pump();

        if (onRing.R > 240 && onRing.G > 240 && onRing.B > 240)
            throw new Exception("12 点方向应当落在环体上，取到的却是白底 —— 环没有画出来");

        if (inHole.R < 240 || inHole.G < 240 || inHole.B < 240)
            throw new Exception($"圆心应当是空的，取到的却是 #{inHole.R:X2}{inHole.G:X2}{inHole.B:X2} —— 画成了实心饼");

        window.Close();
        Pump();
    }

    // ---- 24. 统计页接入导航 ----

    /// <summary>
    /// 统计页必须在真实窗口里挂得上、量得出、切得动，并且展开动效真的向 LAE 注册过。
    /// 侧边栏第四个按钮的指示器定位也在这里复核：它是按按钮布局算的，加一项后不该失准。
    /// </summary>
    private static void TestStatisticsPage()
    {
        DrainSaves();
        ResetBillsData();

        var repo = App.Services.GetRequiredService<BillRepository>();
        var expected = SeedStatisticsData(repo, DateOnly.FromDateTime(DateTime.Today));

        var window = new MainWindow
        {
            Width = WindowWidth,
            Height = WindowHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000
        };

        window.Show();
        Pump();

        window.NavigateForTest("Statistics");
        Pump();

        if (window.FindPageForTest("Statistics") is not Statistics page)
            throw new Exception("Statistics 页面未挂载到内容区");

        if (page.DataContext is not StatisticsViewModel vm)
            throw new Exception("Statistics 页面的 DataContext 不是 StatisticsViewModel");

        if (vm.Segments.Count == 0)
            throw new Exception("统计页没有聚合出任何圆环分段（数据是有的）");

        if (vm.TotalExpense != expected.MonthExpense)
            throw new Exception(
                $"页面聚合出的本月支出应为 {expected.MonthExpense}，实际 {vm.TotalExpense}");

        Report($"圆环 {vm.Segments.Count} 段, 排行 {vm.RankRows.Count} 行, 趋势 {vm.TrendBars.Count} 根");

        // 布局体检：缺 StaticResource / 模板名字域解析失败时控件会静默塌成 0 尺寸
        AssertNoCollapse(page, "Statistics");

        // 展开动效必须真的注册到 LAE。这里用 IsRunning 而不是计数：
        // 测试进程没有渲染循环，LAE 时钟不推进，计数要等 OnComplete 才涨。
        if (!LAE.LAEngine.IsRunning(Statistics.RevealAnimationName + "_Rank"))
            throw new Exception("排行条展开没有向 LAE 注册（动效没接上）");

        // 圆环的弧线要能在真实窗口里解析出画刷
        var paths = new List<System.Windows.Shapes.Path>();
        CollectVisuals(page, paths);

        if (paths.Count == 0)
            throw new Exception("统计页里没有画出任何弧线");

        // 侧边栏四个按钮的指示器定位。
        // 注意：这一次点击会重新走一遍 NavigateTo，而 ViewModel 是按 Transient 注册的，
        // 页面实例却被 MainWindow 长期缓存 —— 于是缓存页面会拿到**新的** ViewModel。
        // 因此下面所有针对 ViewModel 的状态断言都必须用这一次点击之后的实例，
        // 用点击前抓到的那个会永远对不上（命令与状态都已经是另一份了）。
        window.NavButtonForTest("Statistics").RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump();

        if (page.DataContext is not StatisticsViewModel live)
            throw new Exception("重新导航后 Statistics 页面的 DataContext 不是 StatisticsViewModel");

        SettleIndicator(window);
        double statsY = window.IndicatorY;
        Report($"统计项指示器 Y={statsY:0.#}");

        if (Math.Abs(statsY) < 0.5)
            throw new Exception("统计项的指示器没有离开第一项的位置");

        window.NavButtonForTest("TodayBill").RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump();

        SettleIndicator(window);

        if (Math.Abs(window.IndicatorY - statsY) < 1)
            throw new Exception("四个导航项下指示器定位失准：首项与统计项落在同一位置");

        // 回到统计页取当前这一份实例
        window.NavigateForTest("Statistics");
        Pump();

        if (page.DataContext is not StatisticsViewModel current)
            throw new Exception("回到统计页后 DataContext 不是 StatisticsViewModel");

        Report($"当前实例: 圆环 {current.Segments.Count} 段, 排行 {current.RankRows.Count} 行");

        // 点排行的任意一行应当下钻
        if (page.FindRankRowForTest() is not { } row)
            throw new Exception("找不到排行行按钮，下钻入口没接上");

        // 行按钮必须真的绑上了命令：模板里漏掉 Command 时点击毫无反应且不报错
        if (row.Command is null)
            throw new Exception("排行行按钮没有绑定命令（下钻点击会静默失效）");

        // 绑定拿到的必须是本页 ViewModel 上那个命令实例。绑到别的 DataContext 上时
        // 点击照样没反应，而且按钮的 Command 非空，看不出问题。
        if (!ReferenceEquals(row.Command, current.ShowCategoryCommand))
            throw new Exception(
                $"排行行按钮绑定的命令不是本页的命令实例: {row.Command.GetType().Name} / {current.ShowCategoryCommand.GetType().Name}");

        // 先验证绑定接线：命令实例、命令参数、可执行性。
        // 这一段替换了"合成一次点击"的写法 —— Button 的 Click 不是 ClickEvent 的类处理器
        // （它由 OnClick 直接触发），手工 RaiseEvent(ClickEvent) 根本走不到命令，
        // 那样的断言既测不到东西，又会把失败指向错误的方向。
        // 从"抬键"到"OnClick"这一段是 WPF 自己的机制，不需要在这里重测。
        if (row.CommandParameter is not string boundLabel)
            throw new Exception("排行行按钮的 CommandParameter 不是分类名");

        var boundRow = current.VisibleRankRows.FirstOrDefault(r => r.Label == boundLabel)
            ?? throw new Exception($"排行行按钮绑定的分类 {boundLabel} 不在当前列表里");

        if (!row.Command.CanExecute(boundLabel))
            throw new Exception("排行行按钮的命令对绑定参数不可执行，点击会是静默失效");

        Report($"行按钮接线: 数据={boundRow.Label}, 参数={boundLabel}, 可执行=True");

        // 驱动命令，确认它真的能选中分类（这是点击最终要走的动作）
        current.ShowCategoryCommand.Execute(boundLabel);
        Pump();

        if (current.SelectedCategory != boundLabel)
            throw new Exception("执行 ShowCategoryCommand 没有选中分类");

        if (current.CategoryEntries.Count == 0)
            throw new Exception("选中分类后明细列表是空的（该分类明明有流水）");

        Report($"分类明细已展开: {current.CategoryDetailTitle}, {current.CategorySummaryText}");

        // 明细必须落在不需要滚动就能看到的位置。
        // 它原来排在页面最下面（在排行的下面），点完排行后它其实更新了，
        // 却在屏幕外，用户只会觉得"点了没反应"。现在它紧跟圆环、排在排行之前。
        Pump();
        page.UpdateLayout();

        double viewportTop = 0;
        double viewportBottom = page.DetailViewportHeightForTest;

        var detailTop = page.CategoryDetailOffsetForTest;

        Report($"分类明细区块纵坐标 = {detailTop:0.#}，首屏可视高度 = {viewportBottom:0.#}");

        if (detailTop > viewportBottom)
            throw new Exception(
                $"分类明细在首屏之外（纵坐标 {detailTop:0.#} > 可视高度 {viewportBottom:0.#}），" +
                "点完排行看不到它，等于没生效");

        current.ClearCategoryCommand.Execute(null);
        Pump();

        if (current.HasCategorySelection)
            throw new Exception("清除后仍然有选中的分类");

        // 明细必须真的画到界面上，不只是 ViewModel 里有数据。
        // 绑定写错、可见性没传播、行高为 0 都会造成"数据有、界面空"，而这几种都不报错。
        current.ShowCategoryCommand.Execute(boundLabel);
        Pump();
        page.UpdateLayout();

        if (current.CategoryEntries.Count == 0)
            throw new Exception("选中分类后明细数据是空的");

        var pageTexts = new List<TextBlock>();
        CollectVisuals(page, pageTexts);

        var descriptions = current.CategoryEntries
            .Select(e => e.Description)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToList();

        int shown = descriptions.Count(d => pageTexts.Any(t => t.Text == d));

        Report($"分类明细：数据 {descriptions.Count} 条，出现在可视树上的 {shown} 条");

        if (shown == 0)
            throw new Exception("分类明细一条都没画到界面上（数据有但界面空）");

        // 下拉框里选中的分类名必须是主题前景色，不能是默认黑
        var picker = FindVisualByName<ComboBox>(page, "CategoryPicker")
            ?? throw new Exception("找不到分类下拉框 CategoryPicker");

        var pickerTexts = new List<TextBlock>();
        CollectVisuals(picker, pickerTexts);

        var shownLabel = pickerTexts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text))
            ?? throw new Exception("下拉框里没有任何文本，选中的分类名没画出来");

        var color = (shownLabel.Foreground as SolidColorBrush)?.Color ?? Colors.Black;
        Report($"下拉框文本 '{shownLabel.Text}' 颜色 = #{color.R:X2}{color.G:X2}{color.B:X2}");

        // 暗色主题的正文色是 #F0F4FA；接近黑色说明 ContentPresenter 没把前景色传下去
        if (color.R < 0x40 && color.G < 0x40 && color.B < 0x40)
            throw new Exception(
                $"下拉框选中项文本是黑色（#{color.R:X2}{color.G:X2}{color.B:X2}），暗色主题下几乎看不见");

        // ---- 每次进场都要重播入场动画 ----
        // 统计页实例被 MainWindow 缓存，切走再切回不会重建控件。展开状态如果不复位，
        // 第二次进场时圆环、排行条、趋势柱都已经在收尾值上，动画等于没播。
        AssertStatisticsReplaysOnEntry(window, page);

        window.Close();
        Pump();

        DrainSaves();
        ResetBillsData();
    }

    /// <summary>
    /// 切走再切回统计页，断言三块图表的展开动画都重播了。
    /// 只看"播放计数涨了"：本进程里 LAE 时钟不推进，动画会在第一次 Update 时直接落到终点，
    /// 但计数（OnComplete 里的回调）照样会涨，所以它是可靠的信号。
    /// </summary>
    private static void AssertStatisticsReplaysOnEntry(MainWindow window, Statistics page)
    {
        Report("--- 切走再切回，检查入场动画是否重播 ---");

        // 记下上一轮的计数，用来反向确认进场时确实被清零了
        int rankBefore = page.RankBarsPlayCount;
        int trendBefore = page.TrendBarsPlayCount;
        int roundsBefore = page.RevealRounds;

        // 切到别的页面再切回来
        window.NavigateForTest("Settings");
        Pump();

        window.NavigateForTest("Statistics");
        Pump();
        SettleIndicator(window);

        // 展开是排在 Render / Loaded 优先级的，多泵几轮等它跑完
        for (int i = 0; i < 8; i++) Pump();

        if (page.RevealRounds <= roundsBefore)
            throw new Exception($"第二次进场没有开始新一轮展开（轮数 {roundsBefore} -> {page.RevealRounds}）");

        Report($"  展开轮数 {roundsBefore} -> {page.RevealRounds}");
        Report($"  圆环 本轮播完 {page.DonutPlayCount} / 共 {page.DonutSegmentCountForTest} 段");
        Report($"  排行条 本轮播完 {page.RankBarsPlayCount}");
        Report($"  趋势柱 本轮播完 {page.TrendBarsPlayCount}");
        Report($"  排行条 {rankBefore} -> {page.RankBarsPlayCount}");
        Report($"  趋势柱 {trendBefore} -> {page.TrendBarsPlayCount}");

        // 这三个计数都是"本轮进度"：进场时由 Reset 清零，动画每完成一个元素加一。
        // 所以第二次进场后它们必须重新涨满 —— 停在 0 说明压根没播，停在上一轮的值
        // 说明连清零都没发生（那就等于没重播）。
        if (page.DonutPlayCount != page.DonutSegmentCountForTest || page.DonutSegmentCountForTest == 0)
            throw new Exception(
                $"第二次进统计页时圆环没有重播展开动画（本轮播完 {page.DonutPlayCount} / 共 {page.DonutSegmentCountForTest} 段）");

        if (page.RankBarsPlayCount == 0)
            throw new Exception("第二次进统计页时排行条没有重播展开动画（本轮播完 0 条）");

        if (page.TrendBarsPlayCount == 0)
            throw new Exception("第二次进统计页时趋势柱没有重播展开动画（本轮播完 0 根）");

        // 反向确认：上一轮的计数必须已经被清掉，否则上面的"== 段数"可能只是上一轮的残留
        if (rankBefore == 0 || trendBefore == 0)
            throw new Exception("第一次进场时排行条/趋势柱就没播过，测试前提不成立");
    }

    // ---- 25. 统计 CSV ----

    /// <summary>
    /// 导出的 CSV 必须带 UTF-8 BOM。没有 BOM 时 Excel 按本地代码页解释，
    /// 中文分类与描述全是乱码 —— 这种故障只在 Excel 里才看得见，所以必须在这里挡住。
    /// </summary>
    private static void TestStatisticsCsv()
    {
        DrainSaves();
        ResetBillsData();

        // 用**自己新建**的仓库，不用容器里的单例：单例的内存索引会跨测试留着
        // 前面几项造过的数据（磁盘被清掉也不会退回去），拿它做条数断言会受别的测试影响。
        var repo = NewRepository();
        var today = DateOnly.FromDateTime(DateTime.Today);

        var expected = SeedStatisticsData(repo, today);

        var csvFolder = Path.Combine(PaperCoin.Core.Constants.AppDataFolder, "Export");
        var before = Directory.Exists(csvFolder)
            ? Directory.GetFiles(csvFolder, "*.csv").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var vm = new StatisticsViewModel(repo);
        WaitFor(vm.ReloadNowAsync());

        // 导出的是**当前范围**的流水。这里切到"全部"，好让导出条数、造数时算出的
        // expected.AllEntryCount、以及页面自己统计的笔数三者可比。
        vm.ShowAllCommand.Execute(null);
        WaitFor(vm.ReloadNowAsync());

        vm.ExportCsvCommand.Execute(null);
        Pump();

        if (!vm.HasNotice)
            throw new Exception("导出后没有任何提示，用户不知道结果");

        Report(vm.Notice);

        var created = Directory.GetFiles(csvFolder, "*.csv")
            .Where(f => !before.Contains(f))
            .ToList();

        if (created.Count != 1)
            throw new Exception($"本次导出应产生 1 个文件，实际 {created.Count}");

        var path = created[0];

        try
        {
            var bytes = File.ReadAllBytes(path);

            if (bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF)
                throw new Exception("CSV 缺少 UTF-8 BOM，Excel 打开会是乱码");

            var text = File.ReadAllText(path);

            // 按行拆开后先把纯空白行滤掉：明细段与汇总段之间有一行分隔，
            // 它在 \n 上拆出来只剩一个 \r，RemoveEmptyEntries 不会去掉它。
            // 用行数做减法容易被这种空行带偏，所以改成**直接数明细行**：
            // 明细行的首字段是 yyyy-MM-dd，汇总行是"支出合计"这类中文标题。
            var lines = text
                .Split('\n')
                .Select(l => l.TrimEnd('\r').Trim())
                .Where(l => l.Length > 0)
                .ToList();

            var detailLines = lines
                .Where(l => l.Length >= 10 && DateOnly.TryParse(l[..10], out _))
                .ToList();

            Report($"CSV {bytes.Length} 字节, 非空行 {lines.Count}, 明细 {detailLines.Count} 行, 首行: {lines[0]}");

            if (!lines[0].Contains("日期") || !lines[0].Contains("金额"))
                throw new Exception($"表头不对: {lines[0]}");

            // 明细条数必须正好等于导出覆盖的范围里的流水条数
            if (detailLines.Count != expected.AllEntryCount)
                throw new Exception(
                    $"CSV 明细行应为 {expected.AllEntryCount}（全部范围的流水条数），实际 {detailLines.Count}。" +
                    "实际内容: " + string.Join(" / ", detailLines));

            if (detailLines.Count != vm.EntryCount)
                throw new Exception($"CSV 明细行 {detailLines.Count} 与页面统计的笔数 {vm.EntryCount} 不一致");

            if (!text.Contains("餐饮"))
                throw new Exception("CSV 里没有分类内容");

            if (!text.Contains("支出合计"))
                throw new Exception("CSV 里没有汇总段");

            if (!vm.Notice.Contains(path))
                throw new Exception("导出提示里没有文件路径，用户找不到文件");
        }
        finally
        {
            // 导出目录是用户可见的产物目录，测试不留垃圾
            foreach (var file in created) File.Delete(file);
        }

        DrainSaves();
        ResetBillsData();
    }

    /// <summary>
    /// 造一套跨两个月的统计数据，供统计口径、CSV 与页面三项共用，并**算出**各范围的期望值。
    /// </summary>
    /// <remarks>
    /// 期望值由造数这一端算出来、而不是在各测试里写死：<see cref="SafeDay"/> 会把未来的日期
    /// 压到今天，本月内的多笔记录可能落到同一天。写死数字在靠月初运行时会对不上，
    /// 而那种失败看起来像功能坏了，实际只是测试的假设不成立。
    /// </remarks>
    /// <summary>
    /// 在标准种子上再补几个分类，让分类数超过 DefaultRankRows，"查看全部"才会出现。
    /// 单独一个方法而不是改 SeedStatisticsData：那一份返回的期望值被别的项断言着，
    /// 往里面加条目会连带改掉那些数字。
    /// </summary>
    private static void SeedExtraCategories(BillRepository repo, DateOnly today)
    {
        var month = new DateOnly(today.Year, today.Month, 1);

        // 6 个新分类 + 原有的餐饮、交通、工资（工资是收入，不进支出排行）
        string[] extras = ["购物", "娱乐", "房租", "水电", "医疗", "学习"];
        var entries = new List<PaperCoin.Core.Models.BillEntry>();

        for (int i = 0; i < extras.Length; i++)
        {
            int day = 3 + i * 3;
            entries.Add(Entry(SafeDay(month, day, today), $"{extras[i]}支出", -(60m + i * 25m), extras[i]));
        }

        WaitFor(entries.GroupBy(e => e.Date).Select(g => repo.SaveAsync(g.ToList())));
        DrainSaves();
    }

    private static StatisticsExpectations SeedStatisticsData(BillRepository repo, DateOnly today)
    {
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastYear = new DateOnly(today.Year - 1, thisMonth.Month, 15);

        var entries = new List<PaperCoin.Core.Models.BillEntry>
        {
            // 本月：餐饮两笔 + 交通一笔 + 工资一笔
            Entry(SafeDay(thisMonth, 2, today), "本月午饭", -100m, "餐饮"),
            Entry(SafeDay(thisMonth, 5, today), "本月晚饭", -80m, "餐饮"),
            Entry(SafeDay(thisMonth, 8, today), "本月打车", -40m, "交通"),
            Entry(SafeDay(thisMonth, 10, today), "本月工资", 5000m, "工资"),

            // 去年同月：餐饮一笔。切到"本年"看不见，只有"全部"或跨年时才进来
            Entry(lastYear, "去年午饭", -200m, "餐饮")
        };

        WaitFor(entries.GroupBy(e => e.Date).Select(g => repo.SaveAsync(g.ToList())));

        DrainSaves();

        var inMonth = entries.Where(e => e.Date.Year == today.Year && e.Date.Month == today.Month).ToList();
        var inYear = entries.Where(e => e.Date.Year == today.Year).ToList();

        return new StatisticsExpectations(
            MonthExpense: inMonth.Where(e => e.Amount < 0).Sum(e => -e.Amount),
            MonthIncome: inMonth.Where(e => e.Amount > 0).Sum(e => e.Amount),
            MonthEntryCount: inMonth.Count,
            MonthFood: inMonth.Where(e => e.Category == "餐饮").Sum(e => -e.Amount),
            MonthFoodCount: inMonth.Count(e => e.Category == "餐饮"),
            YearFood: inYear.Where(e => e.Category == "餐饮").Sum(e => -e.Amount),
            YearFoodCount: inYear.Count(e => e.Category == "餐饮"),
            AllFoodCount: entries.Count(e => e.Category == "餐饮"),
            AllEntryCount: entries.Count);
    }

    /// <summary>造数据的各项期望值，见 <see cref="SeedStatisticsData"/></summary>
    private sealed record StatisticsExpectations(
        decimal MonthExpense,
        decimal MonthIncome,
        int MonthEntryCount,
        decimal MonthFood,
        int MonthFoodCount,
        decimal YearFood,
        int YearFoodCount,
        int AllFoodCount,
        int AllEntryCount);

    /// <summary>
    /// 取本月里的第 <paramref name="day"/> 天，超过今天时往前压：统计口径是"到今天就结束"，
    /// 造一条未来日期的记录会让"本月合计"与预期对不上。
    /// </summary>
    private static DateOnly SafeDay(DateOnly monthStart, int day, DateOnly today)
    {
        int maxDay = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var date = new DateOnly(monthStart.Year, monthStart.Month, Math.Min(day, maxDay));

        return date > today ? today : date;
    }

    private static PaperCoin.Core.Models.BillEntry Entry(
        DateOnly date, string description, decimal amount, string? category)
        => new()
        {
            Description = description,
            Amount = amount,
            Category = category,
            Date = date
        };

    private static int Check(string name, Action body)    {
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
