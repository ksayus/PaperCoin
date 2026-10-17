using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaperCoin.Core;

namespace PaperCoin.Service.LanSync;

/// <summary>
/// 已配对设备。Baseline 是上一次成功同步后该设备那一侧的 Id -&gt; 内容哈希。
/// </summary>
public sealed class SyncPeer
{
    public string DeviceId { get; set; } = "";

    public string DeviceName { get; set; } = "";

    /// <summary>Unix epoch milliseconds (UTC)</summary>
    public long LastSyncAt { get; set; }

    public Dictionary<string, string> Baseline { get; set; } = new(StringComparer.Ordinal);

    public SyncPeer Clone() => new()
    {
        DeviceId = DeviceId,
        DeviceName = DeviceName,
        LastSyncAt = LastSyncAt,
        Baseline = new Dictionary<string, string>(Baseline, StringComparer.Ordinal)
    };
}

/// <summary>
/// <c>%APPDATA%\PaperCoin\Sync\papercoin-sync.json</c> 的内存模型，字段与协议文档的 schema 一致。
/// </summary>
public sealed class SyncState
{
    public bool Enabled { get; set; }

    public bool AutoSync { get; set; } = true;

    public string Code { get; set; } = "";

    public string DeviceId { get; set; } = Guid.NewGuid().ToString("D");

    public string DeviceName { get; set; } = DefaultDeviceName();

    public List<SyncPeer> Peers { get; set; } = [];

    /// <summary>所有已知设备里最近一次成功同步的时间；没有同步过时为 0。</summary>
    [JsonIgnore]
    public long LastSyncAt => Peers.Count == 0 ? 0 : Peers.Max(p => p.LastSyncAt);

    public SyncPeer? FindPeer(string deviceId)
        => Peers.FirstOrDefault(p => string.Equals(p.DeviceId, deviceId, StringComparison.Ordinal));

    public SyncState Clone() => new()
    {
        Enabled = Enabled,
        AutoSync = AutoSync,
        Code = Code,
        DeviceId = DeviceId,
        DeviceName = DeviceName,
        Peers = Peers.Select(p => p.Clone()).ToList()
    };

    internal static string DefaultDeviceName()
    {
        var name = Environment.MachineName?.Trim();

        if (string.IsNullOrEmpty(name)) name = "Windows PC";

        return name.Length <= SyncStore.MaxDeviceNameLength
            ? name
            : name[..SyncStore.MaxDeviceNameLength];
    }
}

/// <summary>
/// 状态文件的读写。所有 Save/Load 都走同一把信号量，写盘用“同目录临时文件 + 替换”，
/// 避免进程中途退出留下半个 JSON。
/// </summary>
public sealed class SyncStore
{
    public const int MinCodeLength = 6;
    public const int MaxCodeLength = 12;
    public const int MaxDeviceNameLength = 48;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(Constants.AppDataFolder, "Sync", "papercoin-sync.json")
            : Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public async Task<SyncState> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existed = File.Exists(FilePath);
            SyncState? state = null;

            if (existed)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(json))
                        state = JsonSerializer.Deserialize<SyncState>(json, JsonOptions);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    Serilog.Log.Warning(ex, "同步状态文件读取失败，使用默认值: {Path}", FilePath);
                }
            }

            var normalized = Normalize(state);

            // 首次运行就把生成的设备 Id 落盘，否则每次启动都会换一个身份。
            if (!existed)
                await SaveCoreAsync(normalized, ct).ConfigureAwait(false);

            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SyncState state, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(Normalize(state), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveCoreAsync(SyncState state, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        var json = JsonSerializer.Serialize(state, JsonOptions);

        try
        {
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
                // 清理失败不应盖住真正的写入异常
            }

            throw;
        }
    }

    /// <summary>
    /// 把手工改过 / 旧版本留下的状态收敛到协议允许的范围。
    /// 设备 Id 与基线键都用小写 GUID；非法编码直接清空并关闭同步。
    /// </summary>
    public static SyncState Normalize(SyncState? state)
    {
        state ??= new SyncState();

        if (!Guid.TryParse(state.DeviceId, out var id))
            id = Guid.NewGuid();

        state.DeviceId = id.ToString("D");

        var name = (state.DeviceName ?? "").Trim();
        if (name.Length == 0) name = SyncState.DefaultDeviceName();
        if (name.Length > MaxDeviceNameLength) name = name[..MaxDeviceNameLength];
        state.DeviceName = name;

        var code = (state.Code ?? "").Trim().ToUpperInvariant();
        state.Code = IsValidCode(code) ? code : "";

        // 协议：code 为空 = 同步关闭
        if (state.Code.Length == 0) state.Enabled = false;

        state.Peers ??= [];

        var peers = new List<SyncPeer>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var peer in state.Peers)
        {
            if (peer is null || !Guid.TryParse(peer.DeviceId, out var peerId)) continue;

            var peerDeviceId = peerId.ToString("D");
            if (!seen.Add(peerDeviceId)) continue;

            var peerName = (peer.DeviceName ?? "").Trim();
            if (peerName.Length == 0) peerName = "未知设备";
            if (peerName.Length > MaxDeviceNameLength) peerName = peerName[..MaxDeviceNameLength];

            var baseline = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (entryId, hash) in peer.Baseline ?? [])
            {
                if (!Guid.TryParse(entryId, out var parsedId)) continue;
                if (!LanSyncHash.IsHexHash(hash)) continue;

                baseline[parsedId.ToString("D")] = hash.ToLowerInvariant();
            }

            peers.Add(new SyncPeer
            {
                DeviceId = peerDeviceId,
                DeviceName = peerName,
                LastSyncAt = Math.Max(0, peer.LastSyncAt),
                Baseline = baseline
            });
        }

        state.Peers = peers
            .OrderBy(p => p.DeviceId, StringComparer.Ordinal)
            .ToList();

        return state;
    }

    public static bool IsValidCode(string? code)
        => code is { Length: >= MinCodeLength and <= MaxCodeLength }
           && code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');

    public static bool IsValidDeviceName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.Length is >= 1 and <= MaxDeviceNameLength;
    }

    /// <summary>把编码归一成协议格式；调用方仍应先用 <see cref="IsValidCode"/> 决定提示文案。</summary>
    public static string NormalizeCode(string? code)
        => (code ?? "").Trim().ToUpperInvariant();

    /// <summary>Unix epoch milliseconds（UTC），用于状态文件里的 lastSyncAt。</summary>
    public static long NowUnixMilliseconds()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string FormatLastSync(long unixMilliseconds)
    {
        if (unixMilliseconds <= 0) return "从未同步";

        var local = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

        return local;
    }
}
