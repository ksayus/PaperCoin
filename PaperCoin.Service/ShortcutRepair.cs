using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Serilog;

namespace PaperCoin.Service;

/// <summary>
/// 修正安装出来的桌面快捷方式的图标。
/// <para>
/// 安装项目写进 MSI <c>Icon</c> 表的是 <c>.ico</c> 的原始字节，条目名却带 <c>.exe</c> 后缀
/// （MSI 规定与快捷方式关联的图标必须是 PE 格式、且扩展名与快捷方式目标一致）。安装时
/// Windows Installer 据此在 <c>%APPDATA%\Microsoft\Installer\{ProductCode}\</c> 写出一个
/// 9.8KB 的假 exe，壳按 PE 加载必然失败，快捷方式与"应用和功能"就都没有图标。
/// 这是安装项目那一侧的硬约束，没有可用的构建期出口（见 README「快捷方式图标」）。
/// </para>
/// <para>
/// 所以改成运行期修正：把快捷方式的 <c>IconLocation</c> 指向一份真实的
/// <c>papercoin-icon.ico</c>。图标内嵌在本程序集里，修正时按需落盘到用户可写的位置，
/// 不依赖安装目录里是否躺着那个文件。动作幂等——图标已经对了就不写盘，也不会反复去动用户的桌面。
/// </para>
/// </summary>
public static class ShortcutRepair
{
    /// <summary>安装项目建的快捷方式文件名（不含扩展名）</summary>
    private const string ShortcutName = "PaperCoin.lnk";

    /// <summary>内嵌图标资源名（见 PaperCoin.csproj 的 LogicalName）</summary>
    private const string IconResourceName = "PaperCoin.Icon.papercoin-icon.ico";

    /// <summary>随程序可执行文件一起出现的图标文件名（安装目录里就是这样）</summary>
    private const string IconFileName = "papercoin-icon.ico";

    // ---- IShellLink COM 接口 ----

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
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
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    // ---- 入口 ----

    /// <summary>
    /// 修正当前用户桌面上的快捷方式。失败只记日志：这属于外观修正，
    /// 不该因为它让程序起不来。
    /// </summary>
    public static void RepairDesktopShortcut()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Repair(Path.Combine(desktop, ShortcutName));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "修正桌面快捷方式图标失败");
        }
    }

    /// <summary>
    /// 把指定快捷方式的图标指向一份真实的 <c>papercoin-icon.ico</c>。
    /// 快捷方式不存在、或图标已经正确时不写盘。
    /// </summary>
    /// <returns>真的改写了返回 <c>true</c>。</returns>
    public static bool Repair(string shortcutPath)
    {
        if (!File.Exists(shortcutPath)) return false;

        var iconPath = EnsureIconFile();

        if (IconAlreadyCorrect(shortcutPath, iconPath)) return false;

        // 重建而不是就地改。这条是踩出来的：
        // 对 Load 进来的链调 SetIconLocation 再 Save，写盘后快捷方式的**目标**会落成
        // 图标文件的路径，快捷方式就点不开程序了（新建的链不会，从磁盘 Load 的会）。
        // 改成用一个全新的链对象把目标、起始目录、图标一次写全，读回什么就是什么。
        var exePath = Path.Combine(AppContext.BaseDirectory, "PaperCoin.exe");
        if (!File.Exists(exePath)) return false;

        var shellLink = (IShellLinkW)new ShellLinkObject();
        shellLink.SetPath(exePath);
        shellLink.SetWorkingDirectory(AppContext.BaseDirectory);
        shellLink.SetIconLocation(iconPath, 0);

        ((IPersistFile)shellLink).Save(shortcutPath, true);

        Log.Information("已修正快捷方式: {Shortcut} 目标={Target} 图标={Icon}", shortcutPath, exePath, iconPath);
        return true;
    }

    /// <summary>只读判断图标是否已经是我们要的那个，避免无谓地重写用户的快捷方式。</summary>
    private static bool IconAlreadyCorrect(string shortcutPath, string iconPath)
    {
        var shellLink = (IShellLinkW)new ShellLinkObject();
        ((IPersistFile)shellLink).Load(shortcutPath, 0);

        var current = new System.Text.StringBuilder(260);
        shellLink.GetIconLocation(current, current.Capacity, out _);
        return string.Equals(current.ToString(), iconPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 拿到一份磁盘上的图标文件路径，必要时从内嵌资源写出来。
    /// 优先用程序目录里的那份（安装包装出来的就是它）；目录不可写时退回
    /// <c>%LOCALAPPDATA%\PaperCoin</c> —— 两个位置都在用户配置下，不需要提权。
    /// </summary>
    public static string EnsureIconFile()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, IconFileName);
        if (File.Exists(beside)) return beside;

        var fallbackDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PaperCoin");
        Directory.CreateDirectory(fallbackDir);
        var fallback = Path.Combine(fallbackDir, IconFileName);

        if (File.Exists(fallback)) return fallback;

        using var stream = typeof(ShortcutRepair).Assembly
            .GetManifestResourceStream(IconResourceName)
            ?? throw new InvalidOperationException($"程序集里没有内嵌图标资源: {IconResourceName}");

        using var file = File.Create(fallback);
        stream.CopyTo(file);

        return fallback;
    }
}
