package com.ksayus.papercoin.data

import kotlinx.serialization.Serializable

/**
 * 程序配置。字段与桌面版 `AppConfig.cs` 完全一致，导入桌面数据时可以直接复用。
 * `autoStart` 在 Android 上没有对应能力，但保留字段，避免覆盖桌面配置时丢键。
 */
@Serializable
data class AppConfig(
    val app: AppSection = AppSection(),
    val logging: LoggingSection = LoggingSection(),
)

@Serializable
data class AppSection(
    val autoUpdate: Boolean = false,
    val autoUpdateSource: String = "Github",
    val autoStart: Boolean = true,
    val theme: String = "Dark",
    val themeStyle: String = "Classic",
)

@Serializable
data class LoggingSection(
    val level: String = "Infomation",
    val retainDays: Int = 7,
)
