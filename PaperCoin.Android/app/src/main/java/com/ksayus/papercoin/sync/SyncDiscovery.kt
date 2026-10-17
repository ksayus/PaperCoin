package com.ksayus.papercoin.sync

import com.ksayus.papercoin.core.AppLog
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException

data class DiscoveredPeer(
    val deviceId: String,
    val deviceName: String,
    val host: String,
    val tcpPort: Int,
    val lastSeenAt: Long,
)

/**
 * UDP 广播发现。两端都监听 47831：收到 discover 回 offer，收到 offer 交给协调器。
 * 广播地址按每块网卡的 IPv4 掩码算，外加 255.255.255.255。
 */
class SyncDiscovery(
    private val deviceId: String,
    private val deviceName: () -> String,
    private val tcpPort: () -> Int,
    private val onOffer: (DiscoveredPeer) -> Unit,
) {
    private var socket: DatagramSocket? = null
    private var receiveJob: Job? = null
    private var broadcastJob: Job? = null

    fun start(scope: CoroutineScope) {
        if (socket != null) return

        val created = try {
            DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                soTimeout = 1_000
                bind(InetSocketAddress(SYNC_DISCOVERY_PORT))
            }
        } catch (ex: Exception) {
            AppLog.warn("同步发现端口不可用，改为手动添加设备", ex)
            return
        }

        socket = created
        receiveJob = scope.launch(Dispatchers.IO) { receiveLoop(created) }
        broadcastJob = scope.launch(Dispatchers.IO) { broadcastLoop(created) }
    }

    fun stop() {
        receiveJob?.cancel()
        broadcastJob?.cancel()
        receiveJob = null
        broadcastJob = null

        socket?.close()
        socket = null
    }

    fun broadcastNow() {
        val current = socket ?: return
        sendDiscover(current)
    }

    private suspend fun receiveLoop(socket: DatagramSocket) {
        val buffer = ByteArray(4096)

        while (currentCoroutineContext().isActive) {
            val packet = DatagramPacket(buffer, buffer.size)

            try {
                socket.receive(packet)
            } catch (ex: SocketTimeoutException) {
                continue
            } catch (ex: Exception) {
                if (currentCoroutineContext().isActive) {
                    AppLog.warn("同步发现接收失败", ex)
                }
                break
            }

            val text = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)
            val message = runCatching { SyncJson.decodeFromString<WireMessage>(text) }.getOrNull()
                ?: continue

            val peerId = message.deviceId
            if (peerId.isNullOrBlank() || peerId == deviceId) continue

            when (message.type) {
                "discover" -> sendOffer(socket, packet.address, packet.port, peerId, message.deviceName)
                "offer" -> {
                    val port = message.tcpPort ?: continue
                    val host = packet.address?.hostAddress ?: continue

                    onOffer(
                        DiscoveredPeer(
                            deviceId = peerId,
                            deviceName = message.deviceName.orEmpty().ifBlank { "未命名设备" },
                            host = host,
                            tcpPort = port,
                            lastSeenAt = System.currentTimeMillis(),
                        ),
                    )
                }
            }
        }
    }

    private suspend fun broadcastLoop(socket: DatagramSocket) {
        delay(400)

        while (currentCoroutineContext().isActive) {
            sendDiscover(socket)
            delay(2_000)
        }
    }

    private fun sendDiscover(socket: DatagramSocket) {
        val message = WireMessage(
            type = "discover",
            protocol = SYNC_PROTOCOL_VERSION,
            deviceId = deviceId,
            deviceName = deviceName(),
            tcpPort = tcpPort(),
        )

        val bytes = runCatching { SyncJson.encodeToString(message).toByteArray(Charsets.UTF_8) }
            .getOrNull() ?: return

        for (address in broadcastAddresses()) {
            try {
                socket.send(DatagramPacket(bytes, bytes.size, address, SYNC_DISCOVERY_PORT))
            } catch (ex: Exception) {
                // 某块网卡不允许广播时继续尝试其它网卡
            }
        }
    }

    private fun sendOffer(
        socket: DatagramSocket,
        address: InetAddress,
        port: Int,
        peerId: String,
        peerName: String?,
    ) {
        if (peerId.isBlank()) return

        val message = WireMessage(
            type = "offer",
            protocol = SYNC_PROTOCOL_VERSION,
            deviceId = deviceId,
            deviceName = deviceName(),
            tcpPort = tcpPort(),
        )

        val bytes = runCatching { SyncJson.encodeToString(message).toByteArray(Charsets.UTF_8) }
            .getOrNull() ?: return

        runCatching {
            socket.send(DatagramPacket(bytes, bytes.size, address, port))
        }.onFailure {
            AppLog.warn("同步发现回包失败: $peerName", it)
        }
    }

    private fun broadcastAddresses(): List<InetAddress> {
        val addresses = LinkedHashSet<InetAddress>()

        runCatching { addresses += InetAddress.getByName("255.255.255.255") }

        try {
            for (nif in NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()) {
                if (!nif.isUp || nif.isLoopback) continue

                for (interfaceAddress in nif.interfaceAddresses) {
                    if (interfaceAddress.address is Inet4Address) {
                        interfaceAddress.broadcast?.let { addresses += it }
                    }
                }
            }
        } catch (ex: Exception) {
            AppLog.warn("枚举局域网广播地址失败", ex)
        }

        return addresses.toList()
    }
}
