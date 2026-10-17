package com.ksayus.papercoin.sync

import com.ksayus.papercoin.core.AppLog
import com.ksayus.papercoin.data.PaperCoinJson
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.Serializable
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import java.io.File
import java.util.UUID
import kotlin.random.Random

@Serializable
data class SyncPeer(
    val deviceId: String,
    val deviceName: String = "",
    val lastSyncAt: Long = 0L,
    val baseline: Map<String, String> = emptyMap(),
)

@Serializable
data class SyncState(
    val enabled: Boolean = false,
    val autoSync: Boolean = true,
    val code: String = "",
    val deviceId: String = UUID.randomUUID().toString(),
    val deviceName: String = "",
    val peers: List<SyncPeer> = emptyList(),
)

/**
 * 同步设置与每台已知设备的基线。与桌面版共用 `Sync/papercoin-sync.json`；
 * 基线不写进账单 JSON，避免污染账单文件。
 */
class SyncStore(
    private val file: File,
    private val defaultDeviceName: String,
) {
    private val lock = Any()
    private val _state = MutableStateFlow(load())
    val state: StateFlow<SyncState> = _state.asStateFlow()

    init {
        ensureDefaults()
    }

    fun setEnabled(enabled: Boolean) {
        update { it.copy(enabled = enabled) }
    }

    fun setAutoSync(autoSync: Boolean) {
        update { it.copy(autoSync = autoSync) }
    }

    /** 返回是否写入成功；非法码不落盘，由界面提示。 */
    fun setCode(code: String): Boolean {
        val normalized = normalizeCode(code)
        if (!isValidCode(normalized)) return false

        update { it.copy(code = normalized) }
        return true
    }

    fun setDeviceName(name: String) {
        val trimmed = name.trim().take(48)
        if (trimmed.isEmpty()) return

        update { it.copy(deviceName = trimmed) }
    }

    fun generateCode(): String {
        val code = buildString {
            repeat(8) {
                append(CODE_ALPHABET[Random.nextInt(CODE_ALPHABET.length)])
            }
        }

        update { it.copy(code = code) }
        return code
    }

    fun peer(deviceId: String): SyncPeer? = _state.value.peers.firstOrNull { it.deviceId == deviceId }

    fun recordSync(
        deviceId: String,
        deviceName: String,
        baseline: Map<String, String>,
        atMillis: Long = System.currentTimeMillis(),
    ) {
        if (deviceId.isBlank()) return

        update { current ->
            val existing = current.peers.firstOrNull { it.deviceId == deviceId }
            val peer = SyncPeer(
                deviceId = deviceId,
                deviceName = deviceName.ifBlank { existing?.deviceName.orEmpty() },
                lastSyncAt = atMillis,
                baseline = baseline,
            )

            current.copy(
                peers = current.peers.filterNot { it.deviceId == deviceId } + peer,
            )
        }
    }

    fun removePeer(deviceId: String) {
        update { current -> current.copy(peers = current.peers.filterNot { it.deviceId == deviceId }) }
    }

    private fun ensureDefaults() {
        val current = _state.value
        val normalizedCode = normalizeCode(current.code)
        val validCode = isValidCode(normalizedCode)

        val fixed = current.copy(
            enabled = current.enabled && validCode,
            code = if (validCode) normalizedCode else "",
            deviceId = current.deviceId.ifBlank { UUID.randomUUID().toString() },
            deviceName = current.deviceName.ifBlank { defaultDeviceName },
        )

        if (fixed != current) {
            update { fixed }
        }
    }

    private fun update(transform: (SyncState) -> SyncState) {
        synchronized(lock) {
            val next = transform(_state.value)
            if (next == _state.value) return

            try {
                file.parentFile?.mkdirs()
                file.writeText(PaperCoinJson.encodeToString(next))
            } catch (ex: Exception) {
                AppLog.warn("同步设置保存失败: ${file.path}", ex)
                return
            }

            _state.value = next
        }
    }

    private fun load(): SyncState {
        if (!file.isFile) return SyncState(deviceName = defaultDeviceName)

        return try {
            PaperCoinJson.decodeFromString<SyncState>(file.readText())
        } catch (ex: Exception) {
            AppLog.warn("同步设置读取失败，使用默认值: ${file.path}", ex)
            SyncState(deviceName = defaultDeviceName)
        }
    }

    companion object {
        private const val CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"

        fun normalizeCode(code: String): String = code.trim().uppercase()

        fun isValidCode(code: String): Boolean = code.matches(Regex("[A-Z0-9]{6,12}"))
    }
}
