using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaperCoin.Core;

namespace PaperCoin.Service.LanSync;

/// <summary>
/// 线缆消息。协议里所有字段都是可选的，因此这里用一个宽松的 DTO 承载所有类型；
/// 未知字段由 System.Text.Json 自动忽略。
/// </summary>
public sealed class SyncMessage
{
    public const int ProtocolVersion = 1;
    public const string AppId = "windows";

    public string? Type { get; set; }
    public int? Protocol { get; set; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? App { get; set; }
    public string? Version { get; set; }
    public int? TcpPort { get; set; }
    public string? Nonce { get; set; }
    public string? Mac { get; set; }
    public List<WireEntry>? Entries { get; set; }
    public string? Hash { get; set; }
    public string? ExpectedSnapshotHash { get; set; }
    public string? FinalSnapshotHash { get; set; }
    public List<WireAction>? Actions { get; set; }
    public List<BaselineItem>? Baseline { get; set; }
    public int? Count { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? Reason { get; set; }

    public static SyncMessage Hello(string deviceId, string deviceName, int tcpPort) => new()
    {
        Type = "hello",
        Protocol = ProtocolVersion,
        DeviceId = deviceId,
        DeviceName = deviceName,
        App = AppId,
        Version = Constants.AppVersion,
        TcpPort = tcpPort
    };

    public static SyncMessage Challenge(byte[] nonce) => new()
    {
        Type = "challenge",
        Protocol = ProtocolVersion,
        Nonce = Convert.ToBase64String(nonce)
    };

    public static SyncMessage Auth(string mac) => new()
    {
        Type = "auth",
        Protocol = ProtocolVersion,
        Mac = mac
    };

    public static SyncMessage Welcome(string deviceId, string deviceName) => new()
    {
        Type = "welcome",
        Protocol = ProtocolVersion,
        DeviceId = deviceId,
        DeviceName = deviceName,
        App = AppId,
        Version = Constants.AppVersion
    };

    public static SyncMessage Snapshot(SyncSnapshot snapshot) => new()
    {
        Type = "snapshot",
        Protocol = ProtocolVersion,
        Entries = WireConvert.ToWire(snapshot),
        Hash = snapshot.Hash
    };

    public static SyncMessage Apply(
        string expectedSnapshotHash,
        string finalSnapshotHash,
        IReadOnlyList<SyncAction> actions,
        SyncSnapshot finalSnapshot) => new()
    {
        Type = "apply",
        Protocol = ProtocolVersion,
        ExpectedSnapshotHash = expectedSnapshotHash,
        FinalSnapshotHash = finalSnapshotHash,
        Actions = WireConvert.ToWire(actions),
        Baseline = MergeEngine.BaselineItems(finalSnapshot)
    };

    public static SyncMessage Applied(int count, string finalSnapshotHash) => new()
    {
        Type = "applied",
        Protocol = ProtocolVersion,
        Count = count,
        Hash = finalSnapshotHash
    };

    public static SyncMessage Abort(string reason) => new()
    {
        Type = "abort",
        Protocol = ProtocolVersion,
        Reason = reason
    };

    public static SyncMessage Error(string code, string message) => new()
    {
        Type = "error",
        Protocol = ProtocolVersion,
        Code = code,
        Message = message
    };

    public static SyncMessage Discover(string deviceId, string deviceName, int tcpPort) => new()
    {
        Type = "discover",
        Protocol = ProtocolVersion,
        DeviceId = deviceId,
        DeviceName = deviceName,
        TcpPort = tcpPort
    };

    public static SyncMessage Offer(string deviceId, string deviceName, int tcpPort) => new()
    {
        Type = "offer",
        Protocol = ProtocolVersion,
        DeviceId = deviceId,
        DeviceName = deviceName,
        TcpPort = tcpPort
    };

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>与 bills.json 相同的账单对象；Category 为 null 时仍显式写出。</summary>
public sealed class WireEntry
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Category { get; set; }

    public string Date { get; set; } = "";
}

/// <summary>
/// <c>{ "op": "upsert", "entry": {...} }</c> 或
/// <c>{ "op": "delete", "id": "...", "date": "..." }</c>。
/// </summary>
public sealed class WireAction
{
    public string Op { get; set; } = "";
    public WireEntry? Entry { get; set; }
    public string? Id { get; set; }
    public string? Date { get; set; }
}

/// <summary>线缆 DTO 与领域模型之间的转换与校验。</summary>
public static class WireConvert
{
    public static WireEntry ToWire(SyncEntry entry) => new()
    {
        Id = entry.Id,
        Description = entry.Description,
        Amount = entry.Amount,
        Category = entry.Category,
        Date = entry.Date
    };

    public static List<WireEntry> ToWire(SyncSnapshot snapshot)
        => snapshot.OrderedEntries().Select(ToWire).ToList();

    public static List<WireAction> ToWire(IReadOnlyList<SyncAction> actions)
    {
        var list = new List<WireAction>(actions.Count);

        foreach (var action in actions)
        {
            switch (action.Op)
            {
                case SyncActionOp.Upsert when action.Entry is not null:
                    list.Add(new WireAction { Op = "upsert", Entry = ToWire(action.Entry) });
                    break;

                case SyncActionOp.Delete when action.Id is not null && action.Date is not null:
                    list.Add(new WireAction { Op = "delete", Id = action.Id, Date = action.Date });
                    break;

                default:
                    throw new InvalidDataException("无法序列化的同步动作");
            }
        }

        return list;
    }

    public static bool TryToEntry(WireEntry? wire, out SyncEntry entry, out string error)
    {
        entry = new SyncEntry();

        if (wire is null)
        {
            error = "缺少 entry";
            return false;
        }

        if (!SyncEntry.IsValidId(wire.Id))
        {
            error = $"非法的账单 Id: {wire.Id}";
            return false;
        }

        if (!SyncEntry.IsValidDate(wire.Date))
        {
            error = $"非法的日期: {wire.Date}";
            return false;
        }

        entry = new SyncEntry
        {
            Id = SyncEntry.NormalizeId(wire.Id),
            Description = wire.Description ?? "",
            Amount = wire.Amount,
            Category = wire.Category,
            Date = wire.Date
        };

        error = "";
        return true;
    }

    /// <summary>
    /// 从 snapshot 消息构造快照。若消息带 hash，会重算并比对；不一致视为协议错误。
    /// </summary>
    public static bool TryToSnapshot(SyncMessage message, out SyncSnapshot snapshot, out string error)
    {
        snapshot = SyncSnapshot.Empty;

        var entries = new List<SyncEntry>(message.Entries?.Count ?? 0);

        foreach (var wire in message.Entries ?? [])
        {
            if (!TryToEntry(wire, out var entry, out error)) return false;
            entries.Add(entry);
        }

        snapshot = SyncSnapshot.Create(entries);

        if (!string.IsNullOrEmpty(message.Hash))
        {
            if (!LanSyncHash.IsHexHash(message.Hash))
            {
                error = "快照哈希格式不正确";
                return false;
            }

            if (!string.Equals(snapshot.Hash, message.Hash, StringComparison.OrdinalIgnoreCase))
            {
                error = "快照哈希与内容不一致";
                return false;
            }
        }

        error = "";
        return true;
    }

    public static bool TryToActions(
        IReadOnlyList<WireAction>? wires,
        out List<SyncAction> actions,
        out string error)
    {
        actions = new List<SyncAction>(wires?.Count ?? 0);

        foreach (var wire in wires ?? [])
        {
            if (wire is null)
            {
                error = "动作列表包含 null";
                return false;
            }

            var op = (wire.Op ?? "").Trim().ToLowerInvariant();

            if (op == "upsert")
            {
                if (!TryToEntry(wire.Entry, out var entry, out error)) return false;
                actions.Add(SyncAction.Upsert(entry));
                continue;
            }

            if (op == "delete")
            {
                if (!SyncEntry.IsValidId(wire.Id))
                {
                    error = $"非法的删除 Id: {wire.Id}";
                    return false;
                }

                if (!SyncEntry.IsValidDate(wire.Date))
                {
                    error = $"非法的删除日期: {wire.Date}";
                    return false;
                }

                actions.Add(SyncAction.Delete(SyncEntry.NormalizeId(wire.Id!), wire.Date!));
                continue;
            }

            error = $"未知的同步动作: {wire.Op}";
            return false;
        }

        error = "";
        return true;
    }

    public static bool TryToBaseline(
        IReadOnlyList<BaselineItem>? items,
        out Dictionary<string, string> baseline,
        out string error)
    {
        baseline = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in items ?? [])
        {
            if (item is null)
            {
                error = "基线列表包含 null";
                return false;
            }

            if (!SyncEntry.IsValidId(item.Id))
            {
                error = $"非法的基线 Id: {item.Id}";
                return false;
            }

            if (!LanSyncHash.IsHexHash(item.Hash))
            {
                error = $"非法的基线哈希: {item.Id}";
                return false;
            }

            baseline[SyncEntry.NormalizeId(item.Id)] = item.Hash.ToLowerInvariant();
        }

        error = "";
        return true;
    }
}
