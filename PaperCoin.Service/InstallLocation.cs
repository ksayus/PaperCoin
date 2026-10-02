using Microsoft.Win32;
using Serilog;

namespace PaperCoin.Service;

/// <summary>
/// 记录本机的应用安装位置。
/// <para>
/// 安装程序升级时会搜这个键来决定装到哪里：搜到就沿用旧路径，搜不到才用默认值。
/// 这样升级不会把文件装到第二个目录，程序的数据目录（就在程序目录下）也不会跟着搬家。
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
    /// 用 HKCU 而不是 HKLM：本程序是每用户安装，写 HKLM 需要提权。
    /// </summary>
    public static void Record()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key?.SetValue(ValueName, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 组策略可能封锁这个位置。记不上只是下次升级找不到旧路径，不该影响程序启动。
            Log.Warning(ex, "记录安装位置失败");
        }
    }

    /// <summary>读回记录的位置，没有记录时返回 <c>null</c></summary>
    public static string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string;
    }
}
