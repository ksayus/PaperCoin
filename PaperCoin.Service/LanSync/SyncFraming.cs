using System.Buffers.Binary;
using System.Text.Json;

namespace PaperCoin.Service.LanSync;

/// <summary>
/// 4 字节大端长度前缀 + UTF-8 JSON。单帧上限 8 MiB，读到超限长度立即拒绝，
/// 不先分配内存。
/// </summary>
public static class SyncFraming
{
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, SyncMessage message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SyncMessage.JsonOptions);

        if (payload.Length > MaxFrameBytes)
            throw new InvalidDataException($"消息过大: {payload.Length} 字节");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<SyncMessage> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadInt32BigEndian(header);

        if (length <= 0 || length > MaxFrameBytes)
            throw new InvalidDataException($"非法的帧长度: {length}");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

        SyncMessage? message;

        try
        {
            message = JsonSerializer.Deserialize<SyncMessage>(payload, SyncMessage.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("消息不是合法的 JSON", ex);
        }

        if (message is null || string.IsNullOrWhiteSpace(message.Type))
            throw new InvalidDataException("消息缺少 type");

        return message;
    }
}
