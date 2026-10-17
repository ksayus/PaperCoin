package com.ksayus.papercoin.data

import com.ksayus.papercoin.core.CategoryPalette
import com.ksayus.papercoin.core.Formats
import java.math.BigDecimal
import java.math.RoundingMode
import java.time.LocalDate
import java.time.temporal.ChronoUnit
import java.util.Locale

/** 统计页的时间范围 */
enum class StatisticsRange { MONTH, YEAR, ALL }

/** 某段区间内按分类汇总的支出（金额为正数） */
data class CategoryTotal(
    val category: String,
    val expense: BigDecimal,
    val count: Int,
)

/**
 * 把 "范围里的全部支出" 按分类归并。分类名大小写不敏感，保留首次出现的写法；
 * 只统计支出（金额为负），金额取绝对值。与桌面版 `BillRepository.GetCategoryExpenseAsync` 同口径。
 */
fun computeCategoryTotals(entries: List<BillEntry>): List<CategoryTotal> {
    val groups = LinkedHashMap<String, Pair<String, MutableList<BillEntry>>>()

    for (entry in entries) {
        if (entry.amount.signum() >= 0) continue

        val name = CategoryPalette.normalize(entry.category)
        val key = name.lowercase(Locale.ROOT)

        val group = groups.getOrPut(key) { name to mutableListOf() }
        group.second.add(entry)
    }

    return groups.values
        .map { (name, list) ->
            CategoryTotal(
                category = name,
                expense = list.fold(BigDecimal.ZERO) { acc, entry -> acc - entry.amount },
                count = list.size,
            )
        }
        .sortedWith(
            compareByDescending<CategoryTotal> { it.expense }
                .thenBy { it.category.lowercase(Locale.ROOT) },
        )
}

/** 圆环图上的一段。金额与占比都已算好，绘制层不做任何业务计算。 */
data class DonutSegment(
    val label: String,
    val amount: BigDecimal,
    val fraction: Double,
    val colorKey: String,
    val count: Int,
) {
    val amountText: String get() = Formats.amount(amount)
    val tooltip: String get() = "$label $amountText · ${Formats.percent(fraction)} · $count 笔"
}

/** 分类排行里的一行。条形长度相对第一名，文字百分比相对总额。 */
data class CategoryRankRow(
    val label: String,
    val amount: BigDecimal,
    val share: Double,
    val barFraction: Double,
    val colorKey: String,
    val count: Int,
) {
    val amountText: String get() = Formats.amount(amount)
    val shareText: String get() = Formats.percent(share)
    val countText: String get() = "$count 笔"
}

/** 月度趋势里的一根柱子 */
data class MonthBar(
    val label: String,
    val amount: BigDecimal,
    val heightFraction: Double,
) {
    val amountText: String get() = if (amount.signum() == 0) "" else Formats.amount0(amount)
    val tooltip: String get() = "$label：${Formats.amount(amount)}"
}

/**
 * 统计页的一次完整快照。范围口径只在这里解释：
 * "本月是几号到几号""趋势取哪 12 个月"都是统计页的选择，不渗进持久化层。
 */
data class StatisticsSnapshot(
    val range: StatisticsRange,
    val rangeText: String,
    val segments: List<DonutSegment>,
    val legendRows: List<DonutSegment>,
    val rankRows: List<CategoryRankRow>,
    val trendBars: List<MonthBar>,
    val totalExpense: BigDecimal,
    val totalIncome: BigDecimal,
    val entryCount: Int,
    val topCategoryText: String,
    val averagePerDayText: String,
    val peakMonthText: String,
    val categories: List<String>,
    val allEntries: List<BillEntry>,
    val rangeEntries: List<BillEntry>,
) {
    val totalExpenseText: String get() = Formats.amount(totalExpense)
    val totalIncomeText: String get() = Formats.amount(totalIncome)
    val entryCountText: String get() = "$entryCount 笔"
    val isEmpty: Boolean get() = entryCount == 0
    val hasNoExpense: Boolean get() = totalExpense.signum() == 0
    val hasExpense: Boolean get() = totalExpense.signum() > 0
}

