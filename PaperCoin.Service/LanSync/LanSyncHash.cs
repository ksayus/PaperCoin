using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PaperCoin.Service.LanSync;

/// <summary>
/// 协议 v1 的内容哈希 / 快照哈希 / 认证 HMAC。这里任何一处格式化不同，
/// Windows 与 Android 就会对同一份账单算出不同哈希，合并结果也会分叉。
/// </summary>
public static class LanSyncHash
{
    /// <summary>规范串的前缀</summary>
    public const string CanonicalPrefix = "PC1";

    /// <summary>字段分隔符 U+001F</summary>
    public const char Separator = '\u001f';

    /// <summary>
    /// 单条账单内容哈希。
    /// 规范串 = "PC1" 0x1F description 0x1F amount 0x1F category 0x1F date。
    /// </summary>
    public static string ContentHash(SyncEntry entry)
        => ContentHash(entry.Description, entry.Amount, entry.Category, entry.Date);

    public static string ContentHash(string description, decimal amount, string? category, string date)
    {
        var canonical = new StringBuilder(64 + description.Length + (category?.Length ?? 0) + date.Length);

        canonical.Append(CanonicalPrefix).Append(Separator)
            .Append(description ?? "").Append(Separator)
            .Append(FormatAmount(amount)).Append(Separator)
            .Append((category ?? "").Trim()).Append(Separator)
            .Append(date);

        return Sha256Hex(Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    /// <summary>
    /// 金额文本：两位小数、InvariantCulture、中点向远离零的方向舍入。
    /// 不用 <c>decimal.ToString("F2")</c> 直接产出：它的舍入模式在不同运行时上不够明确，
    /// 这里先显式舍入再格式化。
    /// </summary>
    public static string FormatAmount(decimal amount)
    {
        var rounded = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

        // 避免 -0.004 这类值被格式化成 "-0.00"
        if (rounded == 0m) return "0.00";

        return rounded.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 快照哈希：逐条算内容哈希，按 Id 序数排序，拼成 <c>id 0x1F hash "\n"</c>，
    /// 再对 UTF-8 字节做 SHA-256。空集合就是空字符串的 SHA-256。
    /// </summary>
    public static string SnapshotHash(IEnumerable<SyncEntry> entries)
    {
        var text = new StringBuilder();

        foreach (var entry in entries.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            text.Append(entry.Id).Append(Separator)
                .Append(ContentHash(entry)).Append('\n');
        }

        return Sha256Hex(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>
    /// 认证 MAC：HMAC-SHA256(key = UTF-8(code), message = nonceBytes || UTF-8(deviceId))。
    /// 返回小写十六进制。
    /// </summary>
    public static string ComputeAuthMac(string code, ReadOnlySpan<byte> nonce, string deviceId)
    {
        var deviceBytes = Encoding.UTF8.GetBytes(deviceId);
        var message = new byte[nonce.Length + deviceBytes.Length];

        nonce.CopyTo(message);
        deviceBytes.CopyTo(message.AsSpan(nonce.Length));

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexString(hmac.ComputeHash(message)).ToLowerInvariant();
    }

    /// <summary>
    /// 常量时间比较两个十六进制 MAC。长度不同会先返回 false，但仍只对字节做定长比较；
    /// 格式错误的输入直接视为不匹配。
    /// </summary>
    public static bool FixedTimeEqualsHex(string? left, string? right)
    {
        if (left is null || right is null) return false;

        byte[] leftBytes;
        byte[] rightBytes;

        try
        {
            leftBytes = Convert.FromHexString(left);
            rightBytes = Convert.FromHexString(right);
        }
        catch (FormatException)
        {
            return false;
        }

        if (leftBytes.Length != rightBytes.Length) return false;

        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    /// <summary>SHA-256，小写十六进制。</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>64 位小写十六进制的基本校验，用于拒绝明显不是哈希的线缆字段。</summary>
    public static bool IsHexHash(string? value)
    {
        if (value is null || value.Length != 64) return false;

        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }

        return true;
    }
}
