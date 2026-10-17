package com.ksayus.papercoin.sync

import com.ksayus.papercoin.data.BillChange
import com.ksayus.papercoin.data.BillEntry
import com.ksayus.papercoin.data.SyncHash
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.math.BigDecimal
import java.time.LocalDate

class SyncMergeTest {
    private val day1 = LocalDate.of(2026, 10, 18)
    private val day2 = LocalDate.of(2026, 10, 19)

    private fun entry(
        id: String,
        description: String = "x",
        amount: String = "-10",
        category: String? = "餐饮",
        date: LocalDate = day1,
    ) = BillEntry(
        id = id,
        description = description,
        amount = BigDecimal(amount),
        category = category,
        date = date,
    )

    private fun baselineOf(vararg entries: BillEntry): Map<String, String> =
        entries.associate { it.id to SyncHash.contentHash(it) }

    @Test
    fun `new entries on both sides are merged without conflict`() {
        val localEntry = entry("local")
        val remoteEntry = entry("remote")

        val plan = SyncMerge.plan(
            local = SyncSnapshot.of(listOf(localEntry)),
            remote = SyncSnapshot.of(listOf(remoteEntry)),
            baseline = emptyMap(),
        )

        assertEquals(2, plan.finalEntries.size)
        assertTrue(plan.conflicts.isEmpty())
        assertEquals(1, plan.localChanges.size)
        assertEquals(1, plan.remoteChanges.size)
        assertTrue(plan.localChanges.single() is BillChange.Upsert)
        assertTrue(plan.remoteChanges.single() is BillChange.Upsert)
    }

    @Test
    fun `only the changed side wins when the other side is unchanged`() {
        val original = entry("a", amount = "-10")
        val changed = original.copy(amount = BigDecimal("-20"))

        val localWins = SyncMerge.plan(
            local = SyncSnapshot.of(listOf(changed)),
            remote = SyncSnapshot.of(listOf(original)),
            baseline = baselineOf(original),
        )

        assertTrue(localWins.conflicts.isEmpty())
        assertEquals(BigDecimal("-20"), localWins.finalEntries.single().amount)

        val remoteWins = SyncMerge.plan(
            local = SyncSnapshot.of(listOf(original)),
            remote = SyncSnapshot.of(listOf(changed)),
            baseline = baselineOf(original),
        )

        assertTrue(remoteWins.conflicts.isEmpty())
        assertEquals(BigDecimal("-20"), remoteWins.finalEntries.single().amount)
    }

    @Test
    fun `both sides changed the same entry is a conflict`() {
        val original = entry("a", amount = "-10")
        val local = original.copy(amount = BigDecimal("-20"))
        val remote = original.copy(amount = BigDecimal("-30"))

        val plan = SyncMerge.plan(
            local = SyncSnapshot.of(listOf(local)),
            remote = SyncSnapshot.of(listOf(remote)),
            baseline = baselineOf(original),
        )

        assertEquals(1, plan.conflicts.size)
        assertEquals(ConflictKind.BOTH_CHANGED, plan.conflicts.single().kind)
        assertTrue(plan.localChanges.isEmpty())
        assertTrue(plan.remoteChanges.isEmpty())
    }

    @Test
    fun `conflict choices pick the selected version`() {
        val original = entry("a", amount = "-10")
        val local = original.copy(amount = BigDecimal("-20"))
        val remote = original.copy(amount = BigDecimal("-30"))

        val chooseLocal = SyncMerge.plan(
            local = SyncSnapshot.of(listOf(local)),
            remote = SyncSnapshot.of(listOf(remote)),
            baseline = baselineOf(original),
            choices = mapOf("a" to ConflictChoice.LOCAL),
        )
        assertEquals(BigDecimal("-20"), chooseLocal.finalEntries.single().amount)

        val chooseRemote = SyncMerge.plan(
            local = SyncSnapshot.of(listOf(local)),
            remote = SyncSnapshot.of(listOf(remote)),
            baseline = baselineOf(original),
            choices = mapOf("a" to ConflictChoice.REMOTE),
        )
        assertEquals(BigDecimal("-30"), chooseRemote.finalEntries.single().amount)
    }

    @Test
    fun `remote deletion propagates when local is unchanged`() {
        val original = entry("a")

        val plan = SyncMerge.plan(
            local = SyncSnapshot.of(listOf(original)),
            remote = SyncSnapshot.of(emptyList()),
            baseline = baselineOf(original),
        )

        assertTrue(plan.conflicts.isEmpty())
        assertTrue(plan.finalEntries.isEmpty())
        assertEquals(1, plan.localChanges.size)
        assertTrue(plan.localChanges.single() is BillChange.Delete)
        assertTrue(plan.remoteChanges.isEmpty())
    }

    @Test
    fun `delete versus edit is a conflict`() {
        val original = entry("a", amount = "-10")
        val remote = original.copy(amount = BigDecimal("-30"))

        val plan = SyncMerge.plan(
            local = SyncSnapshot.of(emptyList()),
            remote = SyncSnapshot.of(listOf(remote)),
            baseline = baselineOf(original),
        )

        assertEquals(1, plan.conflicts.size)
        assertEquals(ConflictKind.LOCAL_DELETED_REMOTE_CHANGED, plan.conflicts.single().kind)
        assertEquals(null, plan.conflicts.single().local)
    }

    @Test
    fun `date move deletes the old day before upserting the new one`() {
        val original = entry("a", date = day1)
        val moved = original.copy(date = day2)

        val plan = SyncMerge.plan(
            local = SyncSnapshot.of(listOf(moved)),
            remote = SyncSnapshot.of(listOf(original)),
            baseline = baselineOf(original),
        )

        assertTrue(plan.conflicts.isEmpty())
        assertEquals(day2, plan.finalEntries.single().date)

        assertEquals(2, plan.remoteChanges.size)
        assertTrue(plan.remoteChanges[0] is BillChange.Delete)
        assertTrue(plan.remoteChanges[1] is BillChange.Upsert)

        assertEquals(0, plan.localChanges.size)
    }

    @Test
    fun `final hash always matches the final entries`() {
        val local = entry("a", amount = "-10")
        val remote = entry("b", amount = "-20")

        val plan = SyncMerge.plan(
            local = SyncSnapshot.of(listOf(local)),
            remote = SyncSnapshot.of(listOf(remote)),
            baseline = emptyMap(),
        )

        assertEquals(SyncHash.snapshotHash(plan.finalEntries), plan.finalHash)
    }
}
