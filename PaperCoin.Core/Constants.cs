namespace PaperCoin.Core;

public static class Constants
{
    public const string AppName = "PaperCoin";

    /// <summary>
    /// 产品版本，取自本程序集的版本号，单一来源见根目录的 Directory.Build.props。
    /// 这里不留字面量：写死的版本号一旦和程序集版本不一致，MSI 就会按错误的版本判断要不要覆盖文件。
    /// </summary>
    public static readonly string AppVersion =
        typeof(Constants).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// 运行时数据根目录。
    /// <para>
    /// 放在 <c>%APPDATA%\PaperCoin</c> 而不是程序目录下：本程序现在装到
    /// <c>Program Files</c>（每机器安装），那里普通权限写不进去；而且用户改了安装路径后
    /// 数据不该跟着"看不见"。
    /// </para>
    /// <para>
    /// 1.2.x 的数据在程序目录下的 <c>PaperCoin\</c>，首次启动由
    /// <see cref="PaperCoin.Service.AppDataMigration"/> 搬过来。
    /// </para>
    /// </summary>
    public static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        AppName);
    public static readonly string ConfigFolder = Path.Combine(AppDataFolder, "Config");
    public static readonly string LogsFolder = Path.Combine(AppDataFolder, "Logs");
    public static readonly string BillFolder = Path.Combine(AppDataFolder, "Bills");

    public static string AppSettingsFile = "papercoin-settings.json";
    public static string LogFile = "papercoin-.log";
}
