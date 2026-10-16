using Microsoft.Win32;
using PaperCoin.Core;
using PaperCoin.Services;

namespace PaperCoin.Service;

/// <summary>
/// 主题状态与持久化的所有者，不含渲染代码：<c>PaperCoin.Service</c> 未启用 <c>UseWPF</c>，
/// 替换资源字典由 UI 层完成。
/// <para>
/// 主题有**两个维度**，互相独立：
/// </para>
/// <list type="bullet">
/// <item><b>明暗</b>：跟随系统 / 暗色 / 亮色。决定用哪一套明度的颜色。</item>
/// <item><b>风格</b>：经典 / Windows / 液态玻璃。决定材质与配色的性格。</item>
/// </list>
/// <para>
/// 两个维度的组合是 8 种，对应 <c>Themes/</c> 下 6 本调色板字典
/// （明暗只有两档，跟随系统会先解析成暗或亮）。经典风格的字典名沿用原来的
/// <c>Dark</c> / <c>Bright</c>，没带风格后缀 —— 改掉它会让老用户的配置失效。
/// </para>
/// </summary>
public class ThemeManager
{
    /// <summary>跟随系统</summary>
    public const string System = "System";

    /// <summary>强制暗色</summary>
    public const string Dark = "Dark";

    /// <summary>强制亮色</summary>
    public const string Bright = "Bright";

    /// <summary>经典：项目原本的观感</summary>
    public const string StyleClassic = "Classic";

    /// <summary>Windows：Fluent 观感，纯色表面 + 系统强调蓝</summary>
    public const string StyleWindows = "Windows";

    /// <summary>液态玻璃：半透明材质 + 发丝高光</summary>
    public const string StyleLiquid = "Liquid";

    private const string PersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly ConfigService _configService;

    public ThemeManager(ConfigService configService)
    {
        _configService = configService;

        var config = _configService.GetAppConfig();

        Mode = Normalize(config.App.Theme);
        Style = NormalizeStyle(config.App.ThemeStyle);
        Resolved = ResolveBrightness(Mode);
        Palette = Compose(Mode, Style);
    }

    /// <summary>用户选择的明暗：System / Dark / Bright</summary>
    public string Mode { get; private set; } = Dark;

    /// <summary>用户选择的风格：Classic / Windows / Liquid</summary>
    public string Style { get; private set; } = StyleClassic;

    /// <summary>明暗实际解析出的亮度：Dark / Bright</summary>
    public string Resolved { get; private set; } = Dark;

    /// <summary>实际要装载的调色板字典名，如 <c>DarkLiquid</c>。等于 <c>Themes/</c> 下的文件名。</summary>
    public string Palette { get; private set; } = Dark;

    public bool IsSystemMode => Mode == System;
    public bool IsDarkMode => Mode == Dark;
    public bool IsBrightMode => Mode == Bright;

    public bool IsClassicStyle => Style == StyleClassic;
    public bool IsWindowsStyle => Style == StyleWindows;
    public bool IsLiquidStyle => Style == StyleLiquid;

    /// <summary>当前实际生效的亮度是否为暗色</summary>
    public bool IsDarkResolved => Resolved == Dark;

    /// <summary>实际装载的调色板发生变化时触发；模式或风格改了但解析结果不变时不触发。</summary>
    public event Action<string>? ResolvedThemeChanged;

    /// <summary>系统主题发生变化时触发，与当前模式无关；是否重新渲染由监听方决定。</summary>
    public event Action? SystemThemeChanged;

    /// <summary>设置明暗模式并持久化，返回实际装载的调色板名</summary>
    public string ApplyMode(string? mode)
    {
        var normalized = Normalize(mode);

        if (normalized != Mode)
        {
            // 先落配置再切界面状态，配置写失败（磁盘只读、权限不足）时不切换
            var config = _configService.GetAppConfig();
            config.App.Theme = normalized;
            _configService.SaveAppConfig(config);

            Mode = normalized;
        }

        return Recompute();
    }

    /// <summary>设置风格并持久化，返回实际装载的调色板名</summary>
    public string ApplyStyle(string? style)
    {
        var normalized = NormalizeStyle(style);

        if (normalized != Style)
        {
            var config = _configService.GetAppConfig();
            config.App.ThemeStyle = normalized;
            _configService.SaveAppConfig(config);

            Style = normalized;
        }

        return Recompute();
    }

    /// <summary>
    /// 不落盘地算出"切到某个组合会装载哪本字典"。
    /// 设置页用它把三个风格按钮各自会用的主题显示成预览色，点之前就看得见。
    /// </summary>
    public static string PreviewPalette(string? style, string brightness)
        => Compose(brightness == Dark ? Dark : Bright, NormalizeStyle(style), forPreview: true);

    /// <summary>重新解析当前模式；模式不是 System 时不做任何事，调用方无需自行判断。</summary>
    /// <returns>实际装载的调色板名</returns>
    public string Refresh() => Recompute();

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

    /// <summary>重算亮度与调色板，变了就通知</summary>
    private string Recompute()
    {
        var brightness = ResolveBrightness(Mode);

        if (brightness != Resolved) Resolved = brightness;

        var palette = Compose(Mode, Style);
        if (palette == Palette) return Palette;

        Palette = palette;
        ResolvedThemeChanged?.Invoke(palette);

        return Palette;
    }

    /// <summary>把明暗与风格拼成字典名。经典风格不带后缀，见类型注释。</summary>
    private static string Compose(string mode, string style, bool forPreview = false)
    {
        var brightness = forPreview ? mode : ResolveBrightness(mode);

        return style switch
        {
            StyleWindows => brightness + StyleWindows,
            StyleLiquid => brightness + StyleLiquid,
            _ => brightness
        };
    }

    /// <summary>把配置里的字符串收敛到已知模式；值可被用户直接编辑，非法值回退到 Dark。</summary>
    private static string Normalize(string? mode)
    {
        if (string.Equals(mode, Bright, StringComparison.OrdinalIgnoreCase)) return Bright;
        if (string.Equals(mode, System, StringComparison.OrdinalIgnoreCase)) return System;

        return Dark;
    }

    /// <summary>把配置里的字符串收敛到已知风格；非法值回退到经典</summary>
    private static string NormalizeStyle(string? style)
    {
        if (string.Equals(style, StyleWindows, StringComparison.OrdinalIgnoreCase)) return StyleWindows;
        if (string.Equals(style, StyleLiquid, StringComparison.OrdinalIgnoreCase)) return StyleLiquid;

        return StyleClassic;
    }

    private static string ResolveBrightness(string mode)
        => mode == System ? GetSystemTheme() : mode;
}
