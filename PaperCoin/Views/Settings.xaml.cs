using System.Windows;
using System.Windows.Controls;
using PaperCoin.Service;
using PaperCoin.Service.LanSync;
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

    /// <summary>局域网同步服务的事件同样在 Loaded 订阅、Unloaded 退订</summary>
    private LanSyncService? _lanSync;

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
        _lanSync = _vm?.LanSyncService
                   ?? App.Services.GetService(typeof(LanSyncService)) as LanSyncService;

        if (_themeManager is not null)
        {
            _themeManager.ResolvedThemeChanged += OnThemeResolved;
            _themeManager.SystemThemeChanged += OnSystemThemeChanged;
        }

        if (_lanSync is not null)
        {
            _lanSync.StateChanged += OnLanSyncChanged;
            _lanSync.StatusChanged += OnLanSyncChanged;
            _lanSync.PendingConflictsChanged += OnLanSyncChanged;
        }

        // 补一次：订阅之前系统主题可能已经变过，同步状态也可能已经变化
        _vm?.RefreshThemeState();
        _vm?.RefreshLanSyncState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (_themeManager is not null)
        {
            _themeManager.ResolvedThemeChanged -= OnThemeResolved;
            _themeManager.SystemThemeChanged -= OnSystemThemeChanged;

            _themeManager = null;
        }

        if (_lanSync is not null)
        {
            _lanSync.StateChanged -= OnLanSyncChanged;
            _lanSync.StatusChanged -= OnLanSyncChanged;
            _lanSync.PendingConflictsChanged -= OnLanSyncChanged;

            _lanSync = null;
        }

        _vm = null;
    }

    /// <summary>实际主题变了。渲染已由 App 统一处理，这里只刷新绑定。</summary>
    private void OnThemeResolved(string _) => _vm?.RefreshThemeState();

    /// <summary>系统主题变了。说明文本可能引用系统当前状态，因此无条件刷新一次，由 ViewModel 决定显示什么。</summary>
    private void OnSystemThemeChanged() => _vm?.RefreshThemeState();

    /// <summary>同步服务事件来自网络线程，必须编组回 UI 线程。</summary>
    private void OnLanSyncChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => _vm?.RefreshLanSyncState()));

    private void LanSyncManage_Click(object sender, RoutedEventArgs e)
    {
        var service = _vm?.LanSyncService
                      ?? App.Services.GetService(typeof(LanSyncService)) as LanSyncService;

        if (service is null) return;

        var window = new SyncWindow(service);

        if (Window.GetWindow(this) is { } owner)
            window.Owner = owner;

        window.ShowDialog();

        _vm?.RefreshLanSyncState();
    }
}
