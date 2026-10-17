package com.ksayus.papercoin.core

import java.math.BigDecimal
import java.math.RoundingMode
import java.time.LocalDate
import java.util.Locale

/** 展示层统一的数字与日期格式。正负号手动补，不让区域设置决定负号。 */
object Formats {
    /** 两位小数 + 千分位，对应 C# 的 N2 */
    fun amount(value: BigDecimal): String = String.format(Locale.CHINA, "%,.2f", value)

    /** 一位小数百分比，对应 C# 的 P1；输入是 0..1 的比例 */
    fun percent(fraction: Double): String =
        String.format(Locale.CHINA, "%.1f%%", fraction * 100)

    /** 金额带正负号：正数 "+1,234.50"，负数 "-1,234.50" */
    fun signedAmount(value: BigDecimal): String {
        val sign = if (value.signum() >= 0) "+" else "-"
        return sign + amount(value.abs())
    }

    /** 日历格子里的紧凑金额，对应 C# 的 0.## */
    fun compactAmount(value: BigDecimal): String =
        value.setScale(2, RoundingMode.HALF_UP).stripTrailingZeros().toPlainString()

    /** 无小数点的整额，用于趋势柱顶部的标注（C# 的 N0） */
    fun amount0(value: BigDecimal): String =
        String.format(Locale.CHINA, "%,.0f", value)

    /** "10-18" */
    fun dateShort(date: LocalDate): String =
        String.format(Locale.ROOT, "%02d-%02d", date.monthValue, date.dayOfMonth)

    /** "2026-10-18" */
    fun dateIso(date: LocalDate): String = date.toString()

    /** "2026 年 10 月 18 日" */
    fun dateLong(date: LocalDate): String =
        "${date.year} 年 ${date.monthValue} 月 ${date.dayOfMonth} 日"

    /** "2026 年 10 月" */
    fun monthLong(date: LocalDate): String = "${date.year} 年 ${date.monthValue} 月"

    /** "2026 年" */
    fun yearLong(date: LocalDate): String = "${date.year} 年"

    /** "10 月" */
    fun monthShort(month: Int): String = "$month 月"

    /** "M 月 d 日" */
    fun monthDay(date: LocalDate): String = "${date.monthValue} 月 ${date.dayOfMonth} 日"
}
