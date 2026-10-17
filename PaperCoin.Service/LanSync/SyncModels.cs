using System.Globalization;
using PaperCoin.Core.Models;

namespace PaperCoin.Service.LanSync;

/// <summary>
/// 一条账单在同步协议里的表示。日期用协议规定的 <c>yyyy-MM-dd</c> 文本，
/// Id 一律使用小写 GUID 的 "D" 形式：协议的排序规则按 UTF-16 代码元比较，
/// 双方必须先把 GUID 归一化，否则同一个 Id 在两端会排出不同顺序。
/// </summary>
public sealed class SyncEntry
{
    public string Id { get; init; } = "";

    /// <summary>原样保存的描述，编辑器已经做过 Trim</summary>
    public string Description { get; init; } = "";

    public decimal Amount { get; init; }

    public string? Category { get; init; }

    /// <summary><c>yyyy-MM-dd</c>，InvariantCulture</summary>
    public string Date { get; init; } = "";

    public static SyncEntry FromBillEntry(BillEntry entry) => new()
    {
        Id = entry.Id.ToString("D", CultureInfo.InvariantCulture),
        Description = entry.Description,
        Amount = entry.Amount,
        Category = entry.Category,
        Date = entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    };

    public BillEntry ToBillEntry() => new()
    {
        Id = Guid.Parse(Id),
        Description = Description,
        Amount = Amount,
        Category = Category,
        Date = DateOnly.ParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
    };

    /// <summary>协议里的日期必须是这个格式；不合法时返回 false 而不是抛异常。</summary>
    public static bool IsValidDate(string? date)
        => !string.IsNullOrEmpty(date)
           && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out _);

    public static bool IsValidId(string? id)
        => !string.IsNullOrEmpty(id) && Guid.TryParse(id, out _);

    /// <summary>把任意写法的 GUID 变成协议使用的小写 "D" 形式。</summary>
    public static string NormalizeId(string id)
        => Guid.Parse(id).ToString("D");
}

/// <summary>
/// 内容哈希与条目集合的不可变视图。哈希在构造时算好，后续比较只看字符串。
/// </summary>
public sealed class SyncSnapshot
{
    private SyncSnapshot(Dictionary<string, SyncEntry> entries)
    {
        Entries = entries;
        Hash = LanSyncHash.SnapshotHash(entries.Values);
    }

    public IReadOnlyDictionary<string, SyncEntry> Entries { get; }

    public string Hash { get; }

    public int Count => Entries.Count;

    public static SyncSnapshot Empty => new(new Dictionary<string, SyncEntry>(StringComparer.Ordinal));

    public static SyncSnapshot Create(IEnumerable<SyncEntry> entries)
    {
        var map = new Dictionary<string, SyncEntry>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            // 同一个 Id 出现多次时后一条覆盖前一条，与 bills.json 的读取行为保持一致
            map[entry.Id] = entry;
        }

        return new SyncSnapshot(map);
    }

    /// <summary>按 Id 的序数顺序返回，供线缆消息稳定输出。</summary>
    public IReadOnlyList<SyncEntry> OrderedEntries()
        => Entries.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();

    public SyncEntry? Find(string id) => Entries.TryGetValue(id, out var entry) ? entry : null;
}

public enum SyncActionOp
{
    Upsert,
    Delete
}

/// <summary>同步动作。删除只带 Id 与日期，因为磁盘上的文件按日期分目录。</summary>
public sealed class SyncAction
{
    public SyncActionOp Op { get; init; }

    public SyncEntry? Entry { get; init; }

    public string? Id { get; init; }

    public string? Date { get; init; }

    public static SyncAction Upsert(SyncEntry entry) => new()
    {
        Op = SyncActionOp.Upsert,
        Entry = entry
    };

    public static SyncAction Delete(string id, string date) => new()
    {
        Op = SyncActionOp.Delete,
        Id = id,
        Date = date
    };
}

/// <summary>线缆上的 baseline 项；状态文件里则存成 id -&gt; hash 的对象。</summary>
public sealed class BaselineItem
{
    public string Id { get; init; } = "";
    public string Hash { get; init; } = "";
}

public enum ConflictChoice
{
    Local,
    Remote
}

/// <summary>一处未自动合并的冲突。Local/Remote 为 null 表示该侧已删除。</summary>
public sealed class MergeConflict
{
    public string Id { get; init; } = "";

    public SyncEntry? Local { get; init; }

    public SyncEntry? Remote { get; init; }

    /// <summary>用户选择；null 表示还没选。</summary>
    public ConflictChoice? Resolution { get; internal set; }

    public bool IsResolved => Resolution is not null;

    /// <summary>本机已删除</summary>
    public bool LocalDeleted => Local is null;

    /// <summary>对方已删除</summary>
    public bool RemoteDeleted => Remote is null;
}

public sealed class MergeResult
{
    internal MergeResult(
        IReadOnlyList<MergeConflict> conflicts,
        SyncSnapshot final,
        IReadOnlyList<SyncAction> localActions,
        IReadOnlyList<SyncAction> remoteActions,
        bool complete)
    {
        Conflicts = conflicts;
        Final = final;
        LocalActions = localActions;
        RemoteActions = remoteActions;
        IsComplete = complete;
    }

    /// <summary>false 表示还有没做选择的冲突；此时 Final/动作列表不可用于落盘。</summary>
    public bool IsComplete { get; }

    public bool HasUnresolvedConflicts => !IsComplete;

    public IReadOnlyList<MergeConflict> Conflicts { get; }

    public SyncSnapshot Final { get; }

    public IReadOnlyList<SyncAction> LocalActions { get; }

    public IReadOnlyList<SyncAction> RemoteActions { get; }

    public string FinalSnapshotHash => IsComplete ? Final.Hash : "";
}

/// <summary>
/// 检测到冲突、等待用户选择的一次会话。客户端的会话已经 abort，源数据没有动；
/// 用户点“应用”后会重新连接、交换最新快照再按选择合并。
/// </summary>
public sealed class SyncConflictSession
{
    public string PeerId { get; init; } = "";
    public string PeerName { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public IReadOnlyList<MergeConflict> Conflicts { get; init; } = [];
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;

    public string EndpointText => $"{Host}:{Port}";
}

/// <summary>UDP 发现到的设备。Host/Port 是它对外提供 TCP 服务的地址。</summary>
public sealed class DiscoveredPeer
{
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string Host { get; init; } = "";
    public int TcpPort { get; init; }
    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;

    public string EndpointText => $"{Host}:{TcpPort}";
}

public sealed class SyncCompletedEventArgs : EventArgs
{
    public required string PeerId { get; init; }
    public required string PeerName { get; init; }
    public required string FinalSnapshotHash { get; init; }
    public int AppliedCount { get; init; }
    public int LocalActionCount { get; init; }
}

public sealed class ConflictsDetectedEventArgs : EventArgs
{
    public required SyncConflictSession Session { get; init; }
}

/// <summary>设置类操作的结果。失败时 Message 直接可显示给用户。</summary>
public sealed record SyncSettingsResult(bool Success, string Message)
{
    public static SyncSettingsResult Ok(string message = "") => new(true, message);
    public static SyncSettingsResult Fail(string message) => new(false, message);
}
