package com.ksayus.papercoin.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.ksayus.papercoin.data.AppContainer
import com.ksayus.papercoin.ui.components.AddFab
import com.ksayus.papercoin.ui.components.AppCard
import com.ksayus.papercoin.ui.components.BillEditorSheet
import com.ksayus.papercoin.ui.components.CalendarBillCard
import com.ksayus.papercoin.ui.components.EmptyHint
import com.ksayus.papercoin.ui.components.NoticeText
import com.ksayus.papercoin.ui.components.PeriodNavButton
import com.ksayus.papercoin.ui.components.PillButton
import com.ksayus.papercoin.ui.components.SegmentedControl
import com.ksayus.papercoin.ui.theme.Bold
import com.ksayus.papercoin.ui.theme.LocalAppColors
import com.ksayus.papercoin.ui.theme.SemiBold
import com.ksayus.papercoin.vm.CalendarCellUi
import com.ksayus.papercoin.vm.CalendarViewMode
import com.ksayus.papercoin.vm.CalendarViewModel
import com.ksayus.papercoin.vm.MonthSummaryUi

@Composable
fun CalendarScreen(container: AppContainer) {
    val vm: CalendarViewModel = viewModel(factory = calendarFactory(container))
    val colors = LocalAppColors.current

    LaunchedEffect(Unit) {
        vm.ensureFresh()
    }

    Box(modifier = Modifier.fillMaxSize()) {
        Column(modifier = Modifier.fillMaxSize()) {
            Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    PeriodNavButton("‹") { vm.previous() }
                    Spacer(Modifier.width(4.dp))
                    PeriodNavButton("›") { vm.next() }
                    Spacer(Modifier.width(8.dp))
                    Text(
                        text = vm.periodTitle,
                        fontSize = 14.sp,
                        fontWeight = Bold,
                        color = colors.textTitle,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f),
                    )
                    PillButton("今天", onClick = vm::goToday)
                }

                Row(
                    modifier = Modifier.padding(top = 10.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    SegmentedControl(
                        options = listOf(
                            CalendarViewMode.DAY.name to "日",
                            CalendarViewMode.MONTH.name to "月",
                            CalendarViewMode.YEAR.name to "年",
                        ),
                        selected = vm.mode.name,
                        onSelect = { vm.showMode(CalendarViewMode.valueOf(it)) },
                    )
                    Spacer(Modifier.width(12.dp))
                    Text(
                        text = vm.totalText,
                        fontSize = 13.sp,
                        fontWeight = Bold,
                        color = if (vm.isPositive) colors.positive else colors.negative,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        textAlign = TextAlign.End,
                        modifier = Modifier.weight(1f),
                    )
                }
            }

            LazyColumn(
                modifier = Modifier.weight(1f),
                contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 4.dp, bottom = 88.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                when (vm.mode) {
                    CalendarViewMode.MONTH -> item {
                        Column {
                            WeekdayHeader()
                            Spacer(Modifier.height(4.dp))
                            MonthGrid(vm.cells) { vm.selectDay(it) }
                        }
                    }

                    CalendarViewMode.YEAR -> item {
                        YearGrid(vm.months) { vm.selectMonth(it) }
                    }

                    CalendarViewMode.DAY -> Unit
                }

                item {
                    if (vm.entries.isEmpty()) {
                        EmptyHint("这段时间没有记录")
                    } else {
                        Text(
                            text = vm.countText,
                            fontSize = 10.sp,
                            color = colors.textSecondary,
                            modifier = Modifier.padding(start = 2.dp, bottom = 2.dp),
                        )
                    }
                }

                itemsIndexed(vm.entries, key = { _, entry -> entry.id }) { index, entry ->
                    CalendarBillCard(
                        entry = entry,
                        index = index,
                        onEdit = { vm.edit(entry) },
                        onDelete = { vm.delete(entry) },
                    )
                }
            }

            NoticeText(vm.notice, Modifier.padding(horizontal = 16.dp))
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

@Composable
private fun WeekdayHeader() {
    val colors = LocalAppColors.current
    val labels = listOf("日", "一", "二", "三", "四", "五", "六")

    Row(modifier = Modifier.fillMaxWidth()) {
        labels.forEach { label ->
            Text(
                text = label,
                fontSize = 10.sp,
                color = colors.textSecondary,
                textAlign = TextAlign.Center,
                modifier = Modifier.weight(1f),
            )
        }
    }
}

@Composable
private fun MonthGrid(cells: List<CalendarCellUi>, onSelect: (CalendarCellUi) -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        cells.chunked(7).forEach { week ->
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                for (index in 0 until 7) {
                    val cell = week.getOrNull(index)
                    if (cell == null) {
                        Spacer(Modifier.weight(1f))
                    } else {
                        CalendarCellView(cell, Modifier.weight(1f), onSelect)
                    }
                }
            }
        }
    }
}

@Composable
private fun CalendarCellView(
    cell: CalendarCellUi,
    modifier: Modifier,
    onSelect: (CalendarCellUi) -> Unit,
) {
    val colors = LocalAppColors.current
    val shape = RoundedCornerShape(6.dp)
    val background = if (cell.hasItems) colors.cardActionBackground else colors.cardBackground
    val dayColor = colors.textTitle

    Box(
        modifier = modifier
            .height(44.dp)
            .clip(shape)
            .background(background)
            .then(
                if (cell.isSelected) Modifier.border(1.dp, colors.addNewBillBtn, shape) else Modifier,
            )
            .clickable { onSelect(cell) },
        contentAlignment = Alignment.Center,
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(
                text = cell.day.toString(),
                fontSize = 11.sp,
                color = dayColor.copy(alpha = if (cell.isCurrentMonth) 1f else 0.3f),
                maxLines = 1,
            )
            Text(
                text = cell.totalText,
                fontSize = 8.sp,
                color = colors.textSecondary.copy(alpha = if (cell.isCurrentMonth) 0.9f else 0.2f),
                maxLines = 1,
                softWrap = false,
                overflow = TextOverflow.Clip,
            )
        }
    }
}

@Composable
private fun YearGrid(months: List<MonthSummaryUi>, onSelect: (Int) -> Unit) {
    val colors = LocalAppColors.current

    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        months.chunked(3).forEach { row ->
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                row.forEach { month ->
                    AppCard(
                        modifier = Modifier
                            .weight(1f)
                            .height(64.dp)
                            .clickable { onSelect(month.month) },
                        padding = PaddingValues(horizontal = 8.dp, vertical = 6.dp),
                    ) {
                        Text(text = month.title, fontSize = 11.sp, color = colors.textSecondary)
                        Text(
                            text = month.totalText,
                            fontSize = 13.sp,
                            fontWeight = SemiBold,
                            color = colors.textTitle,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }

                repeat(3 - row.size) {
                    Spacer(Modifier.weight(1f))
                }
            }
        }
    }
}

private fun calendarFactory(container: AppContainer) = viewModelFactory {
    initializer { CalendarViewModel(container.repository) }
}
