/// <summary>程序配置</summary>
namespace PaperCoin.Core.Models;

public class AppConfig
{
    public AppSection App { get; set; } = new();
    public LoggingSection Logging { get; set; } = new();
}

public class AppSection
{
    public bool AutoUpdate { get; set; } = new();
    public string AutoUpdateSource { get; set; } = "Github";
    public bool AutoStart { get; set; } = true;
    public string Theme { get; set; } = "Dark";
}

public class LoggingSection
{
    public string Level { get; set; } = "Infomation";
    public int RetainDays { get; set; } = 7;
}