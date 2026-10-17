package com.ksayus.papercoin.sync

import com.ksayus.papercoin.core.AppLog
import com.ksayus.papercoin.data.BillRepository
import com.ksayus.papercoin.data.SyncHash
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.EOFException
import java.io.IOException
import java.net.ConnectException
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException

sealed interface SyncResult {
    data class Success(
        val peerId: String,
        val peerName: String,
        val localChanges: Int,
        val remoteChanges: Int,
    ) : SyncResult

    data class Conflicts(
        val peerId: String,
        val peerName: String,
        val host: String,
        val port: Int,
        val conflicts: List<SyncConflict>,
    ) : SyncResult

    data class Failure(val message: String) : SyncResult
}

/**
 * 主动同步端。连上对方后只交换快照，真正的合并与冲突决策都在本机完成；
 * 用户取消时只发 abort，任何一侧都不落盘。
 */
class SyncClient(
    private val repository: BillRepository,
    private val store: SyncStore,
    private val appVersion: String,
) {
    suspend fun sync(
        host: String,
        port: Int,
        forcedChoices: Map<String, ConflictChoice> = emptyMap(),
    ): SyncResult = withContext(Dispatchers.IO) {
        val localState = store.state.value
        val code = localState.code

        if (code.isBlank()) return@withContext SyncResult.Failure("请先设置同步码")

        try {
            Socket().use { socket ->
                socket.connect(InetSocketAddress(host, port), 5_000)
                socket.soTimeout = 30_000

                val input = socket.getInputStream()
                val output = socket.getOutputStream()

                SyncFraming.write(
                    output,
                    WireMessage(
                        type = "hello",
                        protocol = SYNC_PROTOCOL_VERSION,
                        deviceId = localState.deviceId,
                        deviceName = localState.deviceName,
                        app = "android",
                        version = appVersion,
                    ),
                )

                val challenge = SyncFraming.read(input)
                failIfError(challenge)?.let { return@withContext it }

                if (challenge.type != "challenge" || challenge.nonce.isNullOrBlank()) {
                    return@withContext SyncResult.Failure("对方没有返回同步挑战")
                }

                SyncFraming.write(
                    output,
                    WireMessage(
                        type = "auth",
                        mac = SyncAuth.mac(code, challenge.nonce, localState.deviceId),
                    ),
                )

                val welcome = SyncFraming.read(input)
                failIfError(welcome)?.let { return@withContext it }

                if (welcome.type != "welcome") {
                    return@withContext SyncResult.Failure("对方没有接受同步")
                }

                val peerId = welcome.deviceId
                if (peerId.isNullOrBlank()) {
                    return@withContext SyncResult.Failure("对方没有返回设备标识")
                }
                val peerName = welcome.deviceName.orEmpty().ifBlank { "未命名设备" }

                val localEntries = repository.loadAll()
                val localSnapshot = SyncSnapshot.of(localEntries)
                SyncFraming.write(output, snapshotMessage(localSnapshot))

                val remoteMessage = SyncFraming.read(input)
                failIfError(remoteMessage)?.let { return@withContext it }

                if (remoteMessage.type != "snapshot") {
                    return@withContext SyncResult.Failure("对方没有返回账单快照")
                }

                val remoteSnapshot = try {
                    remoteMessage.toSnapshot()
                } catch (ex: Exception) {
                    return@withContext SyncResult.Failure("对方账单快照校验失败")
                }

                val baseline = store.peer(peerId)?.baseline.orEmpty()
                val plan = SyncMerge.plan(localSnapshot, remoteSnapshot, baseline, forcedChoices)

                if (plan.conflicts.isNotEmpty()) {
                    runCatching {
                        SyncFraming.write(output, WireMessage(type = "abort", reason = "conflict"))
                    }

                    return@withContext SyncResult.Conflicts(
                        peerId = peerId,
                        peerName = peerName,
                        host = host,
                        port = port,
                        conflicts = plan.conflicts,
                    )
                }

                SyncFraming.write(
                    output,
                    WireMessage(
                        type = "apply",
                        expectedSnapshotHash = remoteSnapshot.hash,
                        finalSnapshotHash = plan.finalHash,
                        actions = plan.remoteChanges.toWireActions(),
                        baseline = baselineItems(plan.finalEntries),
                    ),
                )

                val applied = SyncFraming.read(input)
                failIfError(applied)?.let { return@withContext it }

                if (applied.type != "applied") {
                    return@withContext SyncResult.Failure("对方没有确认同步结果")
                }

                if (applied.hash != null && applied.hash != plan.finalHash) {
                    return@withContext SyncResult.Failure("对方落盘后的账单与合并结果不一致")
                }

                // 本机从交换快照到现在如果被人改过，整次同步作废，避免覆盖新改动。
                val localApplied = repository.applySyncChanges(plan.localChanges, localSnapshot.hash)
                if (!localApplied) {
                    return@withContext SyncResult.Failure("账单在同步过程中发生了变化，请重试")
                }

                val finalEntries = repository.loadAll()
                val finalHash = SyncHash.snapshotHash(finalEntries)
                if (finalHash != plan.finalHash) {
                    return@withContext SyncResult.Failure("同步落盘校验失败，请重试")
                }

                store.recordSync(
                    deviceId = peerId,
                    deviceName = peerName,
                    baseline = baselineItems(plan.finalEntries).toBaselineMap(),
                )

                SyncResult.Success(
                    peerId = peerId,
                    peerName = peerName,
                    localChanges = plan.localChanges.size,
                    remoteChanges = plan.remoteChanges.size,
                )
            }
        } catch (ex: SocketTimeoutException) {
            SyncResult.Failure("连接或读取超时，请确认两台设备在同一局域网")
        } catch (ex: ConnectException) {
            SyncResult.Failure("无法连接对方设备，请确认对方已打开同步")
        } catch (ex: EOFException) {
            SyncResult.Failure("对方提前关闭了连接")
        } catch (ex: IOException) {
            SyncResult.Failure("网络中断：${ex.message ?: "未知错误"}")
        } catch (ex: Exception) {
            AppLog.warn("同步失败", ex)
            SyncResult.Failure("同步失败：${ex.message ?: "未知错误"}")
        }
    }

    private fun failIfError(message: WireMessage): SyncResult.Failure? {
        if (message.type != "error") return null

        val text = message.message
            ?: when (message.code) {
                "auth" -> "同步码不一致"
                "protocol" -> "两台设备的协议版本不一致"
                "busy" -> "对方正在同步，请稍后再试"
                "snapshot-changed" -> "账单在同步过程中发生了变化，请重试"
                else -> "同步失败"
            }

        return SyncResult.Failure(text)
    }
}
