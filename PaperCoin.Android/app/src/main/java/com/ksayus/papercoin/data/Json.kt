package com.ksayus.papercoin.data

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.KSerializer
import kotlinx.serialization.SerializationException
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.descriptors.SerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonDecoder
import kotlinx.serialization.json.JsonEncoder
import kotlinx.serialization.json.JsonUnquotedLiteral
import kotlinx.serialization.json.jsonPrimitive
import java.math.BigDecimal
import java.time.LocalDate
import java.time.format.DateTimeParseException

/**
 * 与桌面版一致的 JSON 选项：camelCase（由属性名决定）、缩进、保留中文字面量、
 * 写入默认值。字段不认识的旧版本数据直接忽略，不因多一个键就整份读不出来。
 */
@OptIn(ExperimentalSerializationApi::class)
val PaperCoinJson: Json = Json {
    prettyPrint = true
    prettyPrintIndent = "  "
    encodeDefaults = true
    ignoreUnknownKeys = true
}

/** C# 的 DateOnly 序列化成 "yyyy-MM-dd"。 */
object LocalDateIsoSerializer : KSerializer<LocalDate> {
    override val descriptor: SerialDescriptor =
        PrimitiveSerialDescriptor("LocalDate", PrimitiveKind.STRING)

    override fun serialize(encoder: Encoder, value: LocalDate) {
        encoder.encodeString(value.toString())
    }

    override fun deserialize(decoder: Decoder): LocalDate {
        val text = decoder.decodeString()
        return try {
            LocalDate.parse(text)
        } catch (ex: DateTimeParseException) {
            throw SerializationException("非法日期: $text", ex)
        }
    }
}

/**
 * 金额以 JSON 数字（而不是字符串）落盘，和 C# 的 decimal 输出保持一致。
 * 用 [JsonUnquotedLiteral] 而不是 encodeDouble：12.34 不该在往返之后变成 12.340000000000001。
 */
@OptIn(ExperimentalSerializationApi::class)
object BigDecimalNumberSerializer : KSerializer<BigDecimal> {
    override val descriptor: SerialDescriptor =
        PrimitiveSerialDescriptor("BigDecimal", PrimitiveKind.DOUBLE)

    override fun serialize(encoder: Encoder, value: BigDecimal) {
        val jsonEncoder = encoder as? JsonEncoder
            ?: throw SerializationException("金额只能写进 JSON")
        jsonEncoder.encodeJsonElement(JsonUnquotedLiteral(value.toPlainString()))
    }

    override fun deserialize(decoder: Decoder): BigDecimal {
        val jsonDecoder = decoder as? JsonDecoder
            ?: throw SerializationException("金额只能从 JSON 读取")
        val literal = jsonDecoder.decodeJsonElement().jsonPrimitive.content
        return literal.toBigDecimalOrNull()
            ?: throw SerializationException("非法金额: $literal")
    }
}
