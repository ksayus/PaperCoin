using Microsoft.Win32;
using PaperCoin.Core;
using PaperCoin.Services;

namespace PaperCoin.Service;

/// <summary>
/// 主题状态与持久化的所有者，不含渲染代码：<c>PaperCoin.Service</c> 未启用 <c>UseWPF</c>，
/// 替换资源字典由 UI 层完成。用户选择的是模式（System / Dark / Bright），实际渲染用的是
/// 主题（Dark / Bright），System 模式解析为后者之一。
/// </summary>
public class ThemeManager
{
    /// <summary>跟随系统</summary>
    public const string System = "System";

    /// <summary>强制暗色</summary>
    public const string Dark = "Dark";

    /// <summary>强制亮色</summary>
    public const string Bright = "Bright";

    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly ConfigService _configService;

    public ThemeManager(ConfigService configService)
    {
        _configService = configService;

        Mode = Normalize(_configService.GetAppConfig().App.Theme);
        Resolved = Resolve(Mode);
    }

    /// <summary>用户选择的模式：System / Dark / Bright</summary>
    public string Mode { get; private set; } = Dark;

    /// <summary>实际渲染用的主题：Dark / Bright</summary>
    public string Resolved { get; private set; } = Dark;

    public bool IsSystemMode => Mode == System;
    public bool IsDarkMode => Mode == Dark;
    public bool IsBrightMode => Mode == Bright;

    /// <summary>当前实际生效的主题是否为暗色</summary>
    public bool IsDarkResolved => Resolved == Dark;

    /// <summary>解析后的主题发生变化时触发；模式改变但实际主题不变时不触发。</summary>
    public event Action<string>? ResolvedThemeChanged;

    /// <summary>系统主题发生变化时触发，与当前模式无关；是否重新渲染由监听方决定。</summary>
    public event Action? SystemThemeChanged;

    /// <summary>设置模式并持久化，返回实际生效的主题</summary>
    public string ApplyMode(string? mode)
    {
        var normalized = Normalize(mode);
        var resolved = Resolve(normalized);

        if (normalized != Mode)
        {
            // 先落配置再返回，配置写失败（磁盘只读、权限不足）时不切换界面状态
            var config = _configService.GetAppConfig();
            config.App.Theme = normalized;
            _configService.SaveAppConfig(config);

            Mode = normalized;
        }

        if (resolved != Resolved)
        {
            Resolved = resolved;
            ResolvedThemeChanged?.Invoke(resolved);
        }

        return Resolved;
    }

    /// <summary>重新解析当前模式；模式不是 System 时不做任何事，调用方无需自行判断。</summary>
    /// <returns>解析后的主题</returns>
    public string Refresh()
    {
        var resolved = Resolve(Mode);

        if (resolved != Resolved)
        {
            Resolved = resolved;
            ResolvedThemeChanged?.Invoke(resolved);
        }

        return Resolved;
    }

    /// <summary>
    /// 通知系统主题变化；由 UI 层在收到 Windows 偏好变更广播后调用，探测系统主题需要用
    /// WPF 的 <c>SystemEvents</c>。先无条件触发 <see cref="SystemThemeChanged"/>，再 <see cref="Refresh"/>。
    /// </summary>
    public void NotifySystemThemeChanged()
    {
        SystemThemeChanged?.Invoke();
        Refresh();
    }

    /// <summary>
    /// 读取 Windows 的应用主题偏好。读的是 <c>AppsUseLightTheme</c> 而不是
    /// <c>SystemUsesLightTheme</c>：前者对应应用模式，后者对应任务栏与开始菜单。
    /// 取不到值时按暗色处理。
    /// </summary>
    public static string GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            // 0 = 浅色（亮色主题），1 = 深色（暗色主题）
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0 ? Dark : Bright;
        }
        catch (Exception ex) when (ex is global::System.Security.SecurityException or UnauthorizedAccessException)
        {
            // 组策略可能封锁该注册表路径，探测失败按暗色处理
            return Dark;
        }
    }

    /// <summary>把配置里的字符串收敛到已知模式；值可被用户直接编辑，非法值回退到 Dark。</summary>
    private static string Normalize(string? mode)
    {
        if (string.Equals(mode, Bright, StringComparison.OrdinalIgnoreCase)) return Bright;
        if (string.Equals(mode, System, StringComparison.OrdinalIgnoreCase)) return System;

        return Dark;
    }

    private static string Resolve(string mode)
        => mode == System ? GetSystemTheme() : mode;
}
