package com.ksayus.papercoin.vm

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.ksayus.papercoin.core.Formats
import com.ksayus.papercoin.data.BillEntry
import com.ksayus.papercoin.data.BillRepository
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.launch
import java.math.BigDecimal
import java.time.LocalDate

enum class CalendarViewMode { DAY, MONTH, YEAR }

/** 日历上的一格 */
data class CalendarCellUi(
    val date: LocalDate,
    val isCurrentMonth: Boolean,
    val isToday: Boolean,
    val isSelected: Boolean,
    val total: BigDecimal,
    val count: Int,
) {
    val day: Int get() = date.dayOfMonth
    val hasItems: Boolean get() = count > 0

    val totalText: String
        get() = if (count == 0) "" else {
            val sign = if (total.signum() >= 0) "+" else "-"
            sign + Formats.compactAmount(total.abs())
        }
}

/** 年视图里的一个月 */
data class MonthSummaryUi(
    val month: Int,
    val total: BigDecimal,
    val count: Int,
) {
    val title: String get() = "$month 月"
    val hasItems: Boolean get() = count > 0

    val totalText: String
        get() = if (count == 0) "—" else Formats.signedAmount(total)
}

/**
 * 日历账单页面。日 / 月 / 年三种粒度共用同一个当前定位，切换粒度只改变聚合方式；
 * 上一页 / 下一页的步长由当前粒度决定。
 */
class CalendarViewModel(private val repository: BillRepository) : ViewModel() {
    var anchor by mutableStateOf(LocalDate.now())
        private set

    var mode by mutableStateOf(CalendarViewMode.DAY)
        private set

    var selectedDate by mutableStateOf(LocalDate.now())
        private set

    var cells by mutableStateOf<List<CalendarCellUi>>(emptyList())
        private set

    var months by mutableStateOf<List<MonthSummaryUi>>(emptyList())
        private set

    var entries by mutableStateOf<List<BillEntry>>(emptyList())
        private set

    var total by mutableStateOf(BigDecimal.ZERO)
        private set

    var notice by mutableStateOf("")

    val editor = BillEditorState(repository, viewModelScope)

    private var refreshJob: Job? = null

    val periodTitle: String
        get() = when (mode) {
            CalendarViewMode.DAY -> Formats.dateLong(anchor)
            CalendarViewMode.MONTH -> Formats.monthLong(anchor)
            CalendarViewMode.YEAR -> Formats.yearLong(anchor)
        }

    val totalText: String get() = Formats.signedAmount(total)
    val isPositive: Boolean get() = total.signum() >= 0
    val countText: String get() = "共 ${entries.size} 笔"
    val isEmpty: Boolean get() = entries.isEmpty()
    val selectedDateText: String get() = Formats.monthDay(selectedDate)

    /** 新增时的默认日期：三种粒度下都有唯一答案 */
    val draftDate: LocalDate
        get() {
            val today = LocalDate.now()

            return when (mode) {
                CalendarViewMode.DAY -> anchor
                CalendarViewMode.MONTH ->
                    if (today.year == anchor.year && today.monthValue == anchor.monthValue) today
                    else anchor.withDayOfMonth(1)
                CalendarViewMode.YEAR ->
                    if (today.year == anchor.year) today
                    else LocalDate.of(anchor.year, 1, 1)
            }
        }

    init {
        editor.saved = { entry ->
            refresh()
            notice = if (entry.date == anchor) "" else "已记到 ${Formats.dateIso(entry.date)}"
        }
        editor.saveFailed = { message -> notice = message }

        refresh()

        viewModelScope.launch {
            repository.revision.drop(1).collect { refresh() }
        }
    }

    fun previous() = shift(-1)

    fun next() = shift(1)

    fun goToday() {
        anchor = LocalDate.now()
        selectedDate = anchor
        refresh()
    }

    fun showMode(value: CalendarViewMode) {
        if (mode == value) return
        mode = value
        refresh()
    }

    /** 点选月视图里的某一格，落到该日并自动切到日视图 */
    fun selectDay(cell: CalendarCellUi) {
        anchor = cell.date
        selectedDate = cell.date
        mode = CalendarViewMode.DAY
        refresh()
    }

    /** 年视图点某个月：切到月视图，锚点移到那个月 */
    fun selectMonth(month: Int) {
        anchor = LocalDate.of(anchor.year, month, 1)
        mode = CalendarViewMode.MONTH
        refresh()
    }

    fun add() {
        notice = ""
        editor.openForCreate(draftDate)
    }

    /** 页面重新进入时与磁盘对齐（在别的页记了一笔之后切回来） */
    fun ensureFresh() {
        refresh()
    }

    fun edit(entry: BillEntry) {
        notice = ""
        editor.openForEdit(entry)
    }

    fun delete(entry: BillEntry) {
        viewModelScope.launch {
            val ok = repository.deleteEntry(entry.date, entry.id)
            if (!ok) notice = "删除失败，请检查磁盘权限或空间"
            refresh()
        }
    }

    private fun shift(direction: Int) {
        anchor = when (mode) {
            CalendarViewMode.DAY -> anchor.plusDays(direction.toLong())
            CalendarViewMode.MONTH -> anchor.plusMonths(direction.toLong())
            CalendarViewMode.YEAR -> anchor.plusYears(direction.toLong())
        }

        if (mode == CalendarViewMode.DAY) selectedDate = anchor

        refresh()
    }

    private fun refresh() {
        refreshJob?.cancel()

        refreshJob = viewModelScope.launch {
            val today = LocalDate.now()

            when (mode) {
                CalendarViewMode.DAY -> {
                    entries = repository.loadDay(anchor)
                    cells = emptyList()
                    months = emptyList()
                }

                CalendarViewMode.MONTH -> {
                    val first = anchor.withDayOfMonth(1)
                    val monthEntries = repository.loadMonth(anchor.year, anchor.monthValue)
                    val byDay = monthEntries.groupBy { it.date.dayOfMonth }

                    // 网格起点回退到本周周日：DayOfWeek 值 1..7，周日取余后为 0
                    val offset = first.dayOfWeek.value % 7
                    val gridStart = first.minusDays(offset.toLong())

                    cells = (0 until 42).map { index ->
                        val date = gridStart.plusDays(index.toLong())
                        val inMonth = date.year == first.year && date.monthValue == first.monthValue
                        val dayEntries = if (inMonth) byDay[date.dayOfMonth].orEmpty() else emptyList()

                        CalendarCellUi(
                            date = date,
                            isCurrentMonth = inMonth,
                            isToday = date == today,
                            isSelected = date == selectedDate,
                            total = if (inMonth) dayEntries.fold(BigDecimal.ZERO) { acc, e -> acc + e.amount }
                            else BigDecimal.ZERO,
                            count = if (inMonth) dayEntries.size else 0,
                        )
                    }

                    months = emptyList()
                    entries = monthEntries
                }

                CalendarViewMode.YEAR -> {
                    val yearEntries = repository.loadYear(anchor.year)
                    val byMonth = yearEntries.groupBy { it.date.monthValue }

                    months = (1..12).map { month ->
                        val monthEntries = byMonth[month].orEmpty()
                        MonthSummaryUi(
                            month = month,
                            total = monthEntries.fold(BigDecimal.ZERO) { acc, e -> acc + e.amount },
                            count = monthEntries.size,
                        )
                    }

                    cells = emptyList()
                    entries = yearEntries
                }
            }

            total = entries.fold(BigDecimal.ZERO) { acc, entry -> acc + entry.amount }
        }
    }
}
