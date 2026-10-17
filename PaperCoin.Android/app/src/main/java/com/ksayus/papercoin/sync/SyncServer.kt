package com.ksayus.papercoin.sync

import com.ksayus.papercoin.core.AppLog
import com.ksayus.papercoin.data.BillRepository
import com.ksayus.papercoin.data.SyncHash
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import java.io.EOFException
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket

/**
 * 被动同步端。只接受客户端的合并结果，不自行猜测冲突；
 * 应用前校验快照哈希，应用后再次校验最终哈希。
 */
class SyncServer(
    private val repository: BillRepository,
    private val store: SyncStore,
    private val appVersion: String,
) {
    private var serverSocket: ServerSocket? = null
    private var acceptJob: Job? = null
    private val sessionLock = Mutex()

    var port: Int = 0
        private set

    fun start(scope: CoroutineScope) {
        if (serverSocket != null) return

        val created = try {
            ServerSocket().apply {
                reuseAddress = true
                bind(InetSocketAddress(SYNC_PREFERRED_TCP_PORT))
            }
        } catch (ex: Exception) {
            ServerSocket(0)
        }

        serverSocket = created
        port = created.localPort

        acceptJob = scope.launch(Dispatchers.IO) { acceptLoop(created, scope) }
    }

    fun stop() {
        acceptJob?.cancel()
        acceptJob = null

        serverSocket?.close()
        serverSocket = null
        port = 0
    }

    private suspend fun acceptLoop(server: ServerSocket, scope: CoroutineScope) {
        while (currentCoroutineContext().isActive) {
            val client = try {
                server.accept()
            } catch (ex: Exception) {
                if (currentCoroutineContext().isActive) {
                    AppLog.warn("同步服务接受连接失败", ex)
                }
                break
            }

            scope.launch(Dispatchers.IO) {
                client.use { handle(it) }
            }
        }
    }

    private suspend fun handle(socket: Socket) {
        socket.soTimeout = 30_000

        if (!sessionLock.tryLock()) {
            sendError(socket, "busy", "另一台设备正在同步")
            return
        }

        try {
            handleSession(socket)
        } catch (ex: EOFException) {
            AppLog.warn("同步连接提前关闭")
        } catch (ex: Exception) {
            AppLog.warn("同步服务处理失败", ex)
            sendError(socket, "internal", "同步处理失败")
        } finally {
            sessionLock.unlock()
        }
    }

    private suspend fun handleSession(socket: Socket) {
        val input = socket.getInputStream()
        val output = socket.getOutputStream()

        val hello = SyncFraming.read(input)

        if (hello.type != "hello") {
            sendError(socket, "invalid", "同步握手格式不正确")
            return
        }

        if (hello.protocol != SYNC_PROTOCOL_VERSION) {
            sendError(socket, "protocol", "协议版本不一致")
            return
        }

        val peerId = hello.deviceId
        val peerName = hello.deviceName.orEmpty().ifBlank { "未命名设备" }

        if (peerId.isNullOrBlank()) {
            sendError(socket, "invalid", "缺少设备标识")
            return
        }

        val code = store.state.value.code
        if (code.isEmpty()) {
            sendError(socket, "auth", "本机还没有设置同步码")
            return
        }

        val nonce = SyncAuth.newNonce()
        SyncFraming.write(output, WireMessage(type = "challenge", nonce = nonce))

        val auth = SyncFraming.read(input)
        if (auth.type != "auth" || !SyncAuth.verify(code, nonce, peerId, auth.mac)) {
            sendError(socket, "auth", "同步码不一致")
            return
        }

        SyncFraming.write(
            output,
            WireMessage(
                type = "welcome",
                protocol = SYNC_PROTOCOL_VERSION,
                deviceId = store.state.value.deviceId,
                deviceName = store.state.value.deviceName,
                app = "android",
                version = appVersion,
            ),
        )

        val clientSnapshot = SyncFraming.read(input)
        if (clientSnapshot.type != "snapshot") {
            sendError(socket, "invalid", "缺少账单快照")
            return
        }

        try {
            clientSnapshot.toSnapshot()
        } catch (ex: Exception) {
            sendError(socket, "invalid", "账单快照校验失败")
            return
        }

        val localEntries = repository.loadAll()
        val localSnapshot = SyncSnapshot.of(localEntries)
        SyncFraming.write(output, snapshotMessage(localSnapshot))

        val next = SyncFraming.read(input)

        when (next.type) {
            "abort" -> return
            "apply" -> apply(socket, next, peerId, peerName)
            else -> sendError(socket, "invalid", "未知同步消息")
        }
    }

    private suspend fun apply(
        socket: Socket,
        message: WireMessage,
        peerId: String,
        peerName: String,
    ) {
        val expected = message.expectedSnapshotHash
        if (expected.isNullOrBlank()) {
            sendError(socket, "invalid", "缺少快照校验值")
            return
        }

        val currentHash = repository.snapshotHash()
        if (currentHash != expected) {
            sendError(socket, "snapshot-changed", "账单在同步过程中发生了变化，请重试")
            return
        }

        val changes = try {
            message.actions.orEmpty().toBillChanges()
        } catch (ex: Exception) {
            sendError(socket, "invalid", "同步变更格式不正确")
            return
        }

        val applied = repository.applySyncChanges(changes, expected)
        if (!applied) {
            sendError(socket, "snapshot-changed", "账单在同步过程中发生了变化，请重试")
            return
        }

        val finalEntries = repository.loadAll()
        val finalHash = SyncHash.snapshotHash(finalEntries)

        if (message.finalSnapshotHash != null && message.finalSnapshotHash != finalHash) {
            sendError(socket, "internal", "同步落盘校验失败")
            return
        }

        val actualBaseline = finalEntries.associate { it.id to SyncHash.contentHash(it) }
        val providedBaseline = message.baseline?.toBaselineMap()

        if (providedBaseline != null && providedBaseline != actualBaseline) {
            sendError(socket, "invalid", "同步基线校验失败")
            return
        }

        store.recordSync(peerId, peerName, actualBaseline)

        SyncFraming.write(
            socket.getOutputStream(),
            WireMessage(
                type = "applied",
                count = changes.size,
                hash = finalHash,
            ),
        )

        AppLog.warn("已接受来自 $peerName 的同步，应用 ${changes.size} 条变更")
    }

    private fun sendError(socket: Socket, code: String, message: String) {
        runCatching {
            SyncFraming.write(
                socket.getOutputStream(),
                WireMessage(type = "error", code = code, message = message),
            )
        }
    }
}
