package com.ksayus.papercoin.vm

import android.content.Context
import android.net.Uri
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.ksayus.papercoin.BuildConfig
import com.ksayus.papercoin.data.AppConfig
import com.ksayus.papercoin.data.BackupManager
import com.ksayus.papercoin.data.ConfigRepository
import com.ksayus.papercoin.data.SemanticVersion
import com.ksayus.papercoin.data.UpdateManager
import com.ksayus.papercoin.data.UpdateRelease
import com.ksayus.papercoin.data.UpdateSource
import com.ksayus.papercoin.ui.theme.ThemeState
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import java.io.File

/** 设置页。外观只改 ThemeState；更新与备份的副作用都在这里收口。 */
class SettingsViewModel(
    private val config: ConfigRepository,
    private val theme: ThemeState,
    private val updates: UpdateManager,
    private val backup: BackupManager,
    val dataRoot: File,
) : ViewModel() {
    var autoUpdate by mutableStateOf(config.config.value.app.autoUpdate)
        private set

    var updateSource by mutableStateOf(config.config.value.app.autoUpdateSource)
        private set

    var statusMessage by mutableStateOf(updates.lastMessage.value)
        private set

    var releaseNotes by mutableStateOf("")
        private set

    var availableRelease by mutableStateOf(updates.available.value)
        private set

    var isBusy by mutableStateOf(false)
        private set

    var isDownloading by mutableStateOf(false)
        private set

    var downloadPercent by mutableStateOf(0f)
        private set

    var downloadText by mutableStateOf("")
        private set

    var notice by mutableStateOf("")

    init {
        availableRelease?.let { releaseNotes = summarize(it.notes) }

        // 启动时那次后台查询可能在本页创建之后才回来，结果要能落到界面上。
        viewModelScope.launch {
            updates.available.collect { release ->
                availableRelease = release
                releaseNotes = release?.let { summarize(it.notes) }.orEmpty()
            }
        }
    }

    val versionText: String get() = BuildConfig.VERSION_NAME
    val dataFolderText: String get() = dataRoot.path
    val isIdle: Boolean get() = !isBusy

    val updateStatusText: String
        get() = when {
            statusMessage.isNotEmpty() -> statusMessage
            autoUpdate -> "已启用（更新源 $updateSource）"
            else -> "已关闭"
        }

    val updateHeadline: String
        get() = availableRelease?.let { release ->
            "发现新版本 ${release.version}（当前 ${updates.currentVersion()}）" +
                if (release.installerUrl == null) "· 需手动下载" else ""
        }.orEmpty()

    val hasUpdate: Boolean get() = availableRelease != null
    val canInstall: Boolean get() = availableRelease?.installerUrl != null && !isBusy
    val mustDownloadManually: Boolean get() = availableRelease != null && availableRelease?.installerUrl == null
    val hasReleaseNotes: Boolean get() = releaseNotes.isNotBlank()

    val sourceOptions: List<String> get() = listOf("Github", "Gitee")

    fun setMode(mode: String) = theme.applyMode(mode)

    fun setStyle(style: String) = theme.applyStyle(style)

    fun toggleAutoUpdate() {
        val target = !autoUpdate
        autoUpdate = target
        persist { it.copy(autoUpdate = target) }

        if (target) {
            check(manual = false)
        }
    }

    fun applyUpdateSource(value: String) {
        if (value == updateSource) return
        updateSource = value
        persist { it.copy(autoUpdateSource = value) }
    }

    fun check(manual: Boolean) {
        if (isBusy) return

        isBusy = true
        statusMessage = "正在检查…"

        viewModelScope.launch {
            val check = updates.checkAndRemember(sourceOf(updateSource), updates.currentVersion())

            availableRelease = updates.available.value
            releaseNotes = availableRelease?.let { summarize(it.notes) }.orEmpty()

            statusMessage = if (check.isFailure || manual) check.message else ""

            isBusy = false
        }
    }

    fun downloadAndInstall() {
        val release = availableRelease ?: return
        if (isBusy || release.installerUrl == null) return

        isBusy = true
        isDownloading = true
        downloadPercent = 0f
        downloadText = "正在下载…"

        viewModelScope.launch {
            val (file, failure) = updates.download(release) { done, total ->
                if (total <= 0) {
                    downloadText = "正在下载…${done / 1024.0 / 1024.0} MB"
                } else {
                    downloadPercent = (done.toFloat() / total.toFloat()).coerceIn(0f, 1f)
                    downloadText = "正在下载…${(downloadPercent * 100).toInt()}%"
                }
            }

            if (file == null) {
                statusMessage = failure
                isDownloading = false
                isBusy = false
                return@launch
            }

            downloadText = "下载完成，正在启动安装程序…"
            val message = updates.install(file)

            statusMessage = message.ifEmpty { "安装程序已启动，按提示完成升级" }

            isDownloading = false
            isBusy = false
        }
    }

    fun exportBackup(uri: Uri) {
        viewModelScope.launch {
            notice = backup.exportZip(uri)
        }
    }

    fun importZip(uri: Uri) {
        viewModelScope.launch {
            notice = backup.importZip(uri)
        }
    }

    fun importFolder(uri: Uri) {
        viewModelScope.launch {
            notice = backup.importFolder(uri)
        }
    }

    fun resetToDefaults() {
        config.save(AppConfig())
        theme.reset()

        autoUpdate = false
        updateSource = "Github"
        statusMessage = ""
        availableRelease = null
        releaseNotes = ""
        notice = "已恢复默认设置"
    }

    fun dismissNotice() {
        notice = ""
    }

    private fun persist(mutate: (com.ksayus.papercoin.data.AppSection) -> com.ksayus.papercoin.data.AppSection) {
        config.update { it.copy(app = mutate(it.app)) }
    }

    private fun sourceOf(value: String): UpdateSource =
        if (value.equals("Gitee", ignoreCase = true)) UpdateSource.GITEE else UpdateSource.GITHUB

    /** 发布说明常常很长，界面上只留前几行。 */
    private fun summarize(notes: String, maxLines: Int = 8): String {
        if (notes.isBlank()) return ""

        val lines = notes
            .replace("\r\n", "\n")
            .split('\n')
            .map { it.trim() }
            .filter { it.isNotEmpty() }

        return if (lines.size <= maxLines) {
            lines.joinToString("\n")
        } else {
            lines.take(maxLines).joinToString("\n") + "\n…（完整说明见发布页）"
        }
    }
}
