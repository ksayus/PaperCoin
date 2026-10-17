package com.ksayus.papercoin.data

import com.ksayus.papercoin.core.CategoryPalette
import com.ksayus.papercoin.core.Formats
import java.math.BigDecimal
import java.math.RoundingMode

/** CSV 导出。写 UTF-8 带 BOM，Excel 直接打开不乱码。 */
object CsvExporter {
    fun build(
        entries: List<BillEntry>,
        totalExpense: BigDecimal,
        totalIncome: BigDecimal,
        count: Int,
    ): String {
        val csv = StringBuilder()

        // 桌面版用 AppendLine，Windows 上是 CRLF；这里固定 CRLF，跨平台打开都稳。
        csv.append("日期,描述,分类,金额\r\n")

        for (entry in entries.sortedBy { it.date }) {
            csv.append(entry.date.toString()).append(',')
                .append(escape(entry.description)).append(',')
                .append(escape(CategoryPalette.normalize(entry.category))).append(',')
                .append(entry.amount.setScale(2, RoundingMode.HALF_UP).toPlainString()).append("\r\n")
        }

        csv.append("\r\n")
        csv.append("支出合计,").append(totalExpense.setScale(2, RoundingMode.HALF_UP).toPlainString()).append("\r\n")
        csv.append("收入合计,").append(totalIncome.setScale(2, RoundingMode.HALF_UP).toPlainString()).append("\r\n")
        csv.append("笔数,").append(count).append("\r\n")

        return csv.toString()
    }

    fun fileName(rangeText: String, now: java.time.LocalDateTime): String {
        val stamp = String.format(
            java.util.Locale.ROOT,
            "%04d%02d%02d-%02d%02d%02d",
            now.year, now.monthValue, now.dayOfMonth, now.hour, now.minute, now.second,
        )
        return "papercoin-统计-${rangeText.replace(" ", "")}-$stamp.csv"
    }

    /** 含逗号、引号或换行时用双引号包住，内部引号翻倍。 */
    private fun escape(value: String): String {
        if (value.none { it == ',' || it == '"' || it == '\n' || it == '\r' }) return value
        return '"' + value.replace("\"", "\"\"") + '"'
    }
}
