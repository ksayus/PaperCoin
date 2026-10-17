namespace PaperCoin.Service;

/// <summary>同步落盘的结果。只有 <see cref="Success"/> 表示最终快照已经等于协议里的 finalSnapshotHash。</summary>
public enum BillApplyStatus
{
    Success,

    /// <summary>磁盘快照与期望值不一致；本次没有写入任何内容，调用方应重试。</summary>
    SnapshotChanged,

    /// <summary>动作或哈希格式非法；没有写入。</summary>
    Invalid,

    /// <summary>写入或写后校验失败。可能已有部分写入，下一次同步会收敛。</summary>
    Internal
}

public sealed record BillApplyResult(BillApplyStatus Status, string Message)
{
    public bool IsSuccess => Status == BillApplyStatus.Success;
}
