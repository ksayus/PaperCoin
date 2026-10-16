using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace PaperCoin.Service;

/// <summary>更新源。两个源的 API 形状不同，各自有对应的解析器。</summary>
public enum UpdateSource
{
    /// <summary>GitHub Releases（api.github.com）</summary>
    Github,

    /// <summary>Gitee Releases（gitee.com/api/v5）</summary>
    Gitee
}

/// <summary>
/// 一次成功的版本查询结果。<see cref="InstallerUrl"/> 为空表示这个发布里没有可安装的文件，
/// 那时只能引导用户去发布页手动下载。
/// </summary>
/// <param name="Version">发布里的版本号，已去掉前缀 v</param>
/// <param name="TagName">原始 tag，如 <c>v1.3.6</c></param>
/// <param name="Notes">发布说明正文，用于在界面上直接展示更新内容</param>
/// <param name="PageUrl">发布页地址，供"打不开时手动下载"</param>
/// <param name="InstallerUrl">安装包下载地址；为空表示这个发布里没有安装包</param>
/// <param name="InstallerName">安装包文件名，用于决定怎么启动它</param>
public sealed record UpdateRelease(
    Version Version,
    string TagName,
    string Notes,
    string PageUrl,
    string? InstallerUrl,
    string? InstallerName);

/// <summary>查询结果：要么拿到一个发布，要么拿到一句能直接显示给用户的原因。</summary>
/// <param name="Release">查到的发布；失败时为 null</param>
/// <param name="Message">给用户看的一句话。成功时说明结果，失败时说明原因。</param>
/// <param name="IsFailure">是否属于"没查成"（网络、限流、响应看不懂），而不是"确实没有新版"</param>
public sealed record UpdateCheck(UpdateRelease? Release, string Message, bool IsFailure)
{
    public static UpdateCheck Ok(UpdateRelease release, string message) => new(release, message, false);

    public static UpdateCheck Failed(string message) => new(null, message, true);
}

/// <summary>
/// 自动更新的查询与下载。
/// <para>
/// 只依赖 <see cref="HttpMessageHandler"/>，所以测试可以塞一个假的处理器进来，
/// 不需要真的联网、也不需要起 HTTP 服务。
/// </para>
/// <para>
/// 这里**不做**界面交互：下载到哪、怎么启动安装程序都由调用方决定。
/// </para>
/// </summary>
public sealed class AppUpdater
{
    /// <summary>下载安装包时的进度回调，参数是已下载字节数与总字节数（总长未知时为 -1）</summary>
    public IProgress<(long Done, long Total)>? Progress { get; set; }

    private readonly HttpClient _http;
    private readonly Func<UpdateSource, string> _endpointOf;

    /// <summary>
    /// 最近一次"确实发现了更新"的结果。
    /// <para>
    /// 放在这里而不是放在设置页的 ViewModel 里：<c>SettingsViewModel</c> 是 transient 的，
    /// 每次进设置页都会新建一个，启动时后台查到的新版本会因此丢掉 —— 用户永远看不到。
    /// 检查结果属于"应用级状态"，就该由单例持有。
    /// </para>
    /// </summary>
    public UpdateRelease? Available { get; private set; }

    /// <summary>最近一次查询给用户的那句话</summary>
    public string LastMessage { get; private set; } = "";

    /// <summary>记下一次查询结果。设置页每次构造时读它，于是启动检查的结果也能显示。</summary>
    internal void Remember(UpdateCheck check, Version current)
    {
        LastMessage = check.Message;

        Available = check.Release is { } release && release.Version > current ? release : null;
    }

    /// <summary>查询并记下结果</summary>
    public async Task<UpdateCheck> CheckAndRememberAsync(
        UpdateSource source, Version current, CancellationToken ct = default)
    {
        var check = await CheckAsync(source, current, ct);

        Remember(check, current);

        return check;
    }

