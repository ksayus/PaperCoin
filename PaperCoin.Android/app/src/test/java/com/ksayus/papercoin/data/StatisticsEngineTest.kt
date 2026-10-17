package com.ksayus.papercoin.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.math.BigDecimal
import java.time.LocalDate

class StatisticsEngineTest {
    private val today = LocalDate.of(2026, 10, 18)

    private fun entry(date: String, amount: String, category: String?) = BillEntry(
        description = "x",
        amount = BigDecimal(amount),
        category = category,
        date = LocalDate.parse(date),
    )

    private val entries = listOf(
        entry("2026-10-01", "-10", "Food"),
        entry("2026-10-02", "-5", "交通"),
        entry("2026-10-03", "-2.5", "Food"),
        entry("2026-10-04", "1000", "工资"),
        entry("2026-09-15", "-3", "餐饮"),
        entry("2026-08-15", "-7", "交通"),
        entry("2025-10-15", "-1", "餐饮"),
    )

    private fun assertAmount(expected: String, actual: BigDecimal) {
        assertEquals(0, BigDecimal(expected).compareTo(actual))
    }

    @Test
    fun `month range only sees the current month`() {
        val snapshot = StatisticsEngine.compute(entries, StatisticsRange.MONTH, today)

        assertAmount("17.5", snapshot.totalExpense)
        assertAmount("1000", snapshot.totalIncome)
        assertEquals(4, snapshot.entryCount)
        assertEquals("2026 年 10 月", snapshot.rangeText)

        assertEquals(2, snapshot.segments.size)
        assertEquals("Food", snapshot.segments[0].label)
        assertAmount("12.5", snapshot.segments[0].amount)
        assertEquals("交通", snapshot.segments[1].label)
        assertEquals(2, snapshot.rankRows.size)
        // 分类下拉取全量流水（分类明细不受范围限制），所以还包括 9 月的“餐饮”
        assertEquals(listOf("Food", "交通", "餐饮"), snapshot.categories)
    }

    @Test
    fun `year and all ranges differ by the entries they include`() {
        val year = StatisticsEngine.compute(entries, StatisticsRange.YEAR, today)
        val all = StatisticsEngine.compute(entries, StatisticsRange.ALL, today)

        assertAmount("27.5", year.totalExpense)
        assertEquals(6, year.entryCount)

        assertAmount("28.5", all.totalExpense)
        assertEquals(7, all.entryCount)
        assertEquals("全部时间", all.rangeText)
    }

    @Test
    fun `trend is the last twelve months and ignores the current range`() {
        val snapshot = StatisticsEngine.compute(entries, StatisticsRange.MONTH, today)

        assertEquals(12, snapshot.trendBars.size)
        assertEquals("11 月", snapshot.trendBars.first().label)
        assertEquals("10 月", snapshot.trendBars.last().label)

        val october = snapshot.trendBars.last()
        assertAmount("17.5", october.amount)
        assertEquals(1.0, october.heightFraction, 0.0001)
        assertEquals("最高 10 月", snapshot.peakMonthText)

        val august = snapshot.trendBars.first { it.label == "8 月" }
        assertEquals(7.0 / 17.5, august.heightFraction, 0.0001)
    }

    @Test
    fun `top category and daily average match the desktop wording`() {
        val snapshot = StatisticsEngine.compute(entries, StatisticsRange.MONTH, today)

        assertEquals("最高 Food 71.4%", snapshot.topCategoryText)
        // 本月范围从 10 月 1 日到 10 月 31 日，共 31 天
        assertEquals("日均 0.56", snapshot.averagePerDayText)
    }

    @Test
    fun `more than eight categories collapse into one other segment`() {
        val many = (1..9).map { index ->
            entry("2026-10-0$index", "-1", "分类$index")
        }

        val snapshot = StatisticsEngine.compute(many, StatisticsRange.MONTH, today)

        assertEquals(9, snapshot.segments.size)
        assertEquals("其他 1 项", snapshot.segments.last().label)
        assertEquals(CategoryPaletteOtherKey, snapshot.segments.last().colorKey)
        // 图例只列前 5 段，环上仍是 9 段
        assertEquals(5, snapshot.legendRows.size)
    }

    @Test
    fun `category detail is case insensitive and sorted newest first`() {
        val matching = StatisticsEngine.categoryEntries(entries, "food")

        assertEquals(2, matching.size)
        assertEquals(LocalDate.parse("2026-10-03"), matching[0].date)
        assertEquals(LocalDate.parse("2026-10-01"), matching[1].date)
    }

    @Test
    fun `category summary uses all-time expense as the share denominator`() {
        val matching = StatisticsEngine.categoryEntries(entries, "Food")
        val summary = StatisticsEngine.categorySummary(entries, matching)

        // 12.5 / 28.5 = 43.86%
        assertEquals("共 2 笔 · 合计 12.50 · 占 43.9%", summary)
    }

    @Test
    fun `empty range has no expense and no segments`() {
        val snapshot = StatisticsEngine.compute(emptyList(), StatisticsRange.MONTH, today)

        assertTrue(snapshot.isEmpty)
        assertTrue(snapshot.hasNoExpense)
        assertFalse(snapshot.hasExpense)
        assertTrue(snapshot.segments.isEmpty())
        assertTrue(snapshot.rankRows.isEmpty())
        assertEquals("日均 0.00", snapshot.averagePerDayText)
    }

    private companion object {
        const val CategoryPaletteOtherKey = "CategoryColorOther"
    }
}
