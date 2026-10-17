package com.ksayus.papercoin.ui.components

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
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
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.ksayus.papercoin.data.CategoryRankRow
import com.ksayus.papercoin.data.DonutSegment
import com.ksayus.papercoin.data.MonthBar
import com.ksayus.papercoin.ui.theme.LocalAppColors
import com.ksayus.papercoin.ui.theme.SemiBold
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.PI
import kotlin.math.atan2
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.min

/**
 * 分类占比圆环。几何与桌面版 DonutChart 一致：
 * 从 12 点方向开始、段间留 0.022 弧度、单段不留缝；展开动画按段错开。
 */
@Composable
fun DonutChart(
    segments: List<DonutSegment>,
    modifier: Modifier = Modifier,
    ringThickness: Dp = 26.dp,
    onSegmentClick: ((DonutSegment) -> Unit)? = null,
) {
    val colors = LocalAppColors.current
    val animations = remember(segments) { segments.map { Animatable(0f) } }

    LaunchedEffect(segments) {
        coroutineScope {
            animations.forEachIndexed { index, animation ->
                launch {
                    delay(index * 55L)
                    animation.animateTo(
                        targetValue = 1f,
                        animationSpec = tween(durationMillis = 420, easing = FastOutSlowInEasing),
                    )
                }
            }
        }
    }

    Canvas(
        modifier = modifier.pointerInput(segments) {
            if (onSegmentClick == null || segments.isEmpty()) return@pointerInput

            detectTapGestures { offset ->
                val ring = ringThickness.toPx()
                val width = size.width.toFloat()
                val height = size.height.toFloat()
                val radius = (min(width, height) - ring) / 2f
                val center = Offset(width / 2f, height / 2f)

                val dx = offset.x - center.x
                val dy = offset.y - center.y
                val distance = hypot(dx, dy)

                if (distance < radius - ring / 2f || distance > radius + ring / 2f) return@detectTapGestures

                var angle = atan2(dy, dx) + PI / 2
                if (angle < 0) angle += PI * 2

                val total = segments.sumOf { it.fraction }.takeIf { it > 0 } ?: 1.0
                var cursor = 0.0

                for (segment in segments) {
                    val sweep = max(0.02, segment.fraction / total * PI * 2)
                    if (angle <= cursor + sweep) {
                        onSegmentClick(segment)
                        return@detectTapGestures
                    }
                    cursor += sweep
                }
            }
        },
    ) {
        if (segments.isEmpty()) return@Canvas

        val ring = ringThickness.toPx()
        val radius = (min(size.width, size.height) - ring) / 2f
        if (radius <= 0f) return@Canvas

        val center = Offset(size.width / 2f, size.height / 2f)
        val total = segments.sumOf { it.fraction }.takeIf { it > 0 } ?: 1.0
        val gap = if (segments.size == 1) 0.0 else 0.022

        var cursor = -PI / 2

        segments.forEachIndexed { index, segment ->
            val sweep = max(0.02, segment.fraction / total * PI * 2)
            val drawn = max(0.01, sweep - gap)
            val strokeWidth = ring * animations[index].value

            if (strokeWidth > 0.01f) {
                drawArc(
                    color = colors.categoryColor(segment.colorKey),
                    startAngle = (cursor * 180 / PI).toFloat(),
                    sweepAngle = (drawn * 180 / PI).toFloat(),
                    useCenter = false,
                    topLeft = Offset(center.x - radius, center.y - radius),
                    size = Size(radius * 2, radius * 2),
                    style = Stroke(width = strokeWidth, cap = StrokeCap.Butt),
                )
            }

            cursor += sweep
        }
    }
}

/** 分类排行的一行：轨道铺满、前景条按相对第一名的比例生长，数字压在条形之上。 */
@Composable
fun RankRow(row: CategoryRankRow, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val colors = LocalAppColors.current
    val barColor = colors.categoryColor(row.colorKey)
    var target by remember(row) { mutableStateOf(0f) }

    LaunchedEffect(row) {
        target = row.barFraction.toFloat().coerceIn(0f, 1f)
    }

    val fraction by animateFloatAsState(
        targetValue = target,
        animationSpec = tween(durationMillis = 460, easing = FastOutSlowInEasing),
        label = "rankBar",
    )

    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(52.dp)
            .clip(RoundedCornerShape(5.dp))
            .clickable(onClick = onClick),
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(barColor.copy(alpha = 0.16f)),
        )

        if (fraction > 0.001f) {
            Box(
                modifier = Modifier
                    .fillMaxHeight()
                    .fillMaxWidth(fraction)
                    .background(barColor),
            )
        }

        // 彩色条上直接压白字，浅色分类色会读不清；垫一层暗色薄纱把对比度钉住。
        Box(modifier = Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.20f)))

        Row(
            modifier = Modifier.fillMaxSize().padding(horizontal = 9.dp, vertical = 3.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = row.label,
                    fontSize = 12.sp,
                    fontWeight = SemiBold,
                    color = Color.White,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    text = row.countText,
                    fontSize = 10.sp,
                    color = Color.White.copy(alpha = 0.82f),
                )
            }

            Column(horizontalAlignment = Alignment.End) {
                Text(
                    text = row.amountText,
                    fontSize = 12.sp,
                    fontWeight = SemiBold,
                    color = Color.White,
                )
                Text(
                    text = row.shareText,
                    fontSize = 10.sp,
                    color = Color.White.copy(alpha = 0.82f),
                )
            }
        }
    }
}

/** 近 12 个月支出柱状图。柱高是相对最高月的比例，不是绝对金额。 */
@Composable
fun TrendBars(bars: List<MonthBar>, modifier: Modifier = Modifier) {
    val colors = LocalAppColors.current
    var started by remember(bars) { mutableStateOf(false) }

    LaunchedEffect(bars) {
        started = true
    }

    Row(
        modifier = modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.Bottom,
    ) {
        bars.forEach { bar ->
            val target = if (started) bar.heightFraction.toFloat().coerceIn(0f, 1f) else 0f
            val height by animateFloatAsState(
                targetValue = target,
                animationSpec = tween(durationMillis = 460, easing = FastOutSlowInEasing),
                label = "trendBar",
            )

            Column(
                modifier = Modifier.weight(1f),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Bottom,
            ) {
                Text(
                    text = bar.amountText.ifEmpty { " " },
                    fontSize = 8.sp,
                    color = colors.textSecondary,
                    maxLines = 1,
                )

                Box(
                    modifier = Modifier.height(100.dp).width(15.dp),
                    contentAlignment = Alignment.BottomCenter,
                ) {
                    if (height > 0.001f) {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .fillMaxHeight(height)
                                .clip(RoundedCornerShape(topStart = 3.dp, topEnd = 3.dp))
                                .background(colors.addNewBillBtn),
                        )
                    }
                }

                Text(
                    text = bar.label,
                    fontSize = 8.sp,
                    color = colors.textSecondary,
                    maxLines = 1,
                    modifier = Modifier.padding(top = 3.dp),
                )
            }
        }
    }
}