    /// <summary>用真实网络构造。<paramref name="handler"/> 传 null 时用默认的。</summary>
    public AppUpdater(HttpMessageHandler? handler = null)
        : this(handler, source => source switch
        {
            UpdateSource.Gitee => "https://gitee.com/api/v5/repos/ksayus/PaperCoin/releases/latest",
            _ => "https://api.github.com/repos/ksayus/PaperCoin/releases/latest"
        })
    {
    }

    /// <summary>测试用：可指定解析出来的地址，于是能对着假处理器断言"地址取对了没有"</summary>
    internal AppUpdater(HttpMessageHandler? handler, Func<UpdateSource, string> endpointOf)
    {
        _endpointOf = endpointOf;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);

        // GitHub 要求带 User-Agent，否则直接 403；Gitee 也接受它。
        // Accept 指明要 v3 的 JSON，避免拿到别的内容类型。
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Core.Constants.AppName}/{Core.Constants.AppVersion}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>
    /// 查询最新发布，并判断它是否比 <paramref name="current"/> 新。
    /// <para>
    /// 任何失败都收敛成 <see cref="UpdateCheck"/>，不抛异常 —— 更新检查失败绝不该影响程序使用。
    /// </para>
    /// </summary>
    public async Task<UpdateCheck> CheckAsync(
        UpdateSource source, Version current, CancellationToken ct = default)
    {
        string json;

        try
        {
            using var response = await _http.GetAsync(_endpointOf(source), ct);

            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;

                // 404 表示仓库一个发布都没有，这跟网络不通是两回事，不该说成"检查失败"
                if (code == 404)
                    return UpdateCheck.Ok(
                        new UpdateRelease(current, "", "", "", null, null), "还没有发布过任何版本");

                // 403 / 429 在 GitHub 上通常是匿名调用的限流
                if (code is 403 or 429)
                    return UpdateCheck.Failed($"查询被拒绝（HTTP {code}），稍后再试");

                return UpdateCheck.Failed($"查询失败（HTTP {code}）");
            }

            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return UpdateCheck.Failed("查询超时，检查网络后重试");
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheck.Failed($"连不上{SourceName(source)}（{ex.Message}）");
        }

        if (!TryParse(json, source, out var release, out var why))
            return UpdateCheck.Failed(why);

        if (release.Version > current)
            return UpdateCheck.Ok(release, $"发现新版本 {release.Version}（当前 {current}）");

        return UpdateCheck.Ok(release, $"已是最新版本（{current}）");
    }

    /// <summary>
    /// 从 <c>/releases/latest</c> 的响应里解析出需要的东西。
    /// 这里刻意不用 JSON 反序列化到强类型：两个源的字段名与结构有差异，
    /// 而响应里还有大量用不到的字段，按需取值比维护两个 DTO 更稳。
    /// </summary>
    internal static bool TryParse(
        string json, UpdateSource source, out UpdateRelease release, out string failure)
    {
        release = new UpdateRelease(new Version(0, 0), "", "", "", null, null);
        failure = "";

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            failure = "更新源返回的内容看不懂（不是合法 JSON）";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                failure = "更新源返回的内容看不懂（不是对象）";
                return false;
            }

            string tag = GetString(root, "tag_name");

            if (string.IsNullOrWhiteSpace(tag))
            {
                // 空仓库时 GitHub 返回的是 {"message":"Not Found"}，走到这里说明确实没有发布
                failure = "更新源没有返回版本号，可能还没有发布过任何版本";
                return false;
            }

            if (!TryParseVersion(tag, out var version))
            {
                failure = $"更新源的 tag「{tag}」不是版本号，无法比较";
                return false;
            }

            // name 常常就是 tag；取不到就退回 tag，界面上总得有个标题
            string name = GetString(root, "name");
            string notes = GetString(root, "body");
            string page = GetString(root, "html_url");

            var (url, fileName) = PickInstaller(root);

            release = new UpdateRelease(version, tag, notes, page, url, fileName);
            return true;
        }
    }

    /// <summary>
    /// 从资产列表里挑安装包。优先 <c>setup.exe</c>，其次 MSI ——
    /// setup.exe 自带引导界面，双击即用；MSI 要交给 msiexec。
    /// </summary>
    private static (string? Url, string? Name) PickInstaller(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null);

        string? fallbackUrl = null;
        string? fallbackName = null;

        foreach (var asset in assets.EnumerateArray())
        {
            string name = GetString(asset, "name");
            if (string.IsNullOrEmpty(name)) continue;

            string url = GetString(asset, "browser_download_url");
            if (string.IsNullOrEmpty(url)) continue;

            if (name.Equals("setup.exe", StringComparison.OrdinalIgnoreCase))
                return (url, name);

            if (fallbackUrl is null && name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                fallbackUrl = url;
                fallbackName = name;
            }
        }

        return (fallbackUrl, fallbackName);
    }

    /// <summary>
    /// 把 tag 解析成可比较的版本号。
    /// 接受 <c>v1.3.6</c> / <c>1.3.6</c> / <c>1.3</c>；不足三段的补零，
    /// 这样 <c>1.3</c> 与 <c>1.3.0</c> 比得出相等，而不是被判成"有新版"。
    /// </summary>
    internal static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0);

        var text = tag.Trim().TrimStart('v', 'V');

        // 去掉 -beta / +build 这类后缀，只比数字部分
        int cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0) text = text[..cut];

        if (!Version.TryParse(text, out var parsed)) return false;

        version = new Version(
            Math.Max(parsed.Major, 0),
            Math.Max(parsed.Minor, 0),
            Math.Max(parsed.Build, 0));

        return true;
    }

    /// <summary>把安装包下载到指定路径，并报告进度</summary>
    /// <returns>成功时为空字符串，失败时是给用户看的原因</returns>
    public async Task<string> DownloadAsync(string url, string targetPath, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                return $"下载失败（HTTP {(int)response.StatusCode}）";

            long total = response.Content.Headers.ContentLength ?? -1;
            long done = 0;

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(targetPath);

            var buffer = new byte[81920];

            while (true)
            {
                int read = await source.ReadAsync(buffer, ct);
                if (read <= 0) break;

                await target.WriteAsync(buffer.AsMemory(0, read), ct);

                done += read;
                Progress?.Report((done, total));
            }

            return "";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return "下载超时，检查网络后重试";
        }
        catch (HttpRequestException ex)
        {
            return $"下载失败（{ex.Message}）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "写不进下载目录，检查磁盘空间与权限";
        }
    }

    /// <summary>
    /// 启动安装程序。
    /// <para>
    /// <b>不在这里等它结束</b>：MSI 在升级前会要求关掉正在运行的本程序，
    /// 而"关掉"这件事只能由调用方决定怎么配合。职责分开，这里只负责把包交给系统。
    /// </para>
    /// </summary>
    /// <returns>成功时为空字符串，失败时是给用户看的原因</returns>
    public static string LaunchInstaller(string installerPath)
    {
        try
        {
            var info = installerPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("msiexec", $"/i \"{installerPath}\"")
                : new ProcessStartInfo(installerPath);

            // MSI 要用 ShellExecute 才会弹 UAC。本程序是每机器安装，升级需要管理员权限。
            info.UseShellExecute = true;

            Process.Start(info);
            return "";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"无法启动安装程序（{ex.Message}）";
        }
    }

    /// <summary>下载文件放哪里。用临时目录，升级失败也不会留下半个安装包占着程序目录。</summary>
    public static string DownloadPathFor(string? installerName)
    {
        var name = string.IsNullOrWhiteSpace(installerName) ? "PaperCoin-Update.msi" : installerName!;
        var dir = Path.Combine(Path.GetTempPath(), "PaperCoin-Update");

        Directory.CreateDirectory(dir);

        return Path.Combine(dir, name);
    }

    private static string SourceName(UpdateSource source)
        => source == UpdateSource.Gitee ? "Gitee" : "GitHub";

    private static string GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
