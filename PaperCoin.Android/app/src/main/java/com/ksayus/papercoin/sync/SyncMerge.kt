package com.ksayus.papercoin.sync

import com.ksayus.papercoin.data.BillEntry
import com.ksayus.papercoin.data.BillChange
import com.ksayus.papercoin.data.SyncHash

/** 收到的完整账单快照。 */
data class SyncSnapshot(
    val entries: List<BillEntry>,
    val hash: String,
) {
    val byId: Map<String, BillEntry> = entries.associateBy { it.id }

    companion object {
        fun of(entries: List<BillEntry>): SyncSnapshot =
            SyncSnapshot(entries, SyncHash.snapshotHash(entries))
    }
}

enum class ConflictKind {
    BOTH_CHANGED,
    LOCAL_DELETED_REMOTE_CHANGED,
    LOCAL_CHANGED_REMOTE_DELETED,
}

data class SyncConflict(
    val id: String,
    val local: BillEntry?,
    val remote: BillEntry?,
    val kind: ConflictKind,
)

enum class ConflictChoice { LOCAL, REMOTE }

data class SyncPlan(
    val finalEntries: List<BillEntry>,
    val finalHash: String,
    val localChanges: List<BillChange>,
    val remoteChanges: List<BillChange>,
    val conflicts: List<SyncConflict>,
)

/**
 * 三方合并。基线是"上次与这个设备同步成功后，每条流水的 id → 内容哈希"。
 * 规则必须与桌面版 `LanSyncMerge.cs` 完全一致。
 */
object SyncMerge {
    fun plan(
        local: SyncSnapshot,
        remote: SyncSnapshot,
        baseline: Map<String, String>,
        choices: Map<String, ConflictChoice> = emptyMap(),
    ): SyncPlan {
        val finalById = LinkedHashMap<String, BillEntry>()
        val conflicts = mutableListOf<SyncConflict>()

        val ids = LinkedHashSet<String>()
        ids += local.byId.keys
        ids += remote.byId.keys
        ids += baseline.keys

        for (id in ids) {
            val l = local.byId[id]
            val r = remote.byId[id]
            val baseHash = baseline[id]

            when {
                l != null && r != null -> {
                    val lHash = SyncHash.contentHash(l)
                    val rHash = SyncHash.contentHash(r)

                    if (lHash == rHash) {
                        finalById[id] = l
                        continue
                    }

                    val localChanged = baseHash == null || lHash != baseHash
                    val remoteChanged = baseHash == null || rHash != baseHash

                    when {
                        localChanged && remoteChanged -> {
                            when (choices[id]) {
                                ConflictChoice.LOCAL -> finalById[id] = l
                                ConflictChoice.REMOTE -> finalById[id] = r
                                null -> conflicts += SyncConflict(
                                    id = id,
                                    local = l,
                                    remote = r,
                                    kind = ConflictKind.BOTH_CHANGED,
                                )
                            }
                        }

                        localChanged -> finalById[id] = l
                        else -> finalById[id] = r
                    }
                }

                l != null -> {
                    if (baseHash == null) {
                        finalById[id] = l
                    } else if (SyncHash.contentHash(l) == baseHash) {
                        // 对方删了，本机没改：传播删除
                    } else {
                        when (choices[id]) {
                            ConflictChoice.LOCAL -> finalById[id] = l
                            ConflictChoice.REMOTE -> Unit
                            null -> conflicts += SyncConflict(
                                id = id,
                                local = l,
                                remote = null,
                                kind = ConflictKind.LOCAL_CHANGED_REMOTE_DELETED,
                            )
                        }
                    }
                }

                r != null -> {
                    if (baseHash == null) {
                        finalById[id] = r
                    } else if (SyncHash.contentHash(r) == baseHash) {
                        // 本机删了，对方没改：传播删除
                    } else {
                        when (choices[id]) {
                            ConflictChoice.LOCAL -> Unit
                            ConflictChoice.REMOTE -> finalById[id] = r
                            null -> conflicts += SyncConflict(
                                id = id,
                                local = null,
                                remote = r,
                                kind = ConflictKind.LOCAL_DELETED_REMOTE_CHANGED,
                            )
                        }
                    }
                }
            }
        }

        val finalEntries = finalById.values.sortedBy { it.id }
        val finalHash = SyncHash.snapshotHash(finalEntries)

        // 有未解决冲突时不能生成可执行变更：否则会被当成"这些 id 在最终快照里不存在"而误删。
        if (conflicts.isNotEmpty()) {
            return SyncPlan(finalEntries, finalHash, emptyList(), emptyList(), conflicts)
        }

        return SyncPlan(
            finalEntries = finalEntries,
            finalHash = finalHash,
            localChanges = changesFrom(local.byId, finalById),
            remoteChanges = changesFrom(remote.byId, finalById),
            conflicts = emptyList(),
        )
    }

    private fun changesFrom(
        side: Map<String, BillEntry>,
        finalById: Map<String, BillEntry>,
    ): List<BillChange> {
        val deletes = mutableListOf<BillChange>()

        for (entry in side.values) {
            val target = finalById[entry.id]

            if (target == null || target.date != entry.date) {
                deletes += BillChange.Delete(entry.id, entry.date)
            }
        }

        val upserts = mutableListOf<BillChange>()

        for (target in finalById.values) {
            val current = side[target.id]

            val unchanged = current != null &&
                current.date == target.date &&
                SyncHash.contentHash(current) == SyncHash.contentHash(target)

            if (!unchanged) upserts += BillChange.Upsert(target)
        }

        return deletes + upserts
    }
}
