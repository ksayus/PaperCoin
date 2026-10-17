package com.ksayus.papercoin.data

import java.math.RoundingMode
import java.security.MessageDigest
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * 局域网同步的内容哈希。必须与桌面版 `LanSyncHash.cs` 逐字节一致：
 * 相同的账单内容在两端必须得到相同的哈希，否则每次同步都会误判成冲突。
 */
object SyncHash {
    private const val FIELD_SEPARATOR = '\u001F'
    private const val HASH_VERSION = "PC1"

    fun contentHash(entry: BillEntry): String {
        val canonical = buildString {
            append(HASH_VERSION).append(FIELD_SEPARATOR)
            append(entry.description).append(FIELD_SEPARATOR)
            append(entry.amount.setScale(2, RoundingMode.HALF_UP).toPlainString())
                .append(FIELD_SEPARATOR)
            append(entry.category?.trim().orEmpty()).append(FIELD_SEPARATOR)
            append(entry.date.toString())
        }

        return sha256Hex(canonical.toByteArray(Charsets.UTF_8))
    }

    fun snapshotHash(entries: List<BillEntry>): String {
        val builder = StringBuilder()

        for (entry in entries.sortedBy { it.id }) {
            builder.append(entry.id)
                .append(FIELD_SEPARATOR)
                .append(contentHash(entry))
                .append('\n')
        }

        return sha256Hex(builder.toString().toByteArray(Charsets.UTF_8))
    }

    fun hmacHex(code: String, nonce: ByteArray, deviceId: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(code.toByteArray(Charsets.UTF_8), "HmacSHA256"))

        val message = nonce + deviceId.toByteArray(Charsets.UTF_8)
        return hex(mac.doFinal(message))
    }

    fun hex(bytes: ByteArray): String {
        val chars = CharArray(bytes.size * 2)
        val digits = "0123456789abcdef"

        bytes.forEachIndexed { index, byte ->
            val value = byte.toInt() and 0xFF
            chars[index * 2] = digits[value ushr 4]
            chars[index * 2 + 1] = digits[value and 0x0F]
        }

        return String(chars)
    }

    private fun sha256Hex(bytes: ByteArray): String =
        hex(MessageDigest.getInstance("SHA-256").digest(bytes))
}
