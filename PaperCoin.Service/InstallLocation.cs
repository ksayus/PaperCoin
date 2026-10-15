using Microsoft.Win32;
using Serilog;

namespace PaperCoin.Service;

/// <summary>
/// 记录本机的应用安装位置。
/// <para>
/// 安装程序升级时会搜这个键来决定装到哪里：搜到就沿用旧路径，搜不到才用默认值。
/// 这样升级不会把文件装到第二个目录。
/// </para>
/// <para>
/// <see cref="KeyPath"/> 与 <see cref="ValueName"/> 是和安装包约定好的：
/// 安装包那边写在 PaperCoin.Setup.vdproj 的 "Locator" 节点里，改一处必须同步改另一处。
/// </para>
/// </summary>
public static class InstallLocation
{
    /// <summary>安装包搜索用的注册表路径</summary>
    public const string KeyPath = @"Software\Ksayus\PaperCoin";

    /// <summary>安装包搜索用的值名</summary>
    public const string ValueName = "InstallDir";

    /// <summary>
    /// 把当前安装位置写进注册表，供下一次升级识别。
    /// <para>
    /// 写 HKLM：1.3.0 起是每机器安装（装到 <c>Program Files</c>，安装时提权），
    /// 安装包的 RegLocator 也改成搜 HKLM。1.2.x 写的是 HKCU，安装包里同时保留了一个
    /// HKCU 定位器，好在从旧版升级上来时还能认出旧路径。
    /// </para>
    /// </summary>
    public static void Record()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
            key?.SetValue(ValueName, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 程序不以管理员身份运行时写不进 HKLM。记不上只是下次升级找不到旧路径，
            // 不该影响程序启动 —— 安装包那边的 HKCU 定位器还能兜住旧版本。
            Log.Warning(ex, "记录安装位置失败（需要管理员权限写 HKLM）");
        }
    }

    /// <summary>
    /// 读回记录的位置，没有记录时返回 <c>null</c>。
    /// HKLM 优先，找不到再看 HKCU —— 后者是 1.2.x 留下的。
    /// </summary>
    public static string? Read()
    {
        using (var machine = Registry.LocalMachine.OpenSubKey(KeyPath))
        {
            if (machine?.GetValue(ValueName) is string fromMachine) return fromMachine;
        }

        using var user = Registry.CurrentUser.OpenSubKey(KeyPath);
        return user?.GetValue(ValueName) as string;
    }
}
