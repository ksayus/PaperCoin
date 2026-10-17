package com.ksayus.papercoin.data

import com.ksayus.papercoin.sync.SyncStore
import org.junit.Assert.assertEquals
import org.junit.Test
import java.math.BigDecimal
import java.time.LocalDate

class SyncHashTest {
    @Test
    fun `content hash matches the protocol vector`() {
        val entry = BillEntry(
            id = "11111111-1111-4111-8111-111111111111",
            description = "午饭",
            amount = BigDecimal("-12.5"),
            category = "餐饮",
            date = LocalDate.of(2026, 10, 18),
        )

        assertEquals(
            "5015e4ccef8f9565d925127cc5b483b831f03bad2dab6bf0fb9482fa35f75593",
            SyncHash.contentHash(entry),
        )
    }

    @Test
    fun `snapshot hash sorts by id and matches the protocol vector`() {
        val first = BillEntry(
            id = "11111111-1111-4111-8111-111111111111",
            description = "午饭",
            amount = BigDecimal("-12.5"),
            category = "餐饮",
            date = LocalDate.of(2026, 10, 18),
        )
        val second = BillEntry(
            id = "22222222-2222-4222-8222-222222222222",
            description = "地铁",
            amount = BigDecimal("-6"),
            category = "交通",
            date = LocalDate.of(2026, 10, 18),
        )

        assertEquals(
            "0f3fee08b5b1ad19bdd72eabd7b8f16df60b36f7024a4d0228b04e84aa17e6c4",
            SyncHash.snapshotHash(listOf(second, first)),
        )
    }

    @Test
    fun `category whitespace and amount scale do not change the hash`() {
        val a = BillEntry(
            description = "午饭",
            amount = BigDecimal("-12.5"),
            category = "餐饮",
            date = LocalDate.of(2026, 10, 18),
        )
        val b = a.copy(
            amount = BigDecimal("-12.500"),
            category = " 餐饮 ",
        )

        assertEquals(SyncHash.contentHash(a), SyncHash.contentHash(b))
    }

    @Test
    fun `hmac matches the protocol vector`() {
        val nonce = ByteArray(16) { it.toByte() }

        assertEquals(
            "0889e7bae7ad9f5038aad194514aeb8a3195e1c4f83f8f9abc7e70c4b7198a08",
            SyncHash.hmacHex("ABCD2345", nonce, "device-1"),
        )
    }

    @Test
    fun `sync code validation`() {
        assertEquals(true, SyncStore.isValidCode("ABCD2345"))
        assertEquals(false, SyncStore.isValidCode("abc"))
        assertEquals(false, SyncStore.isValidCode("ABCD-2345"))
        assertEquals("ABCD2345", SyncStore.normalizeCode(" abcd2345 "))
    }
}
