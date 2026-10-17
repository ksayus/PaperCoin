package com.ksayus.papercoin.sync

import com.ksayus.papercoin.data.BillChange
import com.ksayus.papercoin.data.BillEntry
import com.ksayus.papercoin.data.SyncHash
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.Serializable
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.LocalDate
import java.util.Base64

const val SYNC_PROTOCOL_VERSION = 1
const val SYNC_DISCOVERY_PORT = 47831
const val SYNC_PREFERRED_TCP_PORT = 47832
const val SYNC_MAX_FRAME = 8 * 1024 * 1024

@OptIn(ExperimentalSerializationApi::class)
val SyncJson: Json = Json {
    encodeDefaults = false
    explicitNulls = false
    ignoreUnknownKeys = true
}

@Serializable
data class WireMessage(
    val type: String,
    val protocol: Int? = null,
    val deviceId: String? = null,
    val deviceName: String? = null,
    val app: String? = null,
    val version: String? = null,
    val tcpPort: Int? = null,
    val nonce: String? = null,
    val mac: String? = null,
    val entries: List<BillEntry>? = null,
    val hash: String? = null,
    val expectedSnapshotHash: String? = null,
    val finalSnapshotHash: String? = null,
    val actions: List<WireAction>? = null,
    val baseline: List<BaselineItem>? = null,
    val count: Int? = null,
    val code: String? = null,
    val message: String? = null,
    val reason: String? = null,
)

@Serializable
data class WireAction(
    val op: String,
    val entry: BillEntry? = null,
    val id: String? = null,
    val date: String? = null,
)

@Serializable
data class BaselineItem(
    val id: String,
    val hash: String,
)

object SyncFraming {
    fun write(output: OutputStream, message: WireMessage) {
        val payload = SyncJson.encodeToString(message).toByteArray(Charsets.UTF_8)

        if (payload.size > SYNC_MAX_FRAME) {
            throw IOException("同步消息过大")
        }

        val data = DataOutputStream(output)
        data.writeInt(payload.size)
        data.write(payload)
        data.flush()
    }

    fun read(input: InputStream): WireMessage {
        val data = DataInputStream(input)

        val length = try {
            data.readInt()
        } catch (ex: EOFException) {
            throw EOFException("连接已关闭")
        }

        if (length <= 0 || length > SYNC_MAX_FRAME) {
            throw IOException("同步帧长度非法: $length")
        }

        val payload = ByteArray(length)
        data.readFully(payload)

        return SyncJson.decodeFromString(payload.toString(Charsets.UTF_8))
    }
}

object SyncAuth {
    private val random = SecureRandom()

    fun newNonce(): String {
        val bytes = ByteArray(16)
        random.nextBytes(bytes)
        return Base64.getEncoder().encodeToString(bytes)
    }

    fun mac(code: String, nonce: String, deviceId: String): String {
        val bytes = Base64.getDecoder().decode(nonce)
        return SyncHash.hmacHex(code, bytes, deviceId)
    }

    fun verify(code: String, nonce: String, deviceId: String, received: String?): Boolean {
        if (received.isNullOrBlank()) return false

        return try {
            val expected = mac(code, nonce, deviceId).toByteArray(Charsets.US_ASCII)
            val actual = received.lowercase().toByteArray(Charsets.US_ASCII)
            MessageDigest.isEqual(expected, actual)
        } catch (ex: IllegalArgumentException) {
            false
        }
    }
}

fun List<BillChange>.toWireActions(): List<WireAction> = map { change ->
    when (change) {
        is BillChange.Delete -> WireAction(
            op = "delete",
            id = change.id,
            date = change.date.toString(),
        )

        is BillChange.Upsert -> WireAction(
            op = "upsert",
            entry = change.entry,
        )
    }
}

fun List<WireAction>.toBillChanges(): List<BillChange> = map { action ->
    when (action.op) {
        "upsert" -> {
            val entry = action.entry ?: throw IllegalArgumentException("upsert 缺少 entry")
            BillChange.Upsert(entry)
        }

        "delete" -> {
            val id = action.id ?: throw IllegalArgumentException("delete 缺少 id")
            val date = action.date ?: throw IllegalArgumentException("delete 缺少 date")
            BillChange.Delete(id, LocalDate.parse(date))
        }

        else -> throw IllegalArgumentException("未知同步操作: ${action.op}")
    }
}

fun baselineItems(entries: List<BillEntry>): List<BaselineItem> =
    entries.sortedBy { it.id }.map { BaselineItem(it.id, SyncHash.contentHash(it)) }

fun List<BaselineItem>.toBaselineMap(): Map<String, String> =
    associate { it.id to it.hash }

fun snapshotMessage(snapshot: SyncSnapshot): WireMessage = WireMessage(
    type = "snapshot",
    entries = snapshot.entries,
    hash = snapshot.hash,
)

fun WireMessage.toSnapshot(): SyncSnapshot {
    val list = entries ?: emptyList()
    val computed = SyncHash.snapshotHash(list)

    if (hash != null && hash != computed) {
        throw IllegalArgumentException("快照哈希不一致")
    }

    return SyncSnapshot(list, computed)
}
