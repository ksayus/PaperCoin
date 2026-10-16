using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using PaperCoin.Core;
using PaperCoin.Service;
using PaperCoin.Services;
using PaperCoin.ViewModels;
using Serilog;

namespace PaperCoin;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{    /// <summary>
    /// 全局服务定位器，只在 MainWindow 的页面切换中使用，那里没有构造注入入口。
    /// 新代码优先用构造函数注入。setter 为 internal，仅供 tools/UiSmokeTest 装载测试容器。
    /// </summary>
    internal static IServiceProvider Services { get; set; } = null!;

    /// <summary>
    /// 单实例守卫。必须是字段而不是局部变量：Mutex 被 GC 回收时会一并释放，
    /// 守卫随之失效且不报错。
    /// </summary>
    private static Mutex? _singleInstance;

    /// <summary>
    /// 主窗口是否用 WPF 的分层窗口（<c>AllowsTransparency=True</c>）。
    /// 由 <see cref="ThemeManager.SavedStyleNeedsLayeredWindow"/> 在窗口构造前定下，
    /// MainWindow 只能读、不能改。分层窗口换来圆角裁剪与 WPF 阴影，
    /// 但会让 DWM 系统背板失效 —— 液态玻璃需要后者。
    /// </summary>
    internal static bool UsesLayeredWindow { get; private set; } = true;

    /// <summary>本项目在 GitHub Releases 的固定地址</summary>
    internal const string ReleasesUrl = "https://github.com/ksayus/PaperCoin/releases";

    // 用 DllImport 而非 LibraryImport：后者由源生成器产出 unsafe 代码，需要在程序集级打开 AllowUnsafeBlocks
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 防多开须先于 base.OnStartup 判定：base 会创建 StartupUri 指定的窗口，建好再退出会闪一下。
        if (!TryAcquireSingleInstance())
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 迁移必须先于一切数据读写：ConfigService 的构造函数就会创建并读配置目录，
        // 排到它后面就只能搬到一个"已经初始化过"的新目录上。
        AppDataMigration.Run();

        Directory.CreateDirectory(Constants.ConfigFolder);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(Constants.LogsFolder, Constants.LogFile),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("PaperCoin 启动, 版本{Version}", Constants.AppVersion);

        // 窗口构造之前就要定下"是否分层窗口"：AllowsTransparency 不能在句柄建好之后改，
        // 而它决定液态玻璃能否用上 DWM 系统背板（真背部模糊）。
        UsesLayeredWindow = ThemeManager.SavedStyleNeedsLayeredWindow(BackdropBlur.IsSupported);
        Log.Information("窗口分层={Layered}（系统背板可用={Supported}）",
            UsesLayeredWindow, BackdropBlur.IsSupported);

        // 记下安装位置，安装程序升级时靠它认出旧路径
        InstallLocation.Record();

        // 安装包写进 MSI Icon 表的图标格式不对（见 ShortcutRepair 注释），
        // 快捷方式的图标只能在这里补上。动作幂等，图标已经对了就不写盘。
        ShortcutRepair.RepairDesktopShortcut();

        var configService = new ConfigService();

        // 主题须先于构建容器落地：MainWindow 由 StartupUri 创建，创建时 DynamicResource 即开始解析，晚了会先闪默认主题。
        var themeManager = new ThemeManager(configService);
        ApplyTheme(themeManager.Palette);

        // 跟随系统模式下，Windows 切换应用主题需要实时反映到窗口上。
        HookSystemTheme(themeManager);

        var services = new ServiceCollection();

        services.AddSingleton(configService);
        services.AddSingleton(themeManager);

        // 账单持久化。内部持有内存索引，注册为单例避免重复构建索引。
        services.AddSingleton<BillRepository>();

        services.AddTransient<TodayBillViewModel>();
        services.AddTransient<CalenderBillViewModel>();
        services.AddTransient<StatisticsViewModel>();
        services.AddTransient<SettingsViewModel>();

        Services = services.BuildServiceProvider();
    }

    // ---- 单实例 ----

    /// <summary>
    /// 尝试成为唯一实例。互斥体名带用户名：同一台机器上的多个会话（远程桌面、快速用户切换）
    /// 可以各开一份，而 Global\ 前缀需要额外权限。
    /// </summary>
    private static bool TryAcquireSingleInstance()
    {
        var name = $"{Constants.AppName}-{Environment.UserName}";

        _singleInstance = new Mutex(initiallyOwned: true, name, out var createdNew);

        if (createdNew) return true;

        // 已有实例持有它。释放自己这份句柄再退出，否则退出时会把别人的互斥体一并释放。
        _singleInstance.Dispose();
        _singleInstance = null;
        return false;
    }

    /// <summary>把已在运行的实例窗口拉到前台。</summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            var current = Environment.ProcessId;

            var other = System.Diagnostics.Process
                .GetProcessesByName(Constants.AppName)
                .FirstOrDefault(p => p.Id != current);

            var handle = other?.MainWindowHandle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;

            // 最小化状态下 SetForegroundWindow 不会还原窗口，必须先 SW_RESTORE。
            ShowWindow(handle, SW_RESTORE);
            SetForegroundWindow(handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 对方刚好在这一刻退出，或权限不足以操作它的窗口，静默忽略。
        }
    }

    // ---- 主题 ----

    /// <summary>
    /// 启动阶段装载主题。此时 MainWindow 尚未创建，没有 DynamicResource 解析过颜色，
    /// 可以直接替换字典实例；运行中切换须走 ThemeRenderer 的原地改写。
    /// </summary>
    /// <param name="palette">调色板名，见 <see cref="Service.ThemeManager.Palette"/></param>
    internal static void ApplyTheme(string palette)
    {
        var merged = Current.Resources.MergedDictionaries;
        var index = ThemeRenderer.IndexOfColorDictionary(merged);
        if (index < 0) return;

        merged[index] = ThemeRenderer.LoadTheme(palette);
    }

    /// <summary>
    /// 让"跟随系统"模式实时响应 Windows 主题切换。SystemParameters.StaticPropertyChanged
    /// 不覆盖应用模式切换，外观偏好变更走 SystemEvents.UserPreferenceChanged；
    /// 该事件在非 UI 线程触发，需要编组回 UI 线程。
    /// </summary>
    private static void HookSystemTheme(ThemeManager themeManager)
    {
        // 渲染订阅先挂上，保证 NotifySystemThemeChanged 发出的 ResolvedThemeChanged 有接收者。
        themeManager.ResolvedThemeChanged += ThemeRenderer.Apply;

        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            // 只处理外观类变更，其余（鼠标、键盘、电源）与主题无关
            if (args.Category != UserPreferenceCategory.General
                && args.Category != UserPreferenceCategory.Color
                && args.Category != UserPreferenceCategory.VisualStyle)
                return;

            var dispatcher = Current?.Dispatcher;
            if (dispatcher is null) return;

            dispatcher.BeginInvoke(new Action(themeManager.NotifySystemThemeChanged));
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("PaperCoin Exit.");
        Log.CloseAndFlush();

        _singleInstance?.Dispose();
        _singleInstance = null;

        base.OnExit(e);
    }
}
