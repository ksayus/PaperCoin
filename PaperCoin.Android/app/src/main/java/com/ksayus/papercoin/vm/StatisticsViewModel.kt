package com.ksayus.papercoin.vm

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.ksayus.papercoin.data.BillEntry
import com.ksayus.papercoin.data.BillRepository
import com.ksayus.papercoin.data.CategoryRankRow
import com.ksayus.papercoin.data.CsvExporter
import com.ksayus.papercoin.data.StatisticsEngine
import com.ksayus.papercoin.data.StatisticsRange
import com.ksayus.papercoin.data.StatisticsSnapshot
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.launch
import java.time.LocalDate
import java.time.LocalDateTime

data class CsvExport(val fileName: String, val content: String)

/**
 * 统计页面。所有派生数据（圆环分段、分类排行、月度趋势、下钻明细）由 StatisticsEngine 算好，
 * 这里只负责范围切换、选中分类与刷新时机。
 */
class StatisticsViewModel(private val repository: BillRepository) : ViewModel() {
    var range by mutableStateOf(StatisticsRange.MONTH)
        private set

    var snapshot by mutableStateOf(
        StatisticsEngine.compute(emptyList(), StatisticsRange.MONTH, LocalDate.now()),
    )
        private set

    var isRankExpanded by mutableStateOf(false)
        private set

    var notice by mutableStateOf("")

    var detailEntries by mutableStateOf<List<BillEntry>>(emptyList())
        private set

    var detailSummary by mutableStateOf("")
        private set

    private var _selectedCategory by mutableStateOf<String?>(null)

    var selectedCategory: String?
        get() = _selectedCategory
        set(value) {
            if (_selectedCategory == value) return
            _selectedCategory = value
            refreshDetail()
        }

    private var loadedRevision = -1
    private var reloadJob: Job? = null

    val categories: List<String> get() = snapshot.categories
    val hasCategorySelection: Boolean get() = !selectedCategory.isNullOrEmpty()
    val detailTitle: String get() = selectedCategory ?: "分类明细"

    val visibleRankRows: List<CategoryRankRow>
        get() = if (isRankExpanded) snapshot.rankRows else snapshot.rankRows.take(6)

    val hasMoreRanks: Boolean get() = snapshot.rankRows.size > 6

    init {
        reload(force = true)

        viewModelScope.launch {
            repository.revision.drop(1).collect { revision ->
                if (revision != loadedRevision) reload(force = false)
            }
        }
    }

    /** 每次切回统计页调用；索引版本没变就不重算。 */
    fun ensureFresh() {
        if (loadedRevision == repository.currentRevision) return
        reload(force = false)
    }

    fun showRange(value: StatisticsRange) {
        if (range == value) return

        range = value
        isRankExpanded = false
        reload(force = true)
    }

    fun refresh() = reload(force = true)

    fun selectCategory(category: String) {
        selectedCategory = category
    }

    fun clearCategory() {
        selectedCategory = null
    }

    fun showAllRanks() {
        isRankExpanded = true
    }

    fun showTopRanks() {
        isRankExpanded = false
    }

    fun exportCsv(): CsvExport = CsvExport(
        fileName = CsvExporter.fileName(snapshot.rangeText, LocalDateTime.now()),
        content = CsvExporter.build(
            entries = snapshot.rangeEntries,
            totalExpense = snapshot.totalExpense,
            totalIncome = snapshot.totalIncome,
            count = snapshot.entryCount,
        ),
    )

    private fun reload(force: Boolean) {
        if (!force && loadedRevision == repository.currentRevision) return

        reloadJob?.cancel()

        reloadJob = viewModelScope.launch {
            val all = repository.loadAll()
            val next = StatisticsEngine.compute(all, range, LocalDate.now())

            snapshot = next
            loadedRevision = repository.currentRevision

            val selected = selectedCategory
            if (selected != null && next.categories.none { it.equals(selected, ignoreCase = true) }) {
                selectedCategory = null
            } else {
                refreshDetail()
            }
        }
    }

    private fun refreshDetail() {
        val category = selectedCategory
        if (category.isNullOrEmpty()) {
            detailEntries = emptyList()
            detailSummary = ""
            return
        }

        val matching = StatisticsEngine.categoryEntries(snapshot.allEntries, category)
        detailEntries = matching
        detailSummary = StatisticsEngine.categorySummary(snapshot.allEntries, matching)
    }
}
