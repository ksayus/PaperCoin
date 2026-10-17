package com.ksayus.papercoin.data

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import java.io.File
import java.io.IOException

/**
 * 配置读写。文件布局与桌面版一致：`Config/papercoin-settings.json`，
 * 因此从桌面版复制过来的配置可以直接被识别。
 */
class ConfigRepository(private val file: File) {
    private val _config: MutableStateFlow<AppConfig>
    val config: StateFlow<AppConfig>

    init {
        file.parentFile?.mkdirs()

        if (!file.isFile) {
            runCatching { file.writeText(PaperCoinJson.encodeToString(AppConfig())) }
        }

        _config = MutableStateFlow(read())
        config = _config.asStateFlow()
    }

    /** 读改写整体落盘；mutate 只拿到当前快照，避免调用方长期持有一个会过期的实例。 */
    fun update(mutate: (AppConfig) -> AppConfig): AppConfig {
        val next = mutate(_config.value)
        save(next)
        return next
    }

    fun save(config: AppConfig) {
        try {
            file.parentFile?.mkdirs()
            file.writeText(PaperCoinJson.encodeToString(config))
            _config.value = config
        } catch (ex: IOException) {
            // 配置写失败不应该让界面操作崩掉；内存状态保持原样。
            com.ksayus.papercoin.core.AppLog.warn("配置保存失败: ${file.path}", ex)
        }
    }

    private fun read(): AppConfig {
        if (!file.isFile) return AppConfig()

        return runCatching {
            PaperCoinJson.decodeFromString<AppConfig>(file.readText())
        }.getOrElse {
            com.ksayus.papercoin.core.AppLog.warn("配置读取失败，使用默认值: ${file.path}", it)
            AppConfig()
        }
    }
}
