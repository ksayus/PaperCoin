package com.ksayus.papercoin.data

import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.math.BigDecimal
import java.time.LocalDate

class JsonCompatTest {
    @Test
    fun `reads a desktop bills file`() {
        val json = """
            [
              {
                "id": "0f8fad5b-d9cb-469f-a165-70867728950e",
                "description": "午饭",
                "amount": -12.5,
                "category": "餐饮",
                "date": "2026-10-18"
              }
            ]
        """.trimIndent()

        val entries = PaperCoinJson.decodeFromString<List<BillEntry>>(json)

        assertEquals(1, entries.size)
        assertEquals("午饭", entries[0].description)
        assertEquals(0, BigDecimal("-12.5").compareTo(entries[0].amount))
        assertEquals("餐饮", entries[0].category)
        assertEquals(LocalDate.of(2026, 10, 18), entries[0].date)
    }

    @Test
    fun `writes amounts as numbers and dates as strings`() {
        val entry = BillEntry(
            description = "午饭",
            amount = BigDecimal("-12.5"),
            category = "餐饮",
            date = LocalDate.of(2026, 10, 18),
        )

        val json = PaperCoinJson.encodeToString(listOf(entry))

        assertTrue(json.contains("\"amount\": -12.5"))
        assertTrue(json.contains("\"date\": \"2026-10-18\""))
        assertTrue(json.contains("\"category\": \"餐饮\""))
    }

    @Test
    fun `config keeps unknown keys from breaking the read`() {
        val json = """
            {
              "app": {
                "autoUpdate": true,
                "theme": "Bright",
                "themeStyle": "Liquid",
                "futureKey": 42
              },
              "logging": { "level": "Debug" }
            }
        """.trimIndent()

        val config = PaperCoinJson.decodeFromString<AppConfig>(json)

        assertTrue(config.app.autoUpdate)
        assertEquals("Bright", config.app.theme)
        assertEquals("Liquid", config.app.themeStyle)
        assertEquals("Debug", config.logging.level)
        assertEquals(7, config.logging.retainDays)
    }

    @Test
    fun `csv escapes commas and quotes and ends with totals`() {
        val entries = listOf(
            BillEntry(
                description = "a,b\"c",
                amount = BigDecimal("-2.5"),
                category = null,
                date = LocalDate.of(2026, 10, 18),
            ),
        )

        val csv = CsvExporter.build(entries, BigDecimal("2.5"), BigDecimal.ZERO, 1)

        assertTrue(csv.startsWith("日期,描述,分类,金额\r\n"))
        assertTrue(csv.contains("\"a,b\"\"c\",未分类,-2.50"))
        assertTrue(csv.contains("支出合计,2.50"))
        assertTrue(csv.contains("笔数,1"))
    }
}
