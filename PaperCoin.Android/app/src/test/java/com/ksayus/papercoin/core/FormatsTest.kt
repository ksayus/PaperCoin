package com.ksayus.papercoin.core

import org.junit.Assert.assertEquals
import org.junit.Test
import java.math.BigDecimal
import java.time.LocalDate

class FormatsTest {
    @Test
    fun `amounts use grouping and two decimals`() {
        assertEquals("1,234.50", Formats.amount(BigDecimal("1234.5")))
        assertEquals("-1,234.50", Formats.signedAmount(BigDecimal("-1234.5")))
        assertEquals("+1,234.50", Formats.signedAmount(BigDecimal("1234.5")))
        assertEquals("+0.00", Formats.signedAmount(BigDecimal.ZERO))
    }

    @Test
    fun `compact amount drops trailing zeros`() {
        assertEquals("12.5", Formats.compactAmount(BigDecimal("12.50")))
        assertEquals("12", Formats.compactAmount(BigDecimal("12.00")))
        assertEquals("-3.25", Formats.compactAmount(BigDecimal("-3.25")))
    }

    @Test
    fun `percent uses one decimal`() {
        assertEquals("12.3%", Formats.percent(0.1234))
        assertEquals("100.0%", Formats.percent(1.0))
    }

    @Test
    fun `chinese date formats follow the desktop wording`() {
        val date = LocalDate.of(2026, 10, 18)

        assertEquals("2026 年 10 月 18 日", Formats.dateLong(date))
        assertEquals("2026 年 10 月", Formats.monthLong(date))
        assertEquals("2026 年", Formats.yearLong(date))
        assertEquals("10 月 18 日", Formats.monthDay(date))
        assertEquals("10-18", Formats.dateShort(date))
    }
}
