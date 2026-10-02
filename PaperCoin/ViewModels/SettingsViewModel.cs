using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PaperCoin.Core;
using PaperCoin.Core.Models;
using PaperCoin.Service;
using PaperCoin.Services;

namespace PaperCoin.ViewModels;

/// <summary>
/// 设置页面。自动启动直接写 HKCU 的 Run 项（不需要管理员权限）；自动更新目前只落盘配置，
/// 未接入检查与下载逻辑，见 <see cref="UpdateStatusText"/>。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly ConfigService _configService;
    private readonly ThemeManager _themeManager;

    public SettingsViewModel(ConfigService configService, ThemeManager themeManager)
    {
        _configService = configService;
        _themeManager = themeManager;

        ApplyThemeCommand = new RelayCommand<string>(ApplyThemeMode);
        ToggleAutoStartCommand = new RelayCommand(ToggleAutoStart);
        ToggleAutoUpdateCommand = new RelayCommand(ToggleAutoUpdate);
        OpenReleasesCommand = new RelayCommand(OpenReleases);
        ResetCommand = new RelayCommand(ResetToDefault);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);

        LoadFrom(_configService.GetAppConfig());
    }

    public ICommand ApplyThemeCommand { get; }
    public ICommand ToggleAutoStartCommand { get; }
    public ICommand ToggleAutoUpdateCommand { get; }
    public ICommand OpenReleasesCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    // ---- 外观 ----

    /// <summary>用户选了"跟随系统"</summary>
    public bool IsSystemTheme => _themeManager.IsSystemMode;

    /// <summary>用户选了强制暗色</summary>
    public bool IsDarkTheme => _themeManager.IsDarkMode;

    /// <summary>用户选了强制亮色</summary>
    public bool IsBrightTheme => _themeManager.IsBrightMode;

    /// <summary>当前外观的说明文本，跟随系统时附带系统实际解析出的主题</summary>
    public string ThemeText
    {
        get
        {
            if (!_themeManager.IsSystemMode) return _themeManager.IsDarkResolved ? "暗色" : "亮色";
            return _themeManager.IsDarkResolved ? "跟随系统（当前：暗色）" : "跟随系统（当前：亮色）";
        }
    }

    /// <summary>
    /// 切换外观模式。这里只改状态，渲染统一由 ThemeManager.ApplyMode 触发的
    /// ResolvedThemeChanged 事件完成（挂载点见 Settings.xaml.cs）。
    /// </summary>
    private void ApplyThemeMode(string? mode)
    {
        _themeManager.ApplyMode(mode);
    }

    /// <summary>系统主题变化或模式切换后刷新绑定</summary>
    public void RefreshThemeState()
    {
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsBrightTheme));
        OnPropertyChanged(nameof(ThemeText));
    }

    // ---- 启动 ----

    private bool _autoStart;

    public bool AutoStart
    {
        get => _autoStart;
        private set => SetProperty(ref _autoStart, value);
    }

    private void ToggleAutoStart()
    {
        var target = !AutoStart;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                AutoStartError = "无法打开注册表启动项";
                return;
            }

            if (target)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    AutoStartError = "无法确定程序路径";
                    return;
                }

                // 路径必须加引号，否则装在含空格的目录下时会被截断
                key.SetValue(Constants.AppName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(Constants.AppName, throwOnMissingValue: false);
            }

            AutoStart = target;
            AutoStartError = "";

            // 注册表与配置文件是两份状态，都要写，否则重启后开关显示与实际行为不一致
            Persist(c => c.App.AutoStart = target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 组策略或安全软件可能禁止写 Run 项，异常不应冒泡
            AutoStartError = "系统策略禁止修改启动项";
        }
    }

    private string _autoStartError = "";

    /// <summary>自动启动设置失败的原因，供界面提示</summary>
    public string AutoStartError
    {
        get => _autoStartError;
        private set
        {
            if (SetProperty(ref _autoStartError, value))
                OnPropertyChanged(nameof(HasAutoStartError));
        }
    }

    public bool HasAutoStartError => !string.IsNullOrEmpty(AutoStartError);

    // ---- 更新 ----

    private bool _autoUpdate;

    public bool AutoUpdate
    {
        get => _autoUpdate;
        private set
        {
            if (!SetProperty(ref _autoUpdate, value)) return;
            OnPropertyChanged(nameof(UpdateStatusText));
        }
    }

    private string _updateSource = "Github";

    public string UpdateSource
    {
        get => _updateSource;
        private set => SetProperty(ref _updateSource, value);
    }

    /// <summary>可选更新源</summary>
    public IReadOnlyList<string> UpdateSources { get; } = ["Github", "Gitee"];

    /// <summary>更新状态说明，如实标注检查逻辑尚未接入</summary>
    public string UpdateStatusText => AutoUpdate
        ? $"已启用（更新源 {UpdateSource}）・检查逻辑尚未接入"
        : "已关闭";

    private void ToggleAutoUpdate()
    {
        var target = !AutoUpdate;
        AutoUpdate = target;
        Persist(c => c.App.AutoUpdate = target);
    }

    /// <summary>用系统浏览器打开 GitHub Releases 页面</summary>
    private void OpenReleases()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = App.ReleasesUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // 没有默认浏览器时不要崩
            AutoStartError = "无法打开浏览器";
        }
    }

    // ---- 关于 / 维护 ----

    public string VersionText => Constants.AppVersion;

    /// <summary>
    /// 界面上展示的数据目录，即账单文件夹本身。层级布局（yyyy/M/d/bills.json）
    /// 由 <see cref="PaperCoin.Service.BillJsonManager"/> 决定。
    /// </summary>
    public string DataFolderText => Constants.BillFolder;

    private void OpenDataFolder()
    {
        // 目录可能尚未创建，先确保存在再打开
        Directory.CreateDirectory(Constants.BillFolder);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Constants.BillFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AutoStartError = "无法打开数据目录";
        }
    }

    private void ResetToDefault()
    {
        var defaults = new AppConfig();

        _configService.SaveAppConfig(defaults);
        _configService.Reload();

        LoadFrom(defaults);

        // 主题同样只改状态，渲染交给事件
        _themeManager.ApplyMode(defaults.App.Theme);
        RefreshThemeState();
    }

    // ---- 读写 ----

    private void LoadFrom(AppConfig config)
    {
        AutoStart = config.App.AutoStart;
        AutoUpdate = config.App.AutoUpdate;
        UpdateSource = config.App.AutoUpdateSource;
        AutoStartError = "";
    }

    /// <summary>
    /// 读改写整体落盘。每次都重新读取配置而不是缓存 <see cref="AppConfig"/> 实例，
    /// 避免覆盖别处对配置文件的修改。
    /// </summary>
    private void Persist(Action<AppConfig> mutate)
    {
        var config = _configService.GetAppConfig();
        mutate(config);
        _configService.SaveAppConfig(config);
        _configService.Reload();
    }
}