object StatisticsEngine {
    private const val MAX_DONUT_SEGMENTS = 8
    private const val MAX_LEGEND_ROWS = 5
    private const val TREND_MONTHS = 12

    /** 当前范围的首尾两天，都含在内。 */
    fun rangeBounds(range: StatisticsRange, today: LocalDate): Pair<LocalDate, LocalDate> =
        when (range) {
            StatisticsRange.MONTH ->
                today.withDayOfMonth(1) to today.withDayOfMonth(today.lengthOfMonth())
            StatisticsRange.YEAR ->
                LocalDate.of(today.year, 1, 1) to LocalDate.of(today.year, 12, 31)
            StatisticsRange.ALL ->
                LocalDate.MIN to today
        }

    fun rangeText(range: StatisticsRange, today: LocalDate): String = when (range) {
        StatisticsRange.MONTH -> Formats.monthLong(today)
        StatisticsRange.YEAR -> Formats.yearLong(today)
        StatisticsRange.ALL -> "全部时间"
    }

    fun compute(
        allEntries: List<BillEntry>,
        range: StatisticsRange,
        today: LocalDate = LocalDate.now(),
    ): StatisticsSnapshot {
        val (from, to) = rangeBounds(range, today)
        val rangeEntries = allEntries.filter { it.date >= from && it.date <= to }

        val breakdown = computeCategoryTotals(rangeEntries)

        val totalExpense = rangeEntries
            .filter { it.amount.signum() < 0 }
            .fold(BigDecimal.ZERO) { acc, entry -> acc - entry.amount }
        val totalIncome = rangeEntries
            .filter { it.amount.signum() > 0 }
            .fold(BigDecimal.ZERO) { acc, entry -> acc + entry.amount }

        val segments = buildSegments(breakdown)
        val rankRows = buildRanks(breakdown)

        val first = if (range == StatisticsRange.ALL) {
            rangeEntries.minOfOrNull { it.date } ?: to
        } else {
            from
        }
        val days = maxOf(1, ChronoUnit.DAYS.between(first, to).toInt() + 1)
        val average = totalExpense.divide(BigDecimal(days), 2, RoundingMode.HALF_UP)

        return StatisticsSnapshot(
            range = range,
            rangeText = rangeText(range, today),
            segments = segments,
            legendRows = segments.take(MAX_LEGEND_ROWS),
            rankRows = rankRows,
            trendBars = buildTrend(allEntries, today),
            totalExpense = totalExpense,
            totalIncome = totalIncome,
            entryCount = rangeEntries.size,
            topCategoryText = rankRows.firstOrNull()?.let { "最高 ${it.label} ${it.shareText}" }.orEmpty(),
            averagePerDayText = "日均 ${Formats.amount(average)}",
            peakMonthText = peakMonthText(allEntries, today),
            categories = computeCategoryTotals(allEntries).map { it.category },
            allEntries = allEntries,
            rangeEntries = rangeEntries,
        )
    }

    /** 圆环最多 8 段，其余合并成"其他 N 项"；合并段用独立的中性色。 */
    private fun buildSegments(breakdown: List<CategoryTotal>): List<DonutSegment> {
        if (breakdown.isEmpty()) return emptyList()

        val total = breakdown.fold(BigDecimal.ZERO) { acc, item -> acc + item.expense }
        val head = breakdown.take(MAX_DONUT_SEGMENTS)
        val tail = breakdown.drop(MAX_DONUT_SEGMENTS)

        val segments = head.map { item ->
            DonutSegment(
                label = item.category,
                amount = item.expense,
                fraction = fractionOf(item.expense, total),
                colorKey = CategoryPalette.themeKeyOf(item.category),
                count = item.count,
            )
        }.toMutableList()

        if (tail.isNotEmpty()) {
            val rest = tail.fold(BigDecimal.ZERO) { acc, item -> acc + item.expense }
            segments.add(
                DonutSegment(
                    label = "其他 ${tail.size} 项",
                    amount = rest,
                    fraction = fractionOf(rest, total),
                    colorKey = CategoryPalette.themeKeyOfSlot(CategoryPalette.SLOT_COUNT),
                    count = tail.sumOf { it.count },
                ),
            )
        }

        return segments
    }

