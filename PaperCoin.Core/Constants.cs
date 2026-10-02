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

    public static readonly string AppDataFolder = Path.Combine(Environment.CurrentDirectory, "PaperCoin");
    public static readonly string ConfigFolder = Path.Combine(AppDataFolder, "Config");
    public static readonly string LogsFolder = Path.Combine(AppDataFolder, "Logs");
    public static readonly string BillFolder = Path.Combine(AppDataFolder, "Bills");

    public static string AppSettingsFile = "papercoin-settings.json";
    public static string LogFile = "papercoin-.log";
}
