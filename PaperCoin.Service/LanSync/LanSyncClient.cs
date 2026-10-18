using System.Net.Sockets;
using PaperCoin.Core;
using Serilog;

namespace PaperCoin.Service.LanSync;

public sealed record LanSyncClientOptions(
    string Host,
    int Port,
    string DeviceId,
    string DeviceName,
    string Code,
    int LocalTcpPort,
    IReadOnlyDictionary<string, string> Baseline,
    IReadOnlyDictionary<string, ConflictChoice>? Choices = null,
    string? ExpectedPeerId = null,
    Func<string, IReadOnlyDictionary<string, string>>? BaselineLookup = null,
    Func<string, IReadOnlyDictionary<string, ConflictChoice>?>? ChoicesLookup = null);

public sealed class ClientSyncResult
{
    public bool Success { get; init; }
    public bool HasConflicts { get; init; }
    public string ErrorCode { get; init; } = "";
    public string ErrorMessage { get; init; } = "";
    public string ServerDeviceId { get; init; } = "";
    public string ServerDeviceName { get; init; } = "";
    public string FinalSnapshotHash { get; init; } = "";
    public int AppliedCount { get; init; }
    public int LocalActionCount { get; init; }
    public IReadOnlyDictionary<string, string> Baseline { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<MergeConflict> Conflicts { get; init; } = [];

    public static ClientSyncResult Failure(
        string code,
        string message,
        string serverDeviceId = "",
        string serverDeviceName = "") => new()
    {
        ErrorCode = code,
        ErrorMessage = message,
        ServerDeviceId = serverDeviceId,
        ServerDeviceName = serverDeviceName
    };
}

/// <summary>
/// 同步会话的客户端：握手、交换快照、三方合并、发送 apply，收到 applied 后才改本机。
/// 冲突时发送 abort 并返回冲突列表；任何失败都不会先写本机数据。
/// </summary>
public sealed class LanSyncClient
{
    public const int ConnectTimeoutSeconds = 5;

    private readonly BillRepository _repository;

    public LanSyncClient(BillRepository repository)
    {
        _repository = repository;
    }

