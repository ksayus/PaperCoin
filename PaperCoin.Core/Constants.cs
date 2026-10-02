namespace PaperCoin.Core;

public static class Constants
{
    public const string AppName = "PaperCoin";
    public const string AppVersion = "1.0.0";

    public static readonly string AppDataFolder = Path.Combine(Environment.CurrentDirectory, "PaperCoin");
    public static readonly string ConfigFolder = Path.Combine(AppDataFolder, "Config");
    public static readonly string LogsFolder = Path.Combine(AppDataFolder, "Logs");
    public static readonly string BillFolder = Path.Combine(AppDataFolder, "Bills");

    public static string AppSettingsFile = "papercoin-settings.json";
    public static string LogFile = "papercoin-.log";
}
