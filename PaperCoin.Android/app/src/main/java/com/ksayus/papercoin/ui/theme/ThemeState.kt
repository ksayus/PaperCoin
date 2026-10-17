package com.ksayus.papercoin.ui.theme

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.ksayus.papercoin.data.ConfigRepository

/**
 * 主题的两个独立维度：明暗（System / Dark / Bright）与风格（Classic / Windows / Liquid）。
 * 两个维度各自持久化到配置的 `theme` 与 `themeStyle`，与桌面版同键，导入配置后观感一致。
 */
class ThemeState(private val config: ConfigRepository) {
    var mode by mutableStateOf(normalizeMode(config.config.value.app.theme))
        private set

    var style by mutableStateOf(normalizeStyle(config.config.value.app.themeStyle))
        private set

    val isSystemMode: Boolean get() = mode == MODE_SYSTEM
    val isDarkMode: Boolean get() = mode == MODE_DARK
    val isBrightMode: Boolean get() = mode == MODE_BRIGHT

    val isClassicStyle: Boolean get() = style == STYLE_CLASSIC
    val isWindowsStyle: Boolean get() = style == STYLE_WINDOWS
    val isLiquidStyle: Boolean get() = style == STYLE_LIQUID

    fun applyMode(value: String) {
        val normalized = normalizeMode(value)
        if (normalized == mode) return
        mode = normalized
        persist()
    }

    fun applyStyle(value: String) {
        val normalized = normalizeStyle(value)
        if (normalized == style) return
        style = normalized
        persist()
    }

    fun reset() {
        mode = MODE_DARK
        style = STYLE_CLASSIC
        persist()
    }

    /** 风格说明，给设置页那一行副标题用 */
    fun styleText(): String = when (style) {
        STYLE_WINDOWS -> "Fluent 观感：纯色表面、中性灰阶、系统蓝强调"
        STYLE_LIQUID -> "半透明材质 + 发丝高光，玻璃面板透出背板"
        else -> "项目原本的观感：圆角卡片 + 柔和阴影"
    }

    private fun persist() {
        config.update {
            it.copy(app = it.app.copy(theme = mode, themeStyle = style))
        }
    }

    companion object {
        const val MODE_SYSTEM = "System"
        const val MODE_DARK = "Dark"
        const val MODE_BRIGHT = "Bright"

        const val STYLE_CLASSIC = "Classic"
        const val STYLE_WINDOWS = "Windows"
        const val STYLE_LIQUID = "Liquid"

        fun normalizeMode(mode: String?): String = when {
            mode.equals(MODE_BRIGHT, ignoreCase = true) -> MODE_BRIGHT
            mode.equals(MODE_SYSTEM, ignoreCase = true) -> MODE_SYSTEM
            else -> MODE_DARK
        }

        fun normalizeStyle(style: String?): String = when {
            style.equals(STYLE_WINDOWS, ignoreCase = true) -> STYLE_WINDOWS
            style.equals(STYLE_LIQUID, ignoreCase = true) -> STYLE_LIQUID
            else -> STYLE_CLASSIC
        }
    }
}
