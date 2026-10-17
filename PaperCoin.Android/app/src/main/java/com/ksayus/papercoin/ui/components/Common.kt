package com.ksayus.papercoin.ui.components

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.ksayus.papercoin.ui.theme.IconFont
import com.ksayus.papercoin.ui.theme.LocalAppColors
import com.ksayus.papercoin.ui.theme.ThemeState
import kotlinx.coroutines.delay
import kotlin.math.min

@Composable
fun IconText(
    icon: String,
    size: TextUnit = 18.sp,
    color: androidx.compose.ui.graphics.Color = LocalAppColors.current.textSecondary,
    modifier: Modifier = Modifier,
) {
    Text(
        text = icon,
        fontFamily = IconFont,
        fontSize = size,
        color = color,
        modifier = modifier,
    )
}

/** 桌面版 SettingRow / 卡片共用的表面：圆角 8、1px 边框、轻微投影。 */
@Composable
fun AppCard(
    modifier: Modifier = Modifier,
    padding: PaddingValues = PaddingValues(horizontal = 12.dp, vertical = 10.dp),
    shape: androidx.compose.ui.graphics.Shape = RoundedCornerShape(8.dp),
    content: @Composable ColumnScope.() -> Unit,
) {
    val colors = LocalAppColors.current

    Column(
        modifier = modifier
            .shadow(
                elevation = 2.dp,
                shape = shape,
                ambientColor = colors.shadow,
                spotColor = colors.shadow,
            )
            .clip(shape)
            .background(colors.cardBackground)
            .border(1.dp, colors.cardBorder, shape)
            .padding(padding),
        content = content,
    )
}

@Composable
fun SectionTitle(text: String, modifier: Modifier = Modifier) {
    Text(
        text = text,
        fontSize = 11.sp,
        color = LocalAppColors.current.textSecondary,
        modifier = modifier.padding(start = 2.dp, bottom = 6.dp),
    )
}

/** 日 / 月 / 年、主题三选一与统计范围共用的分段控件。 */
@Composable
fun SegmentedControl(
    options: List<Pair<String, String>>,
    selected: String,
    onSelect: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = LocalAppColors.current

    Row(modifier = modifier) {
        options.forEach { (value, label) ->
            val active = value == selected
            val background by animateColorAsState(
                targetValue = if (active) colors.addNewBillBtn else colors.cardBackground,
                animationSpec = tween(durationMillis = 200),
                label = "segmentBackground",
            )
            val foreground by animateColorAsState(
                targetValue = if (active) Color.White else colors.textSecondary,
                animationSpec = tween(durationMillis = 200),
                label = "segmentForeground",
            )

            Box(
                modifier = Modifier
                    .padding(end = 4.dp)
                    .clip(RoundedCornerShape(6.dp))
                    .background(background)
                    .border(1.dp, colors.cardBorder, RoundedCornerShape(6.dp))
                    .clickable { onSelect(value) }
                    .padding(horizontal = 10.dp, vertical = 5.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text(text = label, fontSize = 12.sp, color = foreground)
            }
        }
    }
}

@Composable
fun PillButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    fontSize: TextUnit = 11.sp,
    padding: PaddingValues = PaddingValues(horizontal = 12.dp, vertical = 5.dp),
) {
    val colors = LocalAppColors.current
    val foreground = if (enabled) colors.textTitle else colors.textSecondary.copy(alpha = 0.55f)

    Box(
        modifier = modifier
            .clip(RoundedCornerShape(11.dp))
            .background(colors.cardActionBackground.copy(alpha = if (enabled) 1f else 0.35f))
            .clickable(enabled = enabled, onClick = onClick)
            .padding(padding),
        contentAlignment = Alignment.Center,
    ) {
        Text(text = text, fontSize = fontSize, color = foreground)
    }
}

/** 日历的上一页 / 下一页。用几何字符 ‹ ›，与桌面版一致。 */
@Composable
fun PeriodNavButton(text: String, onClick: () -> Unit) {
    val colors = LocalAppColors.current

    Box(
        modifier = Modifier
            .size(32.dp)
            .clip(RoundedCornerShape(6.dp))
            .background(colors.cardActionBackground)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(text = text, fontSize = 18.sp, color = colors.textTitle)
    }
}

