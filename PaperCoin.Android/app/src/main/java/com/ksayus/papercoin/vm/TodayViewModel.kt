package com.ksayus.papercoin.vm

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.ksayus.papercoin.core.Formats
import com.ksayus.papercoin.data.BillEntry
import com.ksayus.papercoin.data.BillRepository
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.launch
import java.math.BigDecimal
import java.time.LocalDate

/** 今日账单页面。列表只装今天的流水，新增 / 编辑交给共享的编辑器状态。 */
class TodayViewModel(private val repository: BillRepository) : ViewModel() {
    /** 本页固定的今天，启动时取一次，与桌面版一致 */
    val today: LocalDate = LocalDate.now()

    var entries by mutableStateOf<List<BillEntry>>(emptyList())
        private set

    var notice by mutableStateOf("")

    val editor = BillEditorState(repository, viewModelScope)

    val total: BigDecimal
        get() = entries.fold(BigDecimal.ZERO) { acc, entry -> acc + entry.amount }

    val totalText: String get() = Formats.signedAmount(total)
    val isPositive: Boolean get() = total.signum() >= 0
    val countText: String get() = "共 ${entries.size} 笔"
    val isEmpty: Boolean get() = entries.isEmpty()

    init {
        editor.saved = { entry ->
            refresh()
            notice = if (entry.date == today) "" else "已记到 ${Formats.dateIso(entry.date)}，本页只显示今天"
        }
        editor.saveFailed = { message -> notice = message }

        refresh()

        // 局域网同步可能在后台改了磁盘；当前页打开着也要跟着刷新。
        viewModelScope.launch {
            repository.revision.drop(1).collect { refresh() }
        }
    }

    fun refresh() {
        viewModelScope.launch {
            entries = repository.loadDay(today)
        }
    }

    fun add() {
        notice = ""
        editor.openForCreate(today)
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
}
