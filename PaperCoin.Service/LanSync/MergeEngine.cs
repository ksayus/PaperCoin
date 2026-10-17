namespace PaperCoin.Service.LanSync;

/// <summary>
/// 协议 v1 的三方合并。规则逐条对应 docs/lan-sync-protocol.md 的 Merge 表：
/// 基线 B 里没有某个 Id 时，该侧一律视为“已改动”。
/// </summary>
public static class MergeEngine
{
    /// <summary>
    /// 计算最终快照与两侧要应用的动作。
    /// <paramref name="choices"/> 里的选择只对仍然冲突的 Id 生效；没有选择的冲突会让结果
    /// <see cref="MergeResult.IsComplete"/> 为 false，此时动作列表为空，调用方必须中止会话。
    /// </summary>
    public static MergeResult Merge(
        SyncSnapshot local,
        SyncSnapshot remote,
        IReadOnlyDictionary<string, string>? baseline,
        IReadOnlyDictionary<string, ConflictChoice>? choices = null)
    {
        baseline ??= EmptyBaseline;
        choices ??= EmptyChoices;

        var localHashes = HashMap(local);
        var remoteHashes = HashMap(remote);

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        ids.UnionWith(local.Entries.Keys);
        ids.UnionWith(remote.Entries.Keys);
        ids.UnionWith(baseline.Keys);

        var decisions = new Dictionary<string, SyncEntry?>(StringComparer.Ordinal);
        var conflicts = new List<MergeConflict>();

        foreach (var id in ids)
        {
            local.Entries.TryGetValue(id, out var left);
            remote.Entries.TryGetValue(id, out var right);

            var hasLeft = left is not null;
            var hasRight = right is not null;

            if (hasLeft && hasRight)
            {
                // 内容相同直接取本机，基线是什么都不重要。
                if (string.Equals(localHashes[id], remoteHashes[id], StringComparison.Ordinal))
                {
                    decisions[id] = left;
                    continue;
                }

                var leftChanged = Changed(id, localHashes[id], baseline);
                var rightChanged = Changed(id, remoteHashes[id], baseline);

                if (leftChanged && rightChanged)
                {
                    ResolveConflict(id, left, right, choices, decisions, conflicts);
                }
                else if (leftChanged)
                {
                    decisions[id] = left;
                }
                else if (rightChanged)
                {
                    decisions[id] = right;
                }
                else
                {
                    // 哈希不同却两边都没变，只可能是基线本身损坏；保守取本机。
                    decisions[id] = left;
                }

                continue;
            }

            if (hasLeft)
            {
                // 本机有、对方没有。
                if (!baseline.ContainsKey(id))
                {
                    decisions[id] = left; // 本机新增
                }
                else if (string.Equals(localHashes[id], baseline[id], StringComparison.Ordinal))
                {
                    decisions[id] = null; // 对方删除、本机未改
                }
                else
                {
                    ResolveConflict(id, left, null, choices, decisions, conflicts);
                }

                continue;
            }

            if (hasRight)
            {
                // 对方有、本机没有。
                if (!baseline.ContainsKey(id))
                {
                    decisions[id] = right; // 对方新增
                }
                else if (string.Equals(remoteHashes[id], baseline[id], StringComparison.Ordinal))
                {
                    decisions[id] = null; // 本机删除、对方未改
                }
                else
                {
                    ResolveConflict(id, null, right, choices, decisions, conflicts);
                }

                continue;
            }

            // 两边都没有：双方都删除（或只存在于基线里），最终不存在。
            decisions[id] = null;
        }

        // 未解决的冲突意味着不能生成可落盘的最终快照与动作。
        var complete = conflicts.All(c => c.IsResolved);

        var finalEntries = decisions
            .Where(kv => kv.Value is not null)
            .Select(kv => kv.Value!)
            .ToList();

        var finalSnapshot = SyncSnapshot.Create(finalEntries);

        if (!complete)
        {
            return new MergeResult(
                conflicts,
                finalSnapshot,
                Array.Empty<SyncAction>(),
                Array.Empty<SyncAction>(),
                complete: false);
        }

        var localActions = BuildActions(local, localHashes, finalSnapshot);
        var remoteActions = BuildActions(remote, remoteHashes, finalSnapshot);

        return new MergeResult(conflicts, finalSnapshot, localActions, remoteActions, complete: true);
    }

