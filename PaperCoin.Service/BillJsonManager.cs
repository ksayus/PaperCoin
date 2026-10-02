using PaperCoin.Core;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PaperCoin.Service;

public class BillJsonManager
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 不转义中文
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ToJSON<T>(T value)
        => JsonSerializer.Serialize(value, Options);

    public static T? FromJSON<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options);

    public static async Task SaveToFileAsync<T>(string path, T value, CancellationToken ct = default)
    => await File.WriteAllTextAsync(path, ToJSON(value), ct);

    public static async Task<T?> LoadFromFileAsync<T>(string path, CancellationToken ct = default)
        => File.Exists(path) ? FromJSON<T>(await File.ReadAllTextAsync(path, ct)) : default;

    public static string GetSavePath()
    {
        DateTime date = DateTime.Now;
        string yearFolder = Path.Combine(Constants.BillFolder, date.Year.ToString());
        string monthFolder = Path.Combine(yearFolder, date.Month.ToString());
        string dayFolder = Path.Combine(monthFolder, date.Day.ToString());

        Directory.CreateDirectory(yearFolder);
        Directory.CreateDirectory(monthFolder);
        Directory.CreateDirectory(dayFolder);

        return dayFolder;
    }
}
