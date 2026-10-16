using System.Runtime.InteropServices;

namespace PaperCoin;

/// <summary>
/// 窗口背后的真实高斯模糊（DWM 系统背板）。
/// <para>
/// WPF 没有背部模糊的原生能力：<see cref="System.Windows.Media.Effects.BlurEffect"/> 只能模糊
/// 元素自己的内容，模糊不了窗口背后的桌面。真背部模糊要交给 DWM —— 它拿得到屏幕内容，
/// 能把桌面按高斯核模糊后填进窗口背景。
/// </para>
/// <para>
/// <b>硬约束</b>：系统背板只对**非分层窗口**生效。WPF 里分层窗口由
/// <c>AllowsTransparency=True</c> 打开，而它同时带来圆角裁剪与 <c>DropShadowEffect</c> 的能力。
/// 二者不可兼得，所以本程序的做法是：
/// </para>
/// <list type="bullet">
/// <item>液态玻璃风格：关掉分层窗口，用 DWM 背板取真模糊，圆角交给 DWM，不用 WPF 阴影。</item>
/// <item>其它风格：表面本就不透明，继续用分层窗口，保留圆角裁剪与 WPF 阴影。</item>
/// </list>
/// <para>
/// 系统背板需要 Windows 11（build 22000 起）。更早的系统上 <see cref="IsSupported"/> 为
/// <c>false</c>，主题那边会退回不透明背板，不会出现"模糊没生效、面板还半透明"的糊字。
/// </para>
/// </summary>
internal static class BackdropBlur
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE，Windows 11 上让边框跟随暗色</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE</summary>
    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE</summary>
    private const int DwmwaSystemBackdropType = 38;

    /// <summary>
    /// DWMWA_BACKDROP_TINT_COLOR。**不要依赖它**：Windows 11 build 26300 上实测被拒
    /// （DwmSetWindowAttribute 返回非 0）。模糊层的明度统一由窗口内的 ColorGlassScrim 控制。
    /// </summary>
    private const int DwmwaBackdropTintColor = 34;

    // DWM_SYSTEMBACKDROP_TYPE
    private const int DwmsbtAuto = 0;
    private const int DwmsbtNone = 1;
    private const int DwmsbtMainWindow = 2;      // Mica
    private const int DwmsbtTransientWindow = 3; // Acrylic
    private const int DwmsbtTabbedWindow = 4;    // Mica Alt

    // DWM_WINDOW_CORNER_PREFERENCE
    private const int DwmcpDefault = 0;
    private const int DwmcpDoNotRound = 1;
    private const int DwmcpRound = 2;

    /// <summary>本机是否支持系统背板（Windows 11 build 22000 及以上）</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>最近一次应用的背板类型，供测试断言"真的设下去了"</summary>
    internal static int AppliedBackdropType { get; private set; } = DwmsbtNone;

    /// <summary>
    /// 让窗口背景变成"背后桌面的高斯模糊"。调用方必须保证窗口不是分层窗口，否则调用无效。
    /// </summary>
    /// <param name="handle">窗口句柄</param>
    /// <param name="dark">是否按暗色画窗口边框</param>
    /// <returns>是否生效</returns>
    public static bool Apply(IntPtr handle, bool dark)
    {
        if (handle == IntPtr.Zero || !IsSupported) return false;

        try
        {
            // Acrylic（TransientWindow）：比 Mica 的模糊更明显，且会把窗口背后的**任意**内容
            // 真正模糊掉。Mica 只采样桌面壁纸，窗口拖到别的应用上时不会跟着变。
            int backdrop = DwmsbtTransientWindow;
            int hr = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

            if (hr != 0)
            {
                // 旧一点的 Win11 上没有这个属性，退回 Mica；再不行就当作不支持
                backdrop = DwmsbtMainWindow;
                hr = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
                if (hr != 0) return false;
            }

            AppliedBackdropType = backdrop;

            // 顺带试一下 DWM 色调（有些版本接受，能把模糊层直接压住）。
            // 被拒不算失败 —— 明度由窗口内的 ColorGlassScrim 兜底，这里只如实记录结果。
            int tintValue = unchecked((int)DefaultTint);
            int tintHr = DwmSetWindowAttribute(handle, DwmwaBackdropTintColor, ref tintValue, sizeof(int));
            AppliedTint = tintHr == 0 ? DefaultTint : 0u;
            AppliedTintAccepted = tintHr == 0;

            int corner = DwmcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));

            int darkMode = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

            return true;
        }
        catch (DllNotFoundException)
        {
            // 理论上不会到这里（IsSupported 已经挡了），保险起见不抛
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>尝试设给 DWM 的色调。真正的明度控制看 ColorGlassScrim。</summary>
    private const uint DefaultTint = 0x40000000;

    /// <summary>最近一次实际设下去的 DWM 色调；0 表示 DWM 拒绝了</summary>
    internal static uint AppliedTint { get; private set; }

    /// <summary>DWM 是否接受了色调设置</summary>
    internal static bool AppliedTintAccepted { get; private set; }

    /// <summary>关掉系统背板，回到普通窗口背景</summary>
    public static void Clear(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsSupported) return;

        int backdrop = DwmsbtNone;
        DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        AppliedBackdropType = backdrop;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}