    private static void ResolveConflict(
        string id,
        SyncEntry? left,
        SyncEntry? right,
        IReadOnlyDictionary<string, ConflictChoice> choices,
        Dictionary<string, SyncEntry?> decisions,
        List<MergeConflict> conflicts)
    {
        choices.TryGetValue(id, out var choice);
        var hasChoice = choices.ContainsKey(id);

        var conflict = new MergeConflict
        {
            Id = id,
            Local = left,
            Remote = right,
            Resolution = hasChoice ? choice : null
        };

        conflicts.Add(conflict);

        if (!hasChoice) return;

        // LOCAL 取本机（null 即删除），REMOTE 取对方。
        decisions[id] = choice == ConflictChoice.Local ? left : right;
    }

    /// <summary>
    /// 动作生成：删除先于同一 Id 的 upsert；日期变了就删旧日期再写新日期。
    /// 为了线缆输出稳定，Id 一律按序数排序。
    /// </summary>
    private static List<SyncAction> BuildActions(
        SyncSnapshot side,
        IReadOnlyDictionary<string, string> sideHashes,
        SyncSnapshot final)
    {
        var finalHashes = HashMap(final);

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        ids.UnionWith(side.Entries.Keys);
        ids.UnionWith(final.Entries.Keys);

        var deletes = new List<SyncAction>();
        var upserts = new List<SyncAction>();

        foreach (var id in ids)
        {
            side.Entries.TryGetValue(id, out var current);
            final.Entries.TryGetValue(id, out var target);

            var dateDiffers = current is not null
                              && target is not null
                              && !string.Equals(current.Date, target.Date, StringComparison.Ordinal);

            var hashDiffers = current is not null
                              && target is not null
                              && !string.Equals(sideHashes[id], finalHashes[id], StringComparison.Ordinal);

            if (current is not null && (target is null || dateDiffers || hashDiffers))
            {
                // 同一日期只改内容时 upsert 本身就能覆盖，不需要先删。
                if (target is null || dateDiffers)
                    deletes.Add(SyncAction.Delete(id, current.Date));
            }

            if (target is not null
                && (current is null || dateDiffers || hashDiffers))
            {
                upserts.Add(SyncAction.Upsert(target));
            }
        }

        var actions = new List<SyncAction>(deletes.Count + upserts.Count);
        actions.AddRange(deletes);
        actions.AddRange(upserts);

        return actions;
    }

    private static bool Changed(string id, string hash, IReadOnlyDictionary<string, string> baseline)
        => !baseline.TryGetValue(id, out var baselineHash)
           || !string.Equals(hash, baselineHash, StringComparison.Ordinal);

    private static Dictionary<string, string> HashMap(SyncSnapshot snapshot)
    {
        var map = new Dictionary<string, string>(snapshot.Count, StringComparer.Ordinal);

        foreach (var (id, entry) in snapshot.Entries)
            map[id] = LanSyncHash.ContentHash(entry);

        return map;
    }

    /// <summary>成功后的基线 = 最终快照每个 Id 的内容哈希，按 Id 序数排序。</summary>
    public static Dictionary<string, string> BaselineMap(SyncSnapshot final)
    {
        var map = new Dictionary<string, string>(final.Count, StringComparer.Ordinal);

        foreach (var id in final.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal))
            map[id] = LanSyncHash.ContentHash(final.Entries[id]);

        return map;
    }

    /// <summary>线缆上的 baseline 数组，按 Id 序数排序。</summary>
    public static List<BaselineItem> BaselineItems(SyncSnapshot final)
        => final.Entries.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(id => new BaselineItem { Id = id, Hash = LanSyncHash.ContentHash(final.Entries[id]) })
            .ToList();

    private static readonly IReadOnlyDictionary<string, string> EmptyBaseline =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, ConflictChoice> EmptyChoices =
        new Dictionary<string, ConflictChoice>(StringComparer.Ordinal);
}
