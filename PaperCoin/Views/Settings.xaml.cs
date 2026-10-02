using System.Windows;
using System.Windows.Controls;
using PaperCoin.Service;
using PaperCoin.ViewModels;

namespace PaperCoin.Views;

/// <summary>
/// Settings 的视图逻辑。
/// ThemeManager 的事件是强引用，而容器不会释放 Transient 实例，所以订阅与退订放在视图的 Loaded / Unloaded。
/// </summary>
public partial class Settings : UserControl
{
    private SettingsViewModel? _vm;

    /// <summary>当前订阅的 ThemeManager，退订时要用同一个实例</summary>
    private ThemeManager? _themeManager;

    public Settings()
    {
        InitializeComponent();

        // 订阅放在 Loaded：DataContextChanged 可能早于 Loaded 触发，那时 App.Services 还没构建好，取 ThemeManager 会空引用。
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Detach();

        _vm = DataContext as SettingsViewModel;
        _themeManager = App.Services.GetService(typeof(ThemeManager)) as ThemeManager;

        if (_themeManager is null) return;

        _themeManager.ResolvedThemeChanged += OnThemeResolved;
        _themeManager.SystemThemeChanged += OnSystemThemeChanged;

        // 补一次：订阅之前系统主题可能已经变过
        _vm?.RefreshThemeState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (_themeManager is null) return;

        _themeManager.ResolvedThemeChanged -= OnThemeResolved;
        _themeManager.SystemThemeChanged -= OnSystemThemeChanged;

        _themeManager = null;
        _vm = null;
    }

    /// <summary>实际主题变了。渲染已由 App 统一处理，这里只刷新绑定。</summary>
    private void OnThemeResolved(string _) => _vm?.RefreshThemeState();

    /// <summary>系统主题变了。说明文本可能引用系统当前状态，因此无条件刷新一次，由 ViewModel 决定显示什么。</summary>
    private void OnSystemThemeChanged() => _vm?.RefreshThemeState();
}