@Composable
fun SettingHeader(
    icon: String,
    title: String,
    subtitle: String?,
    modifier: Modifier = Modifier,
    iconSize: TextUnit = 18.sp,
) {
    val colors = LocalAppColors.current

    Row(modifier = modifier, verticalAlignment = Alignment.CenterVertically) {
        IconText(icon, iconSize, colors.textSecondary, Modifier.width(20.dp))
        Spacer(Modifier.width(8.dp))
        Column(modifier = Modifier.weight(1f)) {
            Text(text = title, fontSize = 13.sp, color = colors.textTitle)
            if (!subtitle.isNullOrBlank()) {
                Text(
                    text = subtitle,
                    fontSize = 10.sp,
                    color = colors.textSecondary,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 2.dp),
                )
            }
        }
    }
}

@Composable
fun SettingsSwitch(
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = LocalAppColors.current

    Switch(
        checked = checked,
        onCheckedChange = onCheckedChange,
        modifier = modifier,
        colors = SwitchDefaults.colors(
            checkedThumbColor = Color.White,
            checkedTrackColor = colors.addNewBillBtn,
            checkedBorderColor = colors.addNewBillBtn,
            uncheckedThumbColor = colors.textSecondary,
            uncheckedTrackColor = colors.cardActionBackground,
            uncheckedBorderColor = colors.cardBorder,
        ),
    )
}

@Composable
fun NoticeText(text: String, modifier: Modifier = Modifier) {
    if (text.isEmpty()) return

    Text(
        text = text,
        fontSize = 11.sp,
        color = LocalAppColors.current.textSecondary,
        modifier = modifier.padding(horizontal = 2.dp, vertical = 4.dp),
    )
}

@Composable
fun EmptyHint(text: String, modifier: Modifier = Modifier) {
    Box(modifier = modifier.fillMaxWidth().padding(vertical = 24.dp), contentAlignment = Alignment.Center) {
        Text(text = text, fontSize = 12.sp, color = LocalAppColors.current.textSecondary)
    }
}

@Composable
fun AddFab(onClick: () -> Unit, modifier: Modifier = Modifier) {
    val colors = LocalAppColors.current
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (pressed) 0.9f else 1f,
        animationSpec = spring(dampingRatio = 0.45f, stiffness = 700f),
        label = "fabScale",
    )

    Box(
        modifier = modifier
            .size(52.dp)
            .graphicsLayer {
                scaleX = scale
                scaleY = scale
            }
            .shadow(6.dp, CircleShape, ambientColor = colors.shadow, spotColor = colors.shadow)
            .clip(CircleShape)
            .background(if (pressed) colors.addNewBillBtnPressed else colors.addNewBillBtn)
            .clickable(interactionSource = interaction, indication = null, onClick = onClick)
            .testTag("add-bill"),
        contentAlignment = Alignment.Center,
    ) {
        IconText(AppIcons.ADD, 24.sp, Color.White)
    }
}

/**
 * 列表卡片入场：每张上浮 14dp 并淡入，按显示顺序错开，最多错 8 档。
 * 与桌面版 CardListEntrance 的 360ms / 70ms / 14px 一组参数对齐。
 */
@Composable
fun Modifier.cardEntrance(index: Int): Modifier {
    var shown by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        delay(min(index, 8) * 70L)
        shown = true
    }

    val progress by animateFloatAsState(
        targetValue = if (shown) 1f else 0f,
        animationSpec = tween(durationMillis = 360, easing = FastOutSlowInEasing),
        label = "cardEntrance",
    )

    return graphicsLayer {
        alpha = progress
        translationY = (1f - progress) * 14.dp.toPx()
    }
}

/** 液态玻璃的背板：应用内渐变 + 柔和光斑，再盖一层玻璃明度层。 */
@Composable
fun AppBackdrop(style: String, content: @Composable BoxScope.() -> Unit) {
    val colors = LocalAppColors.current

    if (style == ThemeState.STYLE_LIQUID) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(
                    Brush.verticalGradient(
                        listOf(colors.titleTop, colors.sideBar, colors.mainContentArea),
                    ),
                ),
        ) {
            Canvas(modifier = Modifier.fillMaxSize()) {
                drawCircle(
                    color = colors.categoryColors[0].copy(alpha = 0.22f),
                    radius = size.minDimension * 0.8f,
                    center = Offset(size.width * 0.1f, size.height * 0.12f),
                )
                drawCircle(
                    color = colors.categoryColors[1].copy(alpha = 0.18f),
                    radius = size.minDimension * 0.7f,
                    center = Offset(size.width * 0.95f, size.height * 0.38f),
                )
                drawCircle(
                    color = colors.categoryColors[3].copy(alpha = 0.16f),
                    radius = size.minDimension * 0.9f,
                    center = Offset(size.width * 0.3f, size.height * 0.98f),
                )
                drawRect(color = colors.glassScrim)
            }

            content()
        }
    } else {
        Box(modifier = Modifier.fillMaxSize().background(colors.mainContentArea), content = content)
    }
}
