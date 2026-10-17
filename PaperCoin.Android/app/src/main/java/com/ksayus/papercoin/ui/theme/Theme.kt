package com.ksayus.papercoin.ui.theme

import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.getValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.core.view.WindowCompat
import com.ksayus.papercoin.R

/** 当前生效的调色板。切换主题时这里的每个颜色都以 300ms 过渡到位。 */
val LocalAppColors = staticCompositionLocalOf { Palettes.Dark }

/** 自建图标字体，码位与语义见 Icon/AppIcons.xaml */
val IconFont = FontFamily(Font(R.font.iconfont))

private val AppTypography = Typography()

@Composable
fun PaperCoinTheme(themeState: ThemeState, content: @Composable () -> Unit) {
    val systemDark = isSystemInDarkTheme()

    val bright = when (themeState.mode) {
        ThemeState.MODE_SYSTEM -> !systemDark
        ThemeState.MODE_BRIGHT -> true
        else -> false
    }

    val target = Palettes.of(bright, themeState.style)
    val colors = rememberAnimatedColors(target)

    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = (view.context as? android.app.Activity)?.window ?: return@SideEffect
            val controller = WindowCompat.getInsetsController(window, view)
            controller.isAppearanceLightStatusBars = bright
            controller.isAppearanceLightNavigationBars = bright
        }
    }

    CompositionLocalProvider(LocalAppColors provides colors) {
        MaterialTheme(
            colorScheme = appColorScheme(colors, bright),
            typography = AppTypography,
            content = content,
        )
    }
}

/**
 * 逐个颜色做 300ms 动画。比"直接换整本字典"多一层状态，换来与桌面版一致的过渡，
 * 而不是切换瞬间跳色。
 */
@Composable
private fun rememberAnimatedColors(target: AppColors): AppColors {
    val spec = tween<Color>(durationMillis = 300)

    @Composable
    fun animated(color: Color): Color =
        animateColorAsState(targetValue = color, animationSpec = spec, label = "palette").value

    return AppColors(
        glassBackdrop = animated(target.glassBackdrop),
        glassScrim = animated(target.glassScrim),
        titleTop = animated(target.titleTop),
        sideBar = animated(target.sideBar),
        mainContentArea = animated(target.mainContentArea),
        cardBackground = animated(target.cardBackground),
        cardBorder = animated(target.cardBorder),
        cardActionBackground = animated(target.cardActionBackground),
        sideBarBtn = animated(target.sideBarBtn),
        sideBarBtnOver = animated(target.sideBarBtnOver),
        sideBarBtnActive = animated(target.sideBarBtnActive),
        sideBarBtnPressed = animated(target.sideBarBtnPressed),
        addNewBillBtn = animated(target.addNewBillBtn),
        addNewBillBtnOver = animated(target.addNewBillBtnOver),
        addNewBillBtnPressed = animated(target.addNewBillBtnPressed),
        textTitle = animated(target.textTitle),
        textSecondary = animated(target.textSecondary),
        positive = animated(target.positive),
        negative = animated(target.negative),
        shadow = animated(target.shadow),
        windowShadow = animated(target.windowShadow),
        categoryColors = target.categoryColors.map { animated(it) },
        categoryOther = animated(target.categoryOther),
    )
}

private fun appColorScheme(colors: AppColors, bright: Boolean): ColorScheme {
    val base = if (bright) lightColorScheme() else darkColorScheme()

    return base.copy(
        primary = colors.addNewBillBtn,
        onPrimary = Color.White,
        primaryContainer = colors.cardActionBackground,
        onPrimaryContainer = colors.textTitle,
        secondary = colors.textSecondary,
        onSecondary = if (bright) Color.White else colors.mainContentArea,
        background = colors.mainContentArea,
        onBackground = colors.textTitle,
        surface = colors.cardBackground,
        onSurface = colors.textTitle,
        surfaceVariant = colors.cardActionBackground,
        onSurfaceVariant = colors.textSecondary,
        outline = colors.cardBorder,
        outlineVariant = colors.cardBorder,
        error = colors.negative,
        onError = Color.White,
        scrim = Color.Black,
    )
}

/** 标题字重：桌面版用 SemiBold / Bold 区分层级 */
val SemiBold = FontWeight.SemiBold
val Bold = FontWeight.Bold
