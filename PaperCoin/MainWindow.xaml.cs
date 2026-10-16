using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LAE;
using Microsoft.Extensions.DependencyInjection;
using PaperCoin.Core;
using PaperCoin.Service;
using PaperCoin.Services;
using PaperCoin.ViewModels;
using PaperCoin.Views;

namespace PaperCoin;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>侧边栏的导航项</summary>
    private enum NavItem
    {
        TodayBill,
        CalenderBill,
        Statistics,
        Settings
    }

    /// <summary>
    /// 指示器过渡动画的注册名。目标改变时须先停掉在跑的动画，
    /// 否则两条动画会同时往同一个 Y 上写值。
    /// </summary>
    internal const string IndicatorAnimationName = "SideBarIndicator";

    /// <summary>
    /// 指示器当前应停在的位置，即过渡动画的终点。LayoutUpdated 会频繁带着同一目标触发，
    /// 用该值区分布局复算与真正换了导航项，避免打断正在播放的过渡。初值 NaN，首帧必定走播放分支。
    /// </summary>
    private double _indicatorTarget = double.NaN;


    /// <summary>当前高亮的导航项，窗口尺寸变化时用它把指示器重新对准。</summary>
    private NavItem _activeNav = NavItem.TodayBill;

    private readonly Dictionary<string, UserControl> _pageCache = new();

    /// <summary>
    /// 是否用 WPF 的分层窗口。由 <see cref="App.UsesLayeredWindow"/> 在窗口构造前定下，
    /// 这里只读来派生布局相关的东西（阴影、圆角），不能再改 —— 句柄建好后改不了。
    /// </summary>
    internal bool UsesLayeredWindow => App.UsesLayeredWindow;

    /// <summary>
    /// 玻璃背板的圆角。走 WPF 分层窗口时自己裁角；走 DWM 背板时由系统裁，
    /// 这里留 0，否则圆角外会露出一圈没被裁到的窗口底色。
    /// </summary>
    internal CornerRadius WindowCornerRadius => UsesLayeredWindow ? new CornerRadius(7) : new CornerRadius(0);

    public MainWindow()
    {
        // AllowsTransparency 必须在 InitializeComponent 之前设：窗口句柄一旦建好就不能再改。
        AllowsTransparency = UsesLayeredWindow;

        // 两种情况都用 Transparent，但含义不同：
        //  · 分层窗口下是"真透明"，桌面透上来，圆角由 WPF 裁。
        //  · 非分层窗口下是"这个像素不着色"，DWM 的系统背板正好露出来 ——
        //    这里若给不透明色（哪怕黑色）就把背板整个盖住，模糊白设。
        Background = Brushes.Transparent;

        InitializeComponent();
        this.MaxHeight = SystemParameters.PrimaryScreenHeight;

        NavigateTo("TodayBill");
        Loaded += MainWindow_Loaded;

        // SizeChanged 在拖拽时会被节流，用 LayoutUpdated 保证布局变化后校正一次。
        SizeChanged += (_, _) => RepositionIndicator(animate: false);
        LayoutUpdated += (_, _) =>
        {
            if (IsLoaded) RepositionIndicator(animate: false);
        };
    }

    /// <summary>本次进程是否已经在后台查过更新。窗口可能被反复 Loaded，只查一次。</summary>
    private static bool _updateChecked;

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesOnStartup();

        // 背板要在句柄就绪后设，而且窗口尺寸变化后 DWM 偶尔会把它丢掉，所以跟着布局一起校正
        ApplyBackdrop();
        LayoutUpdated += (_, _) => ApplyBackdrop();

        // 推迟到 Render 优先级：此时布局未完成，NavPanel.ActualHeight 仍为 0，算出的位置不正确。
        Dispatcher.BeginInvoke(new Action(() => RepositionIndicator(animate: true)),
            DispatcherPriority.Render);
    }

    // ---- 启动时检查更新 ----

    /// <summary>
    /// 启动时在后台查一次更新。
    /// <para>
    /// 必须在 Loaded 之后、且**不 await**：网络慢或不通时不能拖住窗口显示 ——
    /// 更新检查失败绝不该影响程序能不能用。
    /// </para>
    /// <para>
    /// 结果写进单例的 <see cref="AppUpdater"/>，不弹任何对话框：用户没主动问的时候，
    /// 打断式弹窗比"设置页里多一行"更烦人。
    /// </para>
    /// </summary>
    private void CheckForUpdatesOnStartup()
    {
        if (_updateChecked) return;
        _updateChecked = true;

        // 测试宿主会反复建窗口，每次都真的联网会让整套测试慢到超时，而且结果随网络抖动而变。
        // 用一个环境变量就能关掉，不必让生产代码为测试开后门。
        if (Environment.GetEnvironmentVariable("PAPERCOIN_NO_UPDATE_CHECK") == "1") return;

        var config = App.Services?.GetService(typeof(ConfigService)) as ConfigService;
        if (config?.GetAppConfig().App.AutoUpdate != true) return;

        var updater = App.Services?.GetService(typeof(AppUpdater)) as AppUpdater;
        if (updater is null) return;

        // 结果记在单例的 AppUpdater 上，设置页构造时自己接手 ——
        // 不往 SettingsViewModel 里塞：那是 transient 的，这里拿到的是另一个实例，
        // 写进去的结果用户永远看不到。
        _ = Dispatcher.BeginInvoke(
            new Action(() => _ = updater.CheckAndRememberAsync(
                UpdateSource.Github, Version.Parse(Constants.AppVersion))),
            DispatcherPriority.ApplicationIdle);
    }

    // ---- 背部高斯模糊 ----

    /// <summary>上一次应用的背板状态，避免每次布局都重复调 DWM</summary>
    private (bool Applied, bool Dark)? _backdropState;

    /// <summary>
    /// 把 DWM 的系统背板对齐到当前主题。
    /// <para>
    /// 只有"非分层窗口 + 液态玻璃"才开背板：非分层是系统背板的硬要求，
    /// 而其它风格表面本就不透明，开了也看不见，还会白白多一层合成开销。
    /// </para>
    /// </summary>
    private void ApplyBackdrop()
    {
        if (!IsLoaded || !BackdropBlur.IsSupported) return;

        var theme = App.Services?.GetService(typeof(ThemeManager)) as ThemeManager;
        bool want = !UsesLayeredWindow && theme?.IsLiquidStyle == true;
        bool dark = theme?.IsDarkResolved ?? true;

        if (_backdropState == (want, dark)) return;
        _backdropState = (want, dark);

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        // 明度由 XAML 里的 ColorGlassScrim 控制，不再依赖 DWM 色调
        // （DWMWA_BACKDROP_TINT_COLOR 在部分 Windows 11 版本上会被拒）。
        if (want) BackdropBlur.Apply(handle, dark);
        else BackdropBlur.Clear(handle);
    }

    // ---- 窗口控制 ----

    private void TitleBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
            this.DragMove();
    }

    private void Min_btn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_btn_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_btn_Click(object sender, RoutedEventArgs e) => Close();

    // ---- 导航 ----

    private void TodayBill_Nav_Click(object sender, RoutedEventArgs e)
        => GoTo(NavItem.TodayBill, TodayBillBtn);

    private void CalenderBtn_Click(object sender, RoutedEventArgs e)
        => GoTo(NavItem.CalenderBill, CalenderBtn);

    private void StatisticsBtn_Click(object sender, RoutedEventArgs e)
        => GoTo(NavItem.Statistics, StatisticsBtn);

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        => GoTo(NavItem.Settings, SettingsBtn);

    /// <summary>
    /// 切换到指定导航项。指示器位置由按钮自身的布局算出，不使用硬编码像素。
    /// 统计页不订阅仓库，靠"索引版本变了才重算"决定要不要重新聚合，
    /// 所以切到它时要显式叫一次刷新，否则记完账再切回来看到的还是旧数字。
    /// </summary>
    private void GoTo(NavItem item, Button button)
    {
        _activeNav = item;
        NavigateTo(item.ToString());
        MoveIndicatorTo(button, animate: true);

        if (item == NavItem.Statistics && MainContentArea.Child is Views.Statistics statistics)
            _ = statistics.RefreshAsync();
    }

    private void NavigateTo(string pageName)
    {
        UserControl? page = pageName switch
        {
            "TodayBill" => GetOrCreatePage("TodayBill", () => new TodayBill()),
            "CalenderBill" => GetOrCreatePage("CalenderBill", () => new CalenderBill()),
            "Statistics" => GetOrCreatePage("Statistics", () => new Statistics()),
            "Settings" => GetOrCreatePage("Settings", () => new Settings()),
            _ => null
        };

        if (page is null) return;

        UpdatePageDataContext(page, pageName);
        MainContentArea.Child = page;
    }

    private UserControl GetOrCreatePage(string key, Func<UserControl> factory)
    {
        if (!_pageCache.TryGetValue(key, out var page))
        {
            page = factory();
            _pageCache[key] = page;
        }
        return page;
    }

    private void UpdatePageDataContext(UserControl page, string pageName)
    {
        switch (pageName)
        {
            case "TodayBill":
                page.DataContext = App.Services.GetRequiredService<TodayBillViewModel>();
                break;
            case "CalenderBill":
                page.DataContext = App.Services.GetRequiredService<CalenderBillViewModel>();
                break;
            case "Statistics":
                page.DataContext = App.Services.GetRequiredService<StatisticsViewModel>();
                break;
            case "Settings":
                page.DataContext = App.Services.GetRequiredService<SettingsViewModel>();
                break;
        }
    }

    // ---- 指示器定位 ----

    private void RepositionIndicator(bool animate)
    {
        // 四个导航项都要显式列出。用 switch 的默认分支兜底时，新增的枚举值会静默落到
        // 最后一个按钮上 —— 点击统计项，指示器却停在设置项，而且没有任何报错。
        var button = _activeNav switch
        {
            NavItem.TodayBill => TodayBillBtn,
            NavItem.CalenderBill => CalenderBtn,
            NavItem.Statistics => StatisticsBtn,
            NavItem.Settings => SettingsBtn,
            _ => TodayBillBtn
        };

        MoveIndicatorTo(button, animate);
    }

    // ---- 自动化验证入口（仅供 tools/UiSmokeTest 使用，internal + InternalsVisibleTo）----

    /// <summary>指示器当前的纵向偏移，供测试断言它随布局重算。</summary>
    internal double IndicatorY => SideBarActiveShapeTranslateTransform.Y;

    /// <summary>侧边栏按钮容器的高度，供测试确认缩放改变了布局。</summary>
    internal double NavPanelHeight => NavPanel.ActualHeight;

    /// <summary>
    /// 某个导航项落定后指示器应当停在的偏移量。
    /// <para>
    /// 测试用它做**位置**断言：只断言"指示器动了"是不够的 —— 新增枚举值漏进 switch 的
    /// 默认分支时，点统计项会让指示器停在设置项上，而"动过了"依然成立。
    /// </para>
    /// </summary>
    internal double IndicatorTargetFor(string pageName) => IndicatorTargetFor(NavButtonForTest(pageName));

    private double IndicatorTargetFor(Button button)
    {
        double navHeight = NavPanel.ActualHeight;
        double buttonHeight = button.ActualHeight;
        if (navHeight <= 0 || buttonHeight <= 0) return double.NaN;

        double center = button.TranslatePoint(new Point(0, buttonHeight / 2), NavPanel).Y;

        double indicatorHeight = SideBarActiveShape.ActualHeight > 0
            ? SideBarActiveShape.ActualHeight
            : SideBarActiveShape.Height;

        return center - navHeight + indicatorHeight / 2 + 4;
    }

    /// <summary>按名导航，供测试驱动侧边栏切换。</summary>
    internal void NavigateForTest(string pageName)
    {
        _activeNav = pageName switch
        {
            "CalenderBill" => NavItem.CalenderBill,
            "Statistics" => NavItem.Statistics,
            "Settings" => NavItem.Settings,
            _ => NavItem.TodayBill
        };

        NavigateTo(pageName);
        RepositionIndicator(animate: false);

        // 与 GoTo 保持一致：按名导航也要给统计页一次刷新机会，
        // 否则测试走的是与真实点击不同的一条路径
        if (MainContentArea.Child is Views.Statistics statistics)
            _ = statistics.RefreshAsync();
    }

    /// <summary>取侧边栏按钮，供测试以真实点击驱动导航（NavigateForTest 走 animate: false，覆盖不到过渡动画）。</summary>
    internal Button NavButtonForTest(string pageName) => pageName switch
    {
        "CalenderBill" => CalenderBtn,
        "Statistics" => StatisticsBtn,
        "Settings" => SettingsBtn,
        _ => TodayBillBtn
    };

    /// <summary>
    /// 取当前挂载在内容区的页面，供测试断言换页真的换了。
    /// 页面实例由 <see cref="_pageCache"/> 长期持有，不取这个引用就只能靠类型硬转。
    /// </summary>
    internal UserControl? FindPageForTest(string pageName)
    {
        _pageCache.TryGetValue(pageName, out var page);
        return page;
    }

    /// <summary>
    /// 把指示器对准指定按钮。指示器的 VerticalAlignment 为 Bottom，Y 表示相对底端的负向偏移，
    /// 偏移量 = 按钮中心（相对 NavPanel）− 侧边栏高度 + 指示器半高。
    /// </summary>
    private void MoveIndicatorTo(Button? button, bool animate)
    {
        if (button is null || NavPanel is null || SideBarActiveShape is null) return;

        double navHeight = NavPanel.ActualHeight;
        double buttonHeight = button.ActualHeight;

        // 布局未完成时两个高度均为 0，跳过，等 LayoutUpdated 再算。
        if (navHeight <= 0 || buttonHeight <= 0) return;

        double buttonCenter = button.TranslatePoint(
            new Point(0, buttonHeight / 2), NavPanel).Y;

        double indicatorHeight = SideBarActiveShape.ActualHeight > 0
            ? SideBarActiveShape.ActualHeight
            : SideBarActiveShape.Height;

        double target = buttonCenter - navHeight + indicatorHeight / 2 + 4;

        ApplyIndicatorOffset(target, animate);
    }

    /// <summary>
    /// 施加偏移。用绝对目标而非相对位移，重复调用不累积误差；
    /// 目标未变化时直接返回，让正在播放的过渡跑完。
    /// </summary>
    private void ApplyIndicatorOffset(double target, bool animate)
    {
        if (SideBarActiveShapeTranslateTransform is null
            || SideBarActiveShapeScaleTransform is null) return;

        if (Math.Abs(_indicatorTarget - target) < 0.5) return;

        _indicatorTarget = target;

        // 先停掉在跑的旧过渡，避免两条动画同时写同一个 Y
        LAEngine.Stop(IndicatorAnimationName);

        if (!animate)
        {
            // 拖拽缩放时直接落位，插值会让指示器追着鼠标跑
            SideBarActiveShapeTranslateTransform.Y = target;

            // 缩放也要归位，只落位 Y 会让指示器停在 0.96 或 1.05 这类非静止尺寸上。
            SideBarActiveShapeScaleTransform.ScaleX = 1;
            SideBarActiveShapeScaleTransform.ScaleY = 1;
            return;
        }

        LA.Builder(IndicatorAnimationName)
            .Ease(Easing.InOutCubic)
            .Move(SideBarActiveShapeTranslateTransform, 0, target, 240)
            .Scale(SideBarActiveShapeScaleTransform, 0.92, 80)
            .Then()
            .Scale(SideBarActiveShapeScaleTransform, 1.05, 80)
            .Then()
            .Scale(SideBarActiveShapeScaleTransform, 1.0, 40)
            .Play();
    }
}
