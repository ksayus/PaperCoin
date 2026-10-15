using Microsoft.Win32;
using Serilog;

namespace PaperCoin.Service;

/// <summary>
/// 把 1.2.x 留在程序目录下的数据搬到新的数据目录。
/// <para>
/// 1.2.x 的数据根是 <c>&lt;程序目录&gt;\PaperCoin</c>（跟着安装位置走）；1.3.0 起到
/// <c>%APPDATA%\PaperCoin</c>。不搬的话，升级上来的用户会看到一份空账 —— 数据其实还在，
/// 只是没人去那儿找了。
/// </para>
/// <para>
/// 只搬一次：目标目录已经有内容就不再动。搬不动（旧目录不存在、权限不足）时只是记一条日志，
/// 不能影响启动 —— 全新安装本来就没有旧数据可搬。
/// </para>
/// </summary>
public static class AppDataMigration
{
    /// <summary>要搬的子目录，即 <c>Constants.AppDataFolder</c> 下的全部内容</summary>
    private static readonly string[] Folders = ["Config", "Logs", "Bills", "Export"];

    /// <summary>
    /// 执行迁移。必须在任何服务读写数据目录**之前**调用（见 <c>App.OnStartup</c>）。
    /// </summary>
    /// <returns>是否真的搬了东西</returns>
    public static bool Run()
    {
        try
        {
            var target = PaperCoin.Core.Constants.AppDataFolder;

            // 目标已经有账单，说明搬过了或者本来就是新装：再搬会覆盖用户当前的账
            if (Directory.Exists(Path.Combine(target, "Bills"))
                && Directory.EnumerateFileSystemEntries(Path.Combine(target, "Bills")).Any())
                return false;

            var source = FindLegacyRoot();
            if (source is null) return false;

            int moved = 0;
            foreach (var name in Folders)
            {
                var from = Path.Combine(source, name);
                if (!Directory.Exists(from)) continue;

                moved += CopyTree(from, Path.Combine(target, name));
            }

            if (moved == 0) return false;

            Log.Information("已从旧数据目录迁移 {Count} 个文件: {From} -> {To}", moved, source, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 迁移失败只是数据留在旧位置，程序照常从新目录开始，不该因此起不来
            Log.Warning(ex, "旧数据目录迁移失败，本次从新目录开始");
            return false;
        }
    }

    /// <summary>
    /// 找 1.2.x 的数据根（<c>&lt;程序目录&gt;\PaperCoin</c>）。
    /// <para>
    /// 程序目录要连**旧**安装位置一起看：升级换过安装位置时，新程序目录下没有旧数据，
    /// 而注册表里记着上一版装在哪。两个都查过才不至于漏掉。
    /// </para>
    /// </summary>
    private static string? FindLegacyRoot()
    {
        var current = Path.Combine(AppContext.BaseDirectory, "PaperCoin");
        if (HasAnyData(current)) return current;

        var recorded = InstallLocation.Read();
        if (!string.IsNullOrWhiteSpace(recorded))
        {
            var previous = Path.Combine(recorded, "PaperCoin");
            if (HasAnyData(previous)) return previous;
        }

        return null;
    }

    private static bool HasAnyData(string root)
        => Folders.Any(name =>
            Directory.Exists(Path.Combine(root, name))
            && Directory.EnumerateFileSystemEntries(Path.Combine(root, name)).Any());

    /// <summary>递归复制，同名文件不覆盖（目标里已有的更新）。返回复制的文件数。</summary>
    private static int CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        int count = 0;

        foreach (var file in Directory.EnumerateFiles(from))
        {
            var destination = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(destination)) continue;

            File.Copy(file, destination);
            count++;
        }

        foreach (var dir in Directory.EnumerateDirectories(from))
            count += CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));

        return count;
    }
}