    public async Task<ClientSyncResult> SyncAsync(
        LanSyncClientOptions options,
        CancellationToken ct = default)
    {
        var code = SyncStore.NormalizeCode(options.Code);

        if (!SyncStore.IsValidCode(code))
            return ClientSyncResult.Failure("auth", "同步码为空或不合法");

        if (string.IsNullOrWhiteSpace(options.Host) || options.Port is <= 0 or > 65535)
            return ClientSyncResult.Failure("invalid", "目标地址不合法");

        var serverDeviceId = "";
        var serverDeviceName = "";

        try
        {
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sessionCts.CancelAfter(TimeSpan.FromMinutes(LanSyncServer.SessionTimeoutMinutes));

            using var client = new TcpClient();

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
                await client.ConnectAsync(options.Host, options.Port, connectCts.Token).ConfigureAwait(false);
            }

            client.NoDelay = true;
            var stream = client.GetStream();

            await WriteFrameAsync(
                stream,
                SyncMessage.Hello(options.DeviceId, options.DeviceName, options.LocalTcpPort),
                sessionCts.Token).ConfigureAwait(false);

            var challenge = await ReadFrameAsync(stream, sessionCts.Token).ConfigureAwait(false);

            if (!string.Equals(challenge.Type, "challenge", StringComparison.Ordinal))
                throw new SyncProtocolException("expected challenge");

            byte[] nonce;

            try
            {
                nonce = Convert.FromBase64String(challenge.Nonce ?? "");
            }
            catch (FormatException)
            {
                throw new SyncProtocolException("invalid nonce");
            }

            if (nonce.Length != 16) throw new SyncProtocolException("invalid nonce length");

            var mac = LanSyncHash.ComputeAuthMac(code, nonce, options.DeviceId);

            await WriteFrameAsync(stream, SyncMessage.Auth(mac), sessionCts.Token).ConfigureAwait(false);

            var welcome = await ReadFrameAsync(stream, sessionCts.Token).ConfigureAwait(false);

            if (!string.Equals(welcome.Type, "welcome", StringComparison.Ordinal))
                throw new SyncProtocolException("expected welcome");

            if (!SyncEntry.IsValidId(welcome.DeviceId))
                throw new SyncProtocolException("invalid server deviceId");

            serverDeviceId = SyncEntry.NormalizeId(welcome.DeviceId!);
            serverDeviceName = string.IsNullOrWhiteSpace(welcome.DeviceName)
                ? "未知设备"
                : welcome.DeviceName.Trim();

            if (!string.IsNullOrEmpty(options.ExpectedPeerId)
                && !string.Equals(options.ExpectedPeerId, serverDeviceId, StringComparison.Ordinal))
                throw new SyncProtocolException("连接到的设备与目标设备不一致");

            var baseline = options.BaselineLookup?.Invoke(serverDeviceId) ?? options.Baseline;
            var choices = options.ChoicesLookup?.Invoke(serverDeviceId) ?? options.Choices;

            var localSnapshot = await _repository.ReadSnapshotAsync(sessionCts.Token).ConfigureAwait(false);

            await WriteFrameAsync(
                stream, SyncMessage.Snapshot(localSnapshot), sessionCts.Token).ConfigureAwait(false);

            var remoteMessage = await ReadFrameAsync(stream, sessionCts.Token).ConfigureAwait(false);

            if (!string.Equals(remoteMessage.Type, "snapshot", StringComparison.Ordinal))
                throw new SyncProtocolException("expected server snapshot");

            // 对方快照内容不合法属于"数据不合法"，不是版本不兼容：具体原因（如"非法的日期: ..."）
            // 必须原样带出去，否则用户只会看到一句"协议版本不兼容"。
            if (!WireConvert.TryToSnapshot(remoteMessage, out var remoteSnapshot, out var snapshotError))
                return ClientSyncResult.Failure("invalid", snapshotError, serverDeviceId, serverDeviceName);

            var merge = MergeEngine.Merge(
                localSnapshot,
                remoteSnapshot,
                baseline,
                choices);

            if (!merge.IsComplete)
            {
                // 协议：有未解决冲突就 abort，两个设备都不落盘。
                await TryAbortAsync(stream, "conflict", sessionCts.Token).ConfigureAwait(false);

                return new ClientSyncResult
                {
                    HasConflicts = true,
                    Conflicts = merge.Conflicts,
                    ServerDeviceId = serverDeviceId,
                    ServerDeviceName = serverDeviceName
                };
            }

            var applyMessage = SyncMessage.Apply(
                remoteSnapshot.Hash,
                merge.Final.Hash,
                merge.RemoteActions,
                merge.Final);

            await WriteFrameAsync(stream, applyMessage, sessionCts.Token).ConfigureAwait(false);

            var applied = await ReadFrameAsync(stream, sessionCts.Token).ConfigureAwait(false);

            if (!string.Equals(applied.Type, "applied", StringComparison.Ordinal))
                throw new SyncProtocolException("expected applied");

            if (!string.Equals(applied.Hash, merge.Final.Hash, StringComparison.OrdinalIgnoreCase))
                throw new SyncProtocolException("server final snapshot hash mismatch");

            // 协议第 9 步：先在本地核对交换快照后本机没有被改过，再应用本地动作。
            var current = await _repository.ReadSnapshotAsync(sessionCts.Token).ConfigureAwait(false);

            if (!string.Equals(current.Hash, localSnapshot.Hash, StringComparison.Ordinal))
            {
                return ClientSyncResult.Failure(
                    "snapshot-changed",
                    "本机账单在同步过程中发生了变化，本次未写入",
                    serverDeviceId,
                    serverDeviceName);
            }

            var localApply = await _repository.ApplyChangesAsync(
                localSnapshot.Hash,
                merge.LocalActions,
                merge.Final.Hash,
                sessionCts.Token).ConfigureAwait(false);

            if (!localApply.IsSuccess)
            {
                return ClientSyncResult.Failure(
                    localApply.Status == BillApplyStatus.SnapshotChanged ? "snapshot-changed" : "internal",
                    localApply.Message,
                    serverDeviceId,
                    serverDeviceName);
            }

            return new ClientSyncResult
            {
                Success = true,
                ServerDeviceId = serverDeviceId,
                ServerDeviceName = serverDeviceName,
                FinalSnapshotHash = merge.Final.Hash,
                AppliedCount = applied.Count ?? 0,
                LocalActionCount = merge.LocalActions.Count,
                Baseline = MergeEngine.BaselineMap(merge.Final)
            };
        }
        catch (RemoteErrorException ex)
        {
            return ClientSyncResult.Failure(ex.Code, ex.Message, serverDeviceId, serverDeviceName);
        }
        catch (SyncProtocolException ex)
        {
            return ClientSyncResult.Failure("protocol", ex.Message, serverDeviceId, serverDeviceName);
        }
        catch (InvalidDataException ex)
        {
            return ClientSyncResult.Failure("invalid", ex.Message, serverDeviceId, serverDeviceName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ClientSyncResult.Failure("canceled", "同步已取消", serverDeviceId, serverDeviceName);
        }
        catch (OperationCanceledException)
        {
            return ClientSyncResult.Failure("timeout", "同步超时或对方无响应", serverDeviceId, serverDeviceName);
        }
        catch (SocketException)
        {
            return ClientSyncResult.Failure("network", "无法连接对方设备", serverDeviceId, serverDeviceName);
        }
        catch (IOException)
        {
            return ClientSyncResult.Failure("network", "与对方设备的连接中断", serverDeviceId, serverDeviceName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "同步客户端异常");
            return ClientSyncResult.Failure("internal", "同步客户端内部错误", serverDeviceId, serverDeviceName);
        }
    }

    private static async Task<SyncMessage> ReadFrameAsync(NetworkStream stream, CancellationToken sessionToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        cts.CancelAfter(TimeSpan.FromSeconds(LanSyncServer.ReadTimeoutSeconds));

        var message = await SyncFraming.ReadAsync(stream, cts.Token).ConfigureAwait(false);

        if (string.Equals(message.Type, "error", StringComparison.Ordinal))
            throw new RemoteErrorException(message.Code ?? "error", message.Message ?? "对方返回错误");

        return message;
    }

    private static async Task WriteFrameAsync(
        NetworkStream stream, SyncMessage message, CancellationToken sessionToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        cts.CancelAfter(TimeSpan.FromSeconds(LanSyncServer.ReadTimeoutSeconds));

        await SyncFraming.WriteAsync(stream, message, cts.Token).ConfigureAwait(false);
    }

    private static async Task TryAbortAsync(NetworkStream stream, string reason, CancellationToken sessionToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            await SyncFraming.WriteAsync(stream, SyncMessage.Abort(reason), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                       or ObjectDisposedException)
        {
            // 对方已经关闭；abort 只是礼貌通知
        }
    }

    private sealed class RemoteErrorException : Exception
    {
        public RemoteErrorException(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    private sealed class SyncProtocolException : Exception
    {
        public SyncProtocolException(string message) : base(message)
        {
        }
    }
}
