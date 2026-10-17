package com.ksayus.papercoin.sync

import com.ksayus.papercoin.data.BillEntry
import com.ksayus.papercoin.data.BillRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.math.BigDecimal
import java.time.LocalDate

/**
 * 不依赖模拟器的端到端协议测试：同一进程里起一个 TCP 服务端和一个客户端，
 * 走完整握手、快照交换、合并、apply、基线更新。
 */
class SyncProtocolLoopbackTest {
    @get:Rule
    val temp = TemporaryFolder()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var server: SyncServer? = null

    private val day = LocalDate.of(2026, 10, 18)

    @After
    fun tearDown() {
        server?.stop()
        scope.cancel()
    }

    private fun entry(
        id: String,
        amount: String = "-10",
        description: String = "x",
        date: LocalDate = day,
    ) = BillEntry(
        id = id,
        description = description,
        amount = BigDecimal(amount),
        category = "餐饮",
        date = date,
    )

    private fun repository(name: String) =
        BillRepository(File(temp.root, "$name/Bills"))

    private fun store(name: String) =
        SyncStore(File(temp.root, "$name/Sync/sync.json"), name)

    private fun assertAmount(expected: String, actual: BigDecimal) {
        assertEquals(0, BigDecimal(expected).compareTo(actual))
    }

    @Test
    fun `two devices converge and conflicting edits can choose local or remote`() = runBlocking {
        val serverRepo = repository("server")
        val clientRepo = repository("client")
        val serverStore = store("server")
        val clientStore = store("client")

        assertEquals(true, serverStore.setCode("ABCD2345"))
        assertEquals(true, clientStore.setCode("ABCD2345"))

        val a = entry("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", amount = "-10")
        val b = entry("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", amount = "-20")

        serverRepo.save(listOf(a))
        clientRepo.save(listOf(b))

        val serverInstance = SyncServer(serverRepo, serverStore, "test")
        server = serverInstance
        serverInstance.start(scope)

        val client = SyncClient(clientRepo, clientStore, "test")

        val first = client.sync("127.0.0.1", serverInstance.port)
        assertTrue("first sync failed: $first", first is SyncResult.Success)

        assertEquals(setOf(a.id, b.id), serverRepo.loadAll().map { it.id }.toSet())
        assertEquals(setOf(a.id, b.id), clientRepo.loadAll().map { it.id }.toSet())

        // both sides edit the same entry -> conflict; nothing is applied
        serverRepo.upsertEntry(a.copy(amount = BigDecimal("-20")))
        clientRepo.upsertEntry(a.copy(amount = BigDecimal("-30")))

        val conflict = client.sync("127.0.0.1", serverInstance.port)
        assertTrue("expected conflict: $conflict", conflict is SyncResult.Conflicts)
        assertEquals(1, (conflict as SyncResult.Conflicts).conflicts.size)

        assertAmount("-20", serverRepo.loadAll().first { it.id == a.id }.amount)
        assertAmount("-30", clientRepo.loadAll().first { it.id == a.id }.amount)

        val resolved = client.sync(
            "127.0.0.1",
            serverInstance.port,
            mapOf(a.id to ConflictChoice.LOCAL),
        )
        assertTrue("resolve failed: $resolved", resolved is SyncResult.Success)

        assertAmount("-30", serverRepo.loadAll().first { it.id == a.id }.amount)
        assertAmount("-30", clientRepo.loadAll().first { it.id == a.id }.amount)

        // server deletes, client edits -> conflict; choosing remote deletes on both
        serverRepo.deleteEntry(day, a.id)
        clientRepo.upsertEntry(a.copy(amount = BigDecimal("-40")))

        val deleteConflict = client.sync("127.0.0.1", serverInstance.port)
        assertTrue("expected delete conflict: $deleteConflict", deleteConflict is SyncResult.Conflicts)

        val chooseRemote = client.sync(
            "127.0.0.1",
            serverInstance.port,
            mapOf(a.id to ConflictChoice.REMOTE),
        )
        assertTrue("delete resolution failed: $chooseRemote", chooseRemote is SyncResult.Success)

        assertTrue(serverRepo.loadAll().none { it.id == a.id })
        assertTrue(clientRepo.loadAll().none { it.id == a.id })
    }

    @Test
    fun `date changes are propagated as a delete plus upsert`() = runBlocking {
        val serverRepo = repository("server2")
        val clientRepo = repository("client2")
        val serverStore = store("server2")
        val clientStore = store("client2")

        serverStore.setCode("ABCD2345")
        clientStore.setCode("ABCD2345")

        val item = entry("cccccccc-cccc-4ccc-8ccc-cccccccccccc", amount = "-42")
        serverRepo.save(listOf(item))
        clientRepo.save(listOf(item))

        val serverInstance = SyncServer(serverRepo, serverStore, "test")
        server = serverInstance
        serverInstance.start(scope)

        val client = SyncClient(clientRepo, clientStore, "test")
        assertTrue(client.sync("127.0.0.1", serverInstance.port) is SyncResult.Success)

        val moved = item.copy(date = day.plusDays(1))
        clientRepo.upsertEntry(moved, previousDate = day)

        val result = client.sync("127.0.0.1", serverInstance.port)
        assertTrue("date sync failed: $result", result is SyncResult.Success)

        assertEquals(day.plusDays(1), serverRepo.loadAll().single().date)
        assertEquals(day.plusDays(1), clientRepo.loadAll().single().date)
        assertEquals(false, File(temp.root, "server2/Bills/2026/10/18/bills.json").exists())
    }
}
