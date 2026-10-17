package com.ksayus.papercoin.data

import com.ksayus.papercoin.core.CategoryPalette
import com.ksayus.papercoin.core.Formats
import kotlinx.serialization.Serializable
import java.math.BigDecimal
import java.time.LocalDate
import java.util.UUID

/**
 * 一条账单流水。字段名、类型与桌面版 `BillInfo.cs` 对齐：
 * `id / description / amount / category / date`，JSON 为 camelCase。
 */
@Serializable
data class BillEntry(
    val id: String = UUID.randomUUID().toString(),
    val description: String = "",
    @Serializable(with = BigDecimalNumberSerializer::class)
    val amount: BigDecimal = BigDecimal.ZERO,
    val category: String? = null,
    @Serializable(with = LocalDateIsoSerializer::class)
    val date: LocalDate = LocalDate.now(),
) {
    /** 列表卡片副标题的日期文本 */
    val dateText: String get() = Formats.dateShort(date)

    /** 带符号金额文本，正数为收入、负数为支出 */
    val amountText: String get() = Formats.signedAmount(amount)

    /** 分类对应的主题颜色键，供卡片色条使用 */
    val categoryColorKey: String get() = CategoryPalette.themeKeyOf(category)
}
