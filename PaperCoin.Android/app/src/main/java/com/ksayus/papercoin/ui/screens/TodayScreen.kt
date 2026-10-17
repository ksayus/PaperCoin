package com.ksayus.papercoin.ui.screens

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.ksayus.papercoin.data.AppContainer
import com.ksayus.papercoin.ui.components.AddFab
import com.ksayus.papercoin.ui.components.AppCard
import com.ksayus.papercoin.ui.components.BillCard
import com.ksayus.papercoin.ui.components.BillEditorSheet
import com.ksayus.papercoin.ui.components.EmptyHint
import com.ksayus.papercoin.ui.components.NoticeText
import com.ksayus.papercoin.ui.theme.Bold
import com.ksayus.papercoin.ui.theme.LocalAppColors
import com.ksayus.papercoin.vm.TodayViewModel

@Composable
fun TodayScreen(container: AppContainer) {
    val vm: TodayViewModel = viewModel(factory = todayFactory(container))
    val colors = LocalAppColors.current

    LaunchedEffect(Unit) {
        vm.refresh()
    }

    Box(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 16.dp, vertical = 12.dp),
        ) {
            AppCard(modifier = Modifier.fillMaxWidth()) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(text = "今日合计", fontSize = 11.sp, color = colors.textSecondary)
                        Text(
                            text = vm.totalText,
                            fontSize = 19.sp,
                            fontWeight = Bold,
                            color = if (vm.isPositive) colors.positive else colors.negative,
                            maxLines = 1,
                        )
                    }
                    Text(text = vm.countText, fontSize = 11.sp, color = colors.textSecondary)
                }
            }

            Spacer(modifier = Modifier.height(10.dp))

            if (vm.entries.isEmpty()) {
                Box(
                    modifier = Modifier.weight(1f).fillMaxWidth(),
                    contentAlignment = Alignment.Center,
                ) {
                    EmptyHint("还没有流水，点右下角 ＋ 记一笔")
                }
            } else {
                LazyColumn(
                    modifier = Modifier.weight(1f),
                    contentPadding = PaddingValues(top = 2.dp, bottom = 88.dp),
                ) {
                    itemsIndexed(vm.entries, key = { _, entry -> entry.id }) { index, entry ->
                        Box(modifier = Modifier.padding(bottom = 8.dp)) {
                            BillCard(
                                entry = entry,
                                index = index,
                                onEdit = { vm.edit(entry) },
                                onDelete = { vm.delete(entry) },
                            )
                        }
                    }
                }
            }

            NoticeText(vm.notice)
        }

        AddFab(
            onClick = vm::add,
            modifier = Modifier.align(Alignment.BottomEnd).padding(16.dp),
        )

        if (vm.editor.isOpen) {
            BillEditorSheet(vm.editor)
        }
    }
}

private fun todayFactory(container: AppContainer) = viewModelFactory {
    initializer { TodayViewModel(container.repository) }
}
