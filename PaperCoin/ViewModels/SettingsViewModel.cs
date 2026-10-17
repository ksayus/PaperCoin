using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PaperCoin.Core;
using PaperCoin.Core.Models;
using PaperCoin.Service;
using PaperCoin.Service.LanSync;
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
    private readonly AppUpdater _updater;
    private readonly LanSyncService? _lanSyncService;

    public SettingsViewModel(
        ConfigService configService,
        ThemeManager themeManager,
        AppUpdater updater,
        LanSyncService? lanSyncService = null)
    {
        _configService = configService;
        _themeManager = themeManager;
        _updater = updater;
        _lanSyncService = lanSyncService;

        ApplyThemeCommand = new RelayCommand<string>(ApplyThemeMode);
        ApplyThemeStyleCommand = new RelayCommand<string>(ApplyThemeStyle);
        ToggleAutoStartCommand = new RelayCommand(ToggleAutoStart);
        ToggleAutoUpdateCommand = new RelayCommand(ToggleAutoUpdate);
        CheckUpdateCommand = new RelayCommand(() => _ = CheckForUpdateAsync(manual: true), () => !IsBusy);
        InstallUpdateCommand = new RelayCommand(() => _ = DownloadAndInstallAsync(), () => CanInstall);
        OpenReleasesCommand = new RelayCommand(OpenReleases);
        ResetCommand = new RelayCommand(ResetToDefault);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);

        LoadFrom(_configService.GetAppConfig());

        // 读启动时后台查询留下的结果：SettingsViewModel 是 transient 的，
        // 不在这里接手的话，那次查询白做了 —— 用户进设置页只会看到一片空白。
        AdoptRememberedResult();
    }

    /// <summary>把单例里记住的查询结果搬到界面上</summary>
    private void AdoptRememberedResult()
    {
        if (_updater.Available is not { } release) return;

        _available = release;
        ReleaseNotes = Summarize(release.Notes);
        _checkMessage = UpdateHeadline;
    }

    public ICommand ApplyThemeCommand { get; }

    /// <summary>主题风格：经典 / Windows / 液态玻璃</summary>
    public ICommand ApplyThemeStyleCommand { get; }
    public ICommand ToggleAutoStartCommand { get; }
    public ICommand ToggleAutoUpdateCommand { get; }

    /// <summary>手动查一次有没有新版本</summary>
    public ICommand CheckUpdateCommand { get; }

    /// <summary>下载并启动安装程序</summary>
    public ICommand InstallUpdateCommand { get; }
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

    /// <summary>切换主题风格。同样只改状态，渲染走 ResolvedThemeChanged。</summary>
    private void ApplyThemeStyle(string? style)
    {
        _themeManager.ApplyStyle(style);
    }

    /// <summary>系统主题变化或模式切换后刷新绑定</summary>
    public void RefreshThemeState()
    {
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsBrightTheme));
        OnPropertyChanged(nameof(ThemeText));

        OnPropertyChanged(nameof(IsClassicStyle));
        OnPropertyChanged(nameof(IsWindowsStyle));
        OnPropertyChanged(nameof(IsLiquidStyle));
        OnPropertyChanged(nameof(StyleText));
    }

    /// <summary>当前风格的说明，给设置页那行副标题用</summary>
    public string StyleText => _themeManager.Style switch
    {
        ThemeManager.StyleWindows => "Fluent 观感：纯色表面、中性灰阶、系统蓝强调",
        ThemeManager.StyleLiquid => "半透明材质 + 发丝高光，玻璃面板透出背板",
        _ => "项目原本的观感：圆角卡片 + 柔和阴影"
    };

    // ---- 主题风格 ----

    /// <summary>经典：项目原本的观感</summary>
    public bool IsClassicStyle => _themeManager.IsClassicStyle;

    /// <summary>Windows：Fluent 观感，纯色表面 + 系统强调蓝</summary>
    public bool IsWindowsStyle => _themeManager.IsWindowsStyle;

    /// <summary>液态玻璃：半透明材质 + 发丝高光</summary>
    public bool IsLiquidStyle => _themeManager.IsLiquidStyle;

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
        private set
        {
            if (!SetProperty(ref _updateSource, value)) return;

            OnPropertyChanged(nameof(UpdateSourceKind));
            OnPropertyChanged(nameof(UpdateStatusText));
        }
    }

    /// <summary>可选更新源</summary>
    public IReadOnlyList<string> UpdateSources { get; } = ["Github", "Gitee"];

    /// <summary>把界面上的字符串收敛成枚举。<c>UpdateSource</c> 是配置里可被手改的文本。</summary>
    internal Service.UpdateSource UpdateSourceKind =>
        string.Equals(UpdateSource, "Gitee", StringComparison.OrdinalIgnoreCase)
            ? Service.UpdateSource.Gitee
            : Service.UpdateSource.Github;

    /// <summary>更新行的一句话状态：开关状态 + 当前进展</summary>
    public string UpdateStatusText => _checkMessage.Length > 0
        ? _checkMessage
        : AutoUpdate ? $"已启用（更新源 {UpdateSource}）" : "已关闭";

    // ---- 检查与安装 ----

    private string _checkMessage = "";
    private string _releaseNotes = "";
    private string _downloadText = "";
    private UpdateRelease? _available;
    private bool _isBusy;
    private bool _isDownloading;
    private double _downloadPercent;

    /// <summary>查询或下载进行中。期间不允许重复触发。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;

            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(IsIdle));

            (CheckUpdateCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (InstallUpdateCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    /// <summary>没在忙的时候才显示"检查"按钮</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>正在下载安装包</summary>
    public bool IsDownloading
    {
        get => _isDownloading;
        private set => SetProperty(ref _isDownloading, value);
    }

    /// <summary>下载进度 0..1；总长未知时为 0</summary>
    public double DownloadPercent
    {
        get => _downloadPercent;
        private set => SetProperty(ref _downloadPercent, value);
    }

    /// <summary>下载进度文案</summary>
    public string DownloadText
    {
        get => _downloadText;
        private set => SetProperty(ref _downloadText, value);
    }

    /// <summary>发现了可安装的新版本</summary>
    public bool HasUpdate => _available is not null;

    /// <summary>可以点"下载并安装"：有新版、且这个发布里带安装包</summary>
    public bool CanInstall => _available is { InstallerUrl: not null } && !IsBusy;

    /// <summary>有新版本但没有可安装的文件，只能去发布页手动下载</summary>
    public bool MustDownloadManually => _available is { InstallerUrl: null };

    /// <summary>新版本的发布说明，直接展示给用户</summary>
    public string ReleaseNotes
    {
        get => _releaseNotes;
        private set
        {
            if (SetProperty(ref _releaseNotes, value)) OnPropertyChanged(nameof(HasReleaseNotes));
        }
    }

    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);

    /// <summary>新版本的标题行，如「发现新版本 1.3.7（当前 1.3.6）」</summary>
    public string UpdateHeadline => _available is { } release && release.Version.Major > 0
        ? $"发现新版本 {release.Version}（当前 {Constants.AppVersion}）" +
          (release.InstallerUrl is null ? "· 需手动下载" : "")
        : "";

    /// <summary>
    /// 查一次更新。
    /// </summary>
    /// <param name="manual">
    /// 用户主动点的。手动时即使"已是最新"也要说出来；自动检查时只在真有新版时才吭声，
    /// 否则每次启动都往界面上写一句废话。
    /// </param>
    internal async Task CheckForUpdateAsync(bool manual)
    {
        if (IsBusy) return;

        IsBusy = true;

        try
        {
            if (manual) _checkMessage = "正在检查…";

            _checkMessage = "正在检查…";
            OnPropertyChanged(nameof(UpdateStatusText));

            var current = Version.Parse(Constants.AppVersion);
            var check = await _updater.CheckAndRememberAsync(UpdateSourceKind, current);

            _available = null;
            ReleaseNotes = "";

            // "没有发布过任何版本"时后端给的是一个占位发布（版本 0），不能当更新
            if (check.Release is { } release && release.Version > current)
            {
                _available = release;

                ReleaseNotes = Summarize(release.Notes);
                _checkMessage = UpdateHeadline;
            }
            else
            {
                _checkMessage = check.IsFailure || manual ? check.Message : "";
            }

            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(MustDownloadManually));
            OnPropertyChanged(nameof(UpdateHeadline));
            OnPropertyChanged(nameof(UpdateStatusText));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 下载安装包并交给系统安装。
    /// <para>
    /// 这里**不代替用户关掉自己**：安装程序会自己提示"请关闭 PaperCoin"。
    /// 进程还活着时 MSI 会挡下来并要求重试，这是最不会出错的做法 ——
    /// 自作主张退出会在用户取消安装时留下一个已经关掉的程序。
    /// </para>
    /// </summary>
    internal async Task DownloadAndInstallAsync()
    {
        if (!CanInstall || _available is not { InstallerUrl: not null } release) return;

        IsBusy = true;
        IsDownloading = true;
        DownloadPercent = 0;
        DownloadText = "正在下载…";

        try
        {
            _updater.Progress = new Progress<(long Done, long Total)>(p =>
            {
                if (p.Total <= 0)
                {
                    DownloadText = $"正在下载…{p.Done / 1024.0 / 1024.0:0.0} MB";
                    return;
                }

                DownloadPercent = Math.Clamp(p.Done / (double)p.Total, 0, 1);
                DownloadText = $"正在下载…{DownloadPercent * 100:0}%（{p.Done / 1024.0 / 1024.0:0.0}/" +
                               $"{p.Total / 1024.0 / 1024.0:0.0} MB）";
            });

            var path = AppUpdater.DownloadPathFor(release.InstallerName);

            var failure = await _updater.DownloadAsync(release.InstallerUrl, path);

            if (failure.Length > 0)
            {
                _checkMessage = failure;
                OnPropertyChanged(nameof(UpdateStatusText));
                return;
            }

            DownloadText = "下载完成，正在启动安装程序…";

            var launch = AppUpdater.LaunchInstaller(path);

            _checkMessage = launch.Length > 0
                ? launch
                : "安装程序已启动，按提示完成升级（会提示关闭本程序）";

            OnPropertyChanged(nameof(UpdateStatusText));
        }
        finally
        {
            _updater.Progress = null;
            IsDownloading = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// 发布说明常常很长（一个版本塞了几十条），界面上只留前几行。
    /// 截断而不是滚动：这块地方在设置页里，用户要的是"大概改了什么"。
    /// </summary>
    private static string Summarize(string notes, int maxLines = 8)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";

        var lines = notes
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        return lines.Length <= maxLines
            ? string.Join("\n", lines)
            : string.Join("\n", lines.Take(maxLines)) + "\n…（完整说明见发布页）";
    }

    private void ToggleAutoUpdate()
    {
        var target = !AutoUpdate;
        AutoUpdate = target;
        Persist(c => c.App.AutoUpdate = target);

        OnPropertyChanged(nameof(UpdateStatusText));

        // 刚打开就顺手查一次：开关拨开却什么都不发生，用户不知道它到底有没有在工作。
        // 测试里必须能关掉这条 —— 它会真的联网，而测试会反复拨这个开关。
        if (target && Environment.GetEnvironmentVariable("PAPERCOIN_NO_UPDATE_CHECK") != "1")
            _ = CheckForUpdateAsync(manual: false);
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

    // ---- 局域网同步 ----

    /// <summary>SyncWindow 的入口；未接入（例如 UI 测试宿主）时为 null。</summary>
    public LanSyncService? LanSyncService => _lanSyncService;

    /// <summary>设置页那一行的副标题：启用状态 + 监听端口 / 待处理冲突。</summary>
    public string LanSyncStatusText
    {
        get
        {
            if (_lanSyncService is null) return "未接入";

            var state = _lanSyncService.GetState();
            if (!state.Enabled) return "已关闭";

            if (!_lanSyncService.IsRunning) return "已启用 · 未监听";

            var pending = _lanSyncService.GetPendingConflicts().Count;
            if (pending > 0) return $"待处理冲突 {pending} 处";

            var auto = state.AutoSync ? "自动同步" : "仅手动";
            var port = _lanSyncService.ListeningPort;

            return port > 0 ? $"已启用 · TCP {port} · {auto}" : $"已启用 · {auto}";
        }
    }

    /// <summary>同步窗口关闭、服务状态变化后刷新那一行。</summary>
    public void RefreshLanSyncState()
    {
        OnPropertyChanged(nameof(LanSyncStatusText));
        OnPropertyChanged(nameof(LanSyncService));
    }

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