    private fun buildRanks(breakdown: List<CategoryTotal>): List<CategoryRankRow> {
        if (breakdown.isEmpty()) return emptyList()

        val total = breakdown.fold(BigDecimal.ZERO) { acc, item -> acc + item.expense }
        val top = breakdown.first().expense

        return breakdown.map { item ->
            CategoryRankRow(
                label = item.category,
                amount = item.expense,
                share = fractionOf(item.expense, total),
                barFraction = fractionOf(item.expense, top),
                colorKey = CategoryPalette.themeKeyOf(item.category),
                count = item.count,
            )
        }
    }

    /**
     * 近 12 个月支出趋势。与当前范围无关：趋势本身就是跨范围的视图。
     * 柱高是相对最高一月的比例，不是绝对金额。
     */
    private fun buildTrend(allEntries: List<BillEntry>, today: LocalDate): List<MonthBar> {
        val thisMonth = today.withDayOfMonth(1)
        val totals = HashMap<LocalDate, BigDecimal>()

        for (entry in allEntries) {
            if (entry.amount.signum() >= 0) continue
            val month = entry.date.withDayOfMonth(1)
            totals[month] = (totals[month] ?: BigDecimal.ZERO) - entry.amount
        }

        val months = (TREND_MONTHS - 1 downTo 0).map { thisMonth.minusMonths(it.toLong()) }
        val peak = months.maxOfOrNull { totals[it] ?: BigDecimal.ZERO } ?: BigDecimal.ZERO

        return months.map { month ->
            val amount = totals[month] ?: BigDecimal.ZERO
            MonthBar(
                label = Formats.monthShort(month.monthValue),
                amount = amount,
                heightFraction = fractionOf(amount, peak),
            )
        }
    }

    private fun peakMonthText(allEntries: List<BillEntry>, today: LocalDate): String {
        val bars = buildTrend(allEntries, today)
        val peak = bars.maxOfOrNull { it.amount } ?: BigDecimal.ZERO
        if (peak.signum() == 0) return ""
        val first = bars.first { it.amount.compareTo(peak) == 0 }
        return "最高 ${first.label}"
    }

    /** 选中分类的全部流水（不受时间范围限制），日期倒序。 */
    fun categoryEntries(allEntries: List<BillEntry>, category: String): List<BillEntry> =
        allEntries
            .filter {
                CategoryPalette.normalize(it.category)
                    .equals(category, ignoreCase = true)
            }
            .sortedByDescending { it.date }

    /**
     * 选中分类的汇总文案。占比的分母是**全部时间**的支出：
     * 明细列表本身不受范围限制，分母跟着范围走会前后矛盾。
     */
    fun categorySummary(allEntries: List<BillEntry>, matching: List<BillEntry>): String {
        val spent = matching
            .filter { it.amount.signum() < 0 }
            .fold(BigDecimal.ZERO) { acc, entry -> acc - entry.amount }
        val earned = matching
            .filter { it.amount.signum() > 0 }
            .fold(BigDecimal.ZERO) { acc, entry -> acc + entry.amount }
        val allExpense = allEntries
            .filter { it.amount.signum() < 0 }
            .fold(BigDecimal.ZERO) { acc, entry -> acc - entry.amount }

        val share = if (allExpense.signum() == 0) {
            ""
        } else {
            " · 占 ${Formats.percent(fractionOf(spent, allExpense))}"
        }

        return if (earned.signum() > 0) {
            "共 ${matching.size} 笔 · 支出 ${Formats.amount(spent)} · 收入 ${Formats.amount(earned)}$share"
        } else {
            "共 ${matching.size} 笔 · 合计 ${Formats.amount(spent)}$share"
        }
    }

    private fun fractionOf(part: BigDecimal, whole: BigDecimal): Double =
        if (whole.signum() == 0) 0.0 else part.toDouble() / whole.toDouble()
}
