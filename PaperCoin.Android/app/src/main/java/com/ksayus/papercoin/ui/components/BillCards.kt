package com.ksayus.papercoin.ui.components

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.ksayus.papercoin.core.CategoryPalette
import com.ksayus.papercoin.data.BillEntry
import com.ksayus.papercoin.ui.theme.LocalAppColors
import com.ksayus.papercoin.ui.theme.SemiBold

/** 今日页的流水卡片：分类色条 + 描述 + 分类/日期 + 金额 + 删除。 */
@Composable
fun BillCard(
    entry: BillEntry,
    index: Int,
    onEdit: () -> Unit,
    onDelete: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = LocalAppColors.current
    val shape = RoundedCornerShape(8.dp)
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (pressed) 0.98f else 1f,
        animationSpec = tween(durationMillis = 110),
        label = "cardScale",
    )

    Row(
        modifier = modifier
            .fillMaxWidth()
            .cardEntrance(index)
            .graphicsLayer {
                scaleX = scale
                scaleY = scale
            }
            .shadow(2.dp, shape, ambientColor = colors.shadow, spotColor = colors.shadow)
            .clip(shape)
            .background(colors.cardBackground)
            .border(1.dp, colors.cardBorder, shape)
            .clickable(interactionSource = interaction, indication = null, onClick = onEdit)
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .width(3.dp)
                        .height(12.dp)
                        .clip(RoundedCornerShape(1.5.dp))
                        .background(colors.categoryColor(entry.categoryColorKey)),
                )
                Spacer(Modifier.width(6.dp))
                Text(
                    text = entry.description,
                    fontSize = 13.sp,
                    fontWeight = SemiBold,
                    color = colors.textTitle,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }

            Row(modifier = Modifier.padding(start = 9.dp, top = 3.dp)) {
                if (!entry.category.isNullOrBlank()) {
                    Text(
                        text = CategoryPalette.normalize(entry.category),
                        fontSize = 11.sp,
                        color = colors.textSecondary,
                    )
                    Spacer(Modifier.width(8.dp))
                }
                Text(text = entry.dateText, fontSize = 11.sp, color = colors.textSecondary)
            }
        }

        Text(
            text = entry.amountText,
            fontSize = 14.sp,
            fontWeight = SemiBold,
            color = colors.textTitle,
            modifier = Modifier.padding(start = 10.dp),
        )

        Box(
            modifier = Modifier
                .padding(start = 4.dp)
                .size(40.dp)
                .clip(RoundedCornerShape(8.dp))
                .clickable(onClick = onDelete),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                imageVector = Icons.Filled.Delete,
                contentDescription = "删除该流水",
                tint = colors.textSecondary.copy(alpha = 0.55f),
                modifier = Modifier.size(17.dp),
            )
        }
    }
}

/** 日历明细卡片：更紧凑，金额与日期分两行，右下角带删除。 */
@Composable
fun CalendarBillCard(
    entry: BillEntry,
    index: Int,
    onEdit: () -> Unit,
    onDelete: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = LocalAppColors.current
    val shape = RoundedCornerShape(6.dp)

    Row(
        modifier = modifier
            .fillMaxWidth()
            .cardEntrance(index)
            .clip(shape)
            .background(colors.cardBackground)
            .border(1.dp, colors.cardBorder, shape)
            .clickable(onClick = onEdit)
            .padding(horizontal = 10.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .width(3.dp)
                        .height(12.dp)
                        .clip(RoundedCornerShape(1.5.dp))
                        .background(colors.categoryColor(entry.categoryColorKey)),
                )
                Spacer(Modifier.width(6.dp))
                Text(
                    text = entry.description,
                    fontSize = 12.sp,
                    fontWeight = SemiBold,
                    color = colors.textTitle,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            Text(
                text = CategoryPalette.normalize(entry.category),
                fontSize = 10.sp,
                color = colors.textSecondary,
                modifier = Modifier.padding(start = 9.dp, top = 2.dp),
            )
        }

        Column(
            horizontalAlignment = Alignment.End,
            verticalArrangement = Arrangement.Center,
            modifier = Modifier.padding(start = 8.dp),
        ) {
            Text(text = entry.amountText, fontSize = 12.sp, fontWeight = SemiBold, color = colors.textTitle)
            Text(text = entry.dateText, fontSize = 10.sp, color = colors.textSecondary)
        }

        Box(
            modifier = Modifier
                .padding(start = 4.dp)
                .size(36.dp)
                .clip(RoundedCornerShape(8.dp))
                .clickable(onClick = onDelete),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                imageVector = Icons.Filled.Delete,
                contentDescription = "删除该流水",
                tint = colors.textSecondary.copy(alpha = 0.55f),
                modifier = Modifier.size(16.dp),
            )
        }
    }
}
