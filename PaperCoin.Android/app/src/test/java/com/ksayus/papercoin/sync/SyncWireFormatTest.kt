package com.ksayus.papercoin.sync

import com.ksayus.papercoin.data.BillEntry
import kotlinx.serialization.encodeToString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.math.BigDecimal
import java.time.LocalDate

/**
 * 线缆上的账单对象必须把五个字段全部写出来，哪怕值等于 Kotlin 默认值。
 * 桌面版的 WireConvert 用 "yyyy-MM-dd" 校验 date，缺字段会被判成“非法的日期”，
 * 所以这里守住的是跨平台互通的字节级契约。
 */
class SyncWireFormatTest {
    private fun snapshotJson(entry: BillEntry): String =
        SyncJson.encodeToString(snapshotMessage(SyncSnapshot.of(listOf(entry))))

    @Test
    fun `entry equal to every kotlin default still writes all fields`() {
        // 故意全部取默认值：空描述、零金额、无分类、今天的日期。
        val today = LocalDate.now()
        val entry = BillEntry(
            id = "11111111-1111-4111-8111-111111111111",
            description = "",
            amount = BigDecimal.ZERO,
            category = null,
            date = today,
        )

        val json = snapshotJson(entry)

        listOf("\"id\"", "\"description\"", "\"amount\"", "\"date\"").forEach { key ->
            assertTrue("线缆快照缺少 $key: $json", json.contains(key))
        }
        assertTrue(
            "今天的日期也必须原样写出: $json",
            json.contains("\"date\":\"$today\""),
        )
    }

    @Test
    fun `date is always written as yyyy-MM-dd`() {
        val day = LocalDate.of(2026, 1, 2)
        val entry = BillEntry(
            id = "22222222-2222-4222-8222-222222222222",
            description = "午饭",
            amount = BigDecimal("-12.5"),
            category = "餐饮",
            date = day,
        )

        val json = snapshotJson(entry)

        assertEquals(day.toString(), "2026-01-02")
        assertTrue("日期必须是 yyyy-MM-dd: $json", json.contains("\"date\":\"$day\""))
    }
}
