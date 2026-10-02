using Microsoft.Extensions.Configuration;
using PaperCoin.Core;
using PaperCoin.Core.Models;
using System.Text.Json;

namespace PaperCoin.Services;

public class ConfigService
{
    private readonly IConfiguration _configuration;
    private readonly string _configFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        // 不转义中文
    };

    public ConfigService()
    {
        Directory.CreateDirectory(Constants.ConfigFolder);

        _configFilePath = Path.Combine(Constants.ConfigFolder, Constants.AppSettingsFile);

        if (!File.Exists(_configFilePath))
        {
            var defaultConfig = new AppConfig();

            // 与 SaveAppConfig 用同一套选项，保证首次生成与后续保存的格式一致
            File.WriteAllText(_configFilePath, JsonSerializer.Serialize(defaultConfig, JsonOptions));
        }

        _configuration = new ConfigurationBuilder()
            .AddJsonFile(_configFilePath, optional: false, reloadOnChange: true)
            .Build();
    }

    /// <summary>读取配置并绑定为强类型对象</summary>
    public AppConfig GetAppConfig()
    {
        var config = new AppConfig();
        _configuration.Bind(config);
        return config;
    }

    /// <summary>更新并保存配置</summary>
    public void SaveAppConfig(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_configFilePath, json);

        // reloadOnChange: true 会自动检测文件变化，无需手动刷新
    }

    /// <summary>触发一次配置重载；<c>reloadOnChange</c> 只在文件内容变化时触发，内容未变时用它强制同步。</summary>
    public void Reload() => (_configuration as IConfigurationRoot)?.Reload();

    /// <summary>读取单个配置值</summary>
    public string GetValue(string key, string defaultValue = "")
    {
        return _configuration[key] ?? defaultValue;
    }
}