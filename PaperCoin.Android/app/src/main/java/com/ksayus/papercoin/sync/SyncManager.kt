package com.ksayus.papercoin.sync

import com.ksayus.papercoin.BuildConfig
import com.ksayus.papercoin.core.AppLog
import com.ksayus.papercoin.data.BillRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import java.io.File

data class SyncUiState(
    val enabled: Boolean = false,
    val autoSync: Boolean = true,
    val code: String = "",
    val deviceName: String = "",
    val running: Boolean = false,
    val busy: Boolean = false,
    val status: String = "同步已关闭",
    val discovered: List<DiscoveredPeer> = emptyList(),
    val peers: List<SyncPeer> = emptyList(),
    val pendingConflicts: List<SyncConflict> = emptyList(),
    val conflictPeerName: String = "",
) {
    val lastSyncAt: Long get() = peers.maxOfOrNull { it.lastSyncAt } ?: 0L
}

/**
 * 同步协调器。Android 只在前台运行：应用退到后台时关闭 TCP/UDP 端口，
 * 回到前台再重新发现。已配对设备自动同步，陌生设备只出现在列表里等用户手动点。
 */
class SyncManager(
    private val repository: BillRepository,
    private val store: SyncStore,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val server = SyncServer(repository, store, BuildConfig.VERSION_NAME)
    private val client = SyncClient(repository, store, BuildConfig.VERSION_NAME)
    private val syncMutex = Mutex()

    private val _ui = MutableStateFlow(
        SyncUiState(
            enabled = store.state.value.enabled,
            autoSync = store.state.value.autoSync,
            code = store.state.value.code,
            deviceName = store.state.value.deviceName,
            peers = store.state.value.peers,
            status = initialStatus(store.state.value),
        ),
    )
    val ui: StateFlow<SyncUiState> = _ui.asStateFlow()

    private var foreground = false
    private var discovery: SyncDiscovery? = null
    private val lastAutoAttempt = mutableMapOf<String, Long>()

    private var pendingTarget: Target? = null
    private var forcedChoices: Map<String, ConflictChoice> = emptyMap()

    private data class Target(
        val host: String,
        val port: Int,
        val peerId: String,
        val peerName: String,
    )

    init {
        scope.launch {
            store.state.collect { state ->
                _ui.update {
                    it.copy(
                        enabled = state.enabled,
                        autoSync = state.autoSync,
                        code = state.code,
                        deviceName = state.deviceName,
                        peers = state.peers,
                    )
                }
            }
        }
    }

    /** 应用回到前台。 */
    fun start() {
        foreground = true
        restartNetwork()
    }

    /** 应用退到后台：关闭端口，但保留设置与基线。 */
    fun stop() {
        foreground = false
        stopNetwork()

        _ui.update {
            it.copy(
                running = false,
                busy = false,
                discovered = emptyList(),
                status = if (it.enabled) "同步已暂停（应用不在前台）" else "同步已关闭",
            )
        }
    }

    fun setEnabled(enabled: Boolean) {
        if (enabled && store.state.value.code.isBlank()) {
            store.generateCode()
        }

        store.setEnabled(enabled)
        _ui.update { it.copy(enabled = enabled, status = if (enabled) "正在启动同步…" else "同步已关闭") }

        if (!enabled) {
            pendingTarget = null
            forcedChoices = emptyMap()
            _ui.update {
                it.copy(
                    pendingConflicts = emptyList(),
                    conflictPeerName = "",
                )
            }
        }

        restartNetwork()
    }

    fun setAutoSync(enabled: Boolean) {
        store.setAutoSync(enabled)
        _ui.update { it.copy(autoSync = enabled) }
    }

    fun setCode(code: String): Boolean {
        val ok = store.setCode(code)
        if (!ok) {
            _ui.update { it.copy(status = "同步码需为 6-12 位字母或数字") }
            return false
        }

        _ui.update { it.copy(code = store.state.value.code, status = "同步码已更新") }
        restartNetwork()
        return true
    }

    fun generateCode(): String {
        val code = store.generateCode()
        _ui.update { it.copy(code = code, status = "已生成新的同步码，请同步到另一台设备") }
        restartNetwork()
        return code
    }

    fun setDeviceName(name: String) {
        if (name.isBlank()) {
            _ui.update { it.copy(status = "设备名称不能为空") }
            return
        }

        store.setDeviceName(name)
        _ui.update { it.copy(deviceName = store.state.value.deviceName) }
        restartNetwork()
    }

    fun forgetPeer(deviceId: String) {
        store.removePeer(deviceId)
        _ui.update { it.copy(status = "已解除配对，下次需要重新同步") }
    }

    fun syncPeer(deviceId: String) {
        if (!canSyncNow()) {
            _ui.update { it.copy(status = "请先启用同步并设置同步码") }
            return
        }

        val peer = _ui.value.discovered.firstOrNull { it.deviceId == deviceId }

        if (peer == null) {
            _ui.update { it.copy(status = "暂时没有在局域网发现这台设备") }
            return
        }

        launchSync(
            Target(peer.host, peer.tcpPort, peer.deviceId, peer.deviceName),
            forcedChoices = emptyMap(),
        )
    }

    fun syncHost(host: String, port: Int) {
        if (!canSyncNow()) {
            _ui.update { it.copy(status = "请先启用同步并设置同步码") }
            return
        }

        if (host.isBlank() || port !in 1..65535) {
            _ui.update { it.copy(status = "请输入 主机:端口") }
            return
        }

        launchSync(Target(host.trim(), port, "", host.trim()), forcedChoices = emptyMap())
    }

    fun resolveConflicts(choices: Map<String, ConflictChoice>) {
        val target = pendingTarget ?: return
        forcedChoices = forcedChoices + choices
        launchSync(target, forcedChoices)
    }

    fun cancelConflicts() {
        pendingTarget = null
        forcedChoices = emptyMap()

        _ui.update {
            it.copy(
                pendingConflicts = emptyList(),
                conflictPeerName = "",
                status = "已取消同步，未修改任何账单",
            )
        }
    }

    private fun launchSync(target: Target, forcedChoices: Map<String, ConflictChoice>) {
        scope.launch {
            performSync(target, forcedChoices)
        }
    }

    private suspend fun performSync(
        target: Target,
        choices: Map<String, ConflictChoice>,
    ) {
        if (!syncMutex.tryLock()) {
            _ui.update { it.copy(status = "正在同步，请稍候") }
            return
        }

        try {
            _ui.update {
                it.copy(
                    busy = true,
                    status = "正在与 ${target.peerName} 同步…",
                )
            }

            val result = client.sync(target.host, target.port, choices)

            when (result) {
                is SyncResult.Success -> {
                    pendingTarget = null
                    forcedChoices = emptyMap()

                    _ui.update {
                        it.copy(
                            busy = false,
                            status = "已与 ${result.peerName} 同步（更新本机 ${result.localChanges} 条，对方 ${result.remoteChanges} 条）",
                            pendingConflicts = emptyList(),
                            conflictPeerName = "",
                        )
                    }
                }

                is SyncResult.Conflicts -> {
                    pendingTarget = Target(
                        host = result.host,
                        port = result.port,
                        peerId = result.peerId,
                        peerName = result.peerName,
                    )
                    forcedChoices = choices

                    _ui.update {
                        it.copy(
                            busy = false,
                            status = "发现 ${result.conflicts.size} 处冲突，请选择保留哪个版本",
                            pendingConflicts = result.conflicts,
                            conflictPeerName = result.peerName,
                        )
                    }
                }

                is SyncResult.Failure -> {
                    _ui.update {
                        it.copy(
                            busy = false,
                            status = result.message,
                        )
                    }
                }
            }
        } finally {
            syncMutex.unlock()
        }
    }

    private fun restartNetwork() {
        stopNetwork()

        val state = store.state.value
        if (!foreground || !state.enabled || state.code.isBlank()) {
            _ui.update {
                it.copy(
                    running = false,
                    discovered = emptyList(),
                    status = when {
                        !it.enabled -> "同步已关闭"
                        it.code.isBlank() -> "请先设置同步码"
                        else -> "同步已暂停（应用不在前台）"
                    },
                )
            }
            return
        }

        try {
            server.start(scope)
        } catch (ex: Exception) {
            AppLog.warn("启动同步服务失败", ex)
            _ui.update {
                it.copy(
                    running = false,
                    status = "无法启动同步服务，请检查端口是否被占用",
                )
            }
            return
        }

        val localDiscovery = SyncDiscovery(
            deviceId = state.deviceId,
            deviceName = { store.state.value.deviceName },
            tcpPort = { server.port },
            onOffer = ::onOffer,
        )

        discovery = localDiscovery
        localDiscovery.start(scope)

        _ui.update {
            it.copy(
                running = true,
                status = "正在局域网寻找已配对设备…",
            )
        }
    }

    private fun stopNetwork() {
        discovery?.stop()
        discovery = null
        server.stop()
        lastAutoAttempt.clear()
    }

    private fun onOffer(peer: DiscoveredPeer) {
        _ui.update { current ->
            val others = current.discovered.filterNot { it.deviceId == peer.deviceId }
            current.copy(discovered = (others + peer).sortedBy { it.deviceName })
        }

        val state = store.state.value
        if (!state.enabled || !state.autoSync || state.code.isBlank()) return
        if (_ui.value.pendingConflicts.isNotEmpty() || _ui.value.busy) return

        val known = store.peer(peer.deviceId) ?: return

        // 两端都发现对方时只让 ID 较小的一端发起，避免双连接。
        if (state.deviceId >= peer.deviceId) return

        val now = System.currentTimeMillis()
        synchronized(lastAutoAttempt) {
            val last = lastAutoAttempt[peer.deviceId] ?: 0L
            if (now - last < 20_000) return
            lastAutoAttempt[peer.deviceId] = now
        }

        launchSync(
            Target(peer.host, peer.tcpPort, peer.deviceId, known.deviceName.ifBlank { peer.deviceName }),
            forcedChoices = emptyMap(),
        )
    }

    private fun initialStatus(state: SyncState): String = when {
        !state.enabled -> "同步已关闭"
        state.code.isBlank() -> "请先设置同步码"
        else -> "同步待启动"
    }

    private fun canSyncNow(): Boolean {
        val state = store.state.value
        return state.enabled && SyncStore.isValidCode(state.code)
    }
}
