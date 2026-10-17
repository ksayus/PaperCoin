package com.ksayus.papercoin.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.ksayus.papercoin.core.CategoryPalette
import com.ksayus.papercoin.data.AppContainer
import com.ksayus.papercoin.data.BillEntry
import com.ksayus.papercoin.sync.ConflictChoice
import com.ksayus.papercoin.sync.ConflictKind
import com.ksayus.papercoin.sync.SyncConflict
import com.ksayus.papercoin.ui.components.AppCard
import com.ksayus.papercoin.ui.components.PillButton
import com.ksayus.papercoin.ui.theme.LocalAppColors
import com.ksayus.papercoin.ui.theme.SemiBold

/**
 * 冲突处理：每条冲突都必须选"保留本机"或"保留对方"才能应用；
 * 取消则整次同步不落盘。对话框由 App 根节点挂载，自动同步在任意页面发现冲突都能弹出来。
 */
@Composable
fun SyncConflictDialog(container: AppContainer) {
    val ui by container.sync.ui.collectAsState()

    if (ui.pendingConflicts.isEmpty()) return

    SyncConflictDialogContent(
        conflicts = ui.pendingConflicts,
        peerName = ui.conflictPeerName,
        status = ui.status,
        busy = ui.busy,
        onResolve = { choices -> container.sync.resolveConflicts(choices) },
        onCancel = { container.sync.cancelConflicts() },
    )
}

@Composable
fun SyncConflictDialogContent(
    conflicts: List<SyncConflict>,
    peerName: String,
    status: String,
    busy: Boolean,
    onResolve: (Map<String, ConflictChoice>) -> Unit,
    onCancel: () -> Unit,
) {
    if (conflicts.isEmpty()) return

    val colors = LocalAppColors.current
    val choices = remember { mutableStateMapOf<String, ConflictChoice>() }
    val allChosen = conflicts.all { choices.containsKey(it.id) }

    Dialog(
        onDismissRequest = onCancel,
        properties = DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Surface(color = colors.mainContentArea, modifier = Modifier.fillMaxSize()) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .statusBarsPadding()
                    .navigationBarsPadding(),
            ) {
                Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp)) {
                    Text(
                        text = "同步冲突（${conflicts.size} 处）",
                        fontSize = 16.sp,
                        fontWeight = com.ksayus.papercoin.ui.theme.Bold,
                        color = colors.textTitle,
                    )
                    Text(
                        text = "与 $peerName 的同一笔流水在两边的版本不一致，请选择保留哪个。",
                        fontSize = 11.sp,
                        color = colors.textSecondary,
                        modifier = Modifier.padding(top = 4.dp),
                    )
                }

                LazyColumn(
                    modifier = Modifier.weight(1f),
                    contentPadding = androidx.compose.foundation.layout.PaddingValues(
                        start = 16.dp,
                        end = 16.dp,
                        bottom = 12.dp,
                    ),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    items(conflicts, key = { it.id }) { conflict ->
                        ConflictCard(
                            conflict = conflict,
                            selected = choices[conflict.id],
                            onSelect = { choice -> choices[conflict.id] = choice },
                        )
                    }
                }

                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .background(colors.mainContentArea)
                        .padding(horizontal = 16.dp, vertical = 10.dp),
                ) {
                    Text(
                        text = status,
                        fontSize = 10.sp,
                        color = colors.textSecondary,
                    )

                    Row(
                        modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
                        horizontalArrangement = Arrangement.End,
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        PillButton(
                            text = "取消同步",
                            onClick = onCancel,
                            enabled = !busy,
                        )
                        Spacer(Modifier.width(8.dp))
                        PillButton(
                            text = if (busy) "正在应用…" else "应用同步",
                            onClick = { onResolve(choices.toMap()) },
                            enabled = allChosen && !busy,
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun ConflictCard(
    conflict: SyncConflict,
    selected: ConflictChoice?,
    onSelect: (ConflictChoice) -> Unit,
) {
    val colors = LocalAppColors.current

    val title = conflict.local?.description
        ?: conflict.remote?.description
        ?: "已删除的流水"

    val kind = when (conflict.kind) {
        ConflictKind.BOTH_CHANGED -> "两边都修改了"
        ConflictKind.LOCAL_DELETED_REMOTE_CHANGED -> "本机删除，对方修改"
        ConflictKind.LOCAL_CHANGED_REMOTE_DELETED -> "对方删除，本机修改"
    }

    AppCard(modifier = Modifier.fillMaxWidth()) {
        Text(
            text = title,
            fontSize = 13.sp,
            fontWeight = SemiBold,
            color = colors.textTitle,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Text(
            text = kind,
            fontSize = 10.sp,
            color = colors.textSecondary,
            modifier = Modifier.padding(top = 2.dp),
        )

        Row(modifier = Modifier.padding(top = 8.dp)) {
            ConflictSide(
                label = "本机",
                entry = conflict.local,
                selected = selected == ConflictChoice.LOCAL,
                modifier = Modifier.weight(1f),
                onClick = { onSelect(ConflictChoice.LOCAL) },
            )
            Spacer(Modifier.width(8.dp))
            ConflictSide(
                label = "对方",
                entry = conflict.remote,
                selected = selected == ConflictChoice.REMOTE,
                modifier = Modifier.weight(1f),
                onClick = { onSelect(ConflictChoice.REMOTE) },
            )
        }
    }
}

@Composable
private fun ConflictSide(
    label: String,
    entry: BillEntry?,
    selected: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    val colors = LocalAppColors.current
    val shape = RoundedCornerShape(6.dp)

    Column(
        modifier = modifier
            .clip(shape)
            .background(colors.cardBackground)
            .border(
                width = if (selected) 2.dp else 1.dp,
                color = if (selected) colors.addNewBillBtn else colors.cardBorder,
                shape = shape,
            )
            .clickable(onClick = onClick)
            .padding(8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                text = label,
                fontSize = 10.sp,
                color = colors.textSecondary,
                modifier = Modifier.weight(1f),
            )
            if (selected) {
                Text(
                    text = "已选",
                    fontSize = 9.sp,
                    color = colors.addNewBillBtn,
                )
            }
        }

        if (entry == null) {
            Text(
                text = "已删除",
                fontSize = 12.sp,
                color = colors.negative,
                modifier = Modifier.padding(top = 6.dp),
            )
            Spacer(Modifier.height(24.dp))
        } else {
            Text(
                text = entry.description,
                fontSize = 12.sp,
                fontWeight = SemiBold,
                color = colors.textTitle,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(top = 5.dp),
            )
            Text(
                text = entry.amountText,
                fontSize = 14.sp,
                fontWeight = SemiBold,
                color = colors.textTitle,
                modifier = Modifier.padding(top = 3.dp),
            )
            Text(
                text = "${CategoryPalette.normalize(entry.category)} · ${entry.date}",
                fontSize = 9.sp,
                color = colors.textSecondary,
                modifier = Modifier.padding(top = 2.dp),
            )
        }

        Text(
            text = if (selected) "已选择" else "点击保留",
            fontSize = 9.sp,
            color = if (selected) colors.addNewBillBtn else colors.textSecondary,
            modifier = Modifier.padding(top = 4.dp),
        )

        Box(modifier = Modifier.height(1.dp))
    }
}
