using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Serilog;

namespace PaperCoin.Service.LanSync;

/// <summary>
/// 服务端身份与认证材料。Code 只在本机内存里参与 HMAC，绝不出现在任何线缆消息中。
/// </summary>
public sealed record LanSyncIdentity(string DeviceId, string DeviceName, string Code);

/// <summary>服务端成功应用一批变更后的回调数据。</summary>
public sealed record PeerAppliedInfo(
    string DeviceId,
    string DeviceName,
    IReadOnlyDictionary<string, string> Baseline,
    string FinalSnapshotHash,
    int ActionCount);

/// <summary>
/// 局域网同步的 TCP 服务端：一次只服务一个会话，握手 → 交换快照 → 接收 apply。
/// 所有网络等待都是异步的，调用方（UI）永远不会被阻塞。
/// </summary>
public sealed class LanSyncServer : IAsyncDisposable
{
    public const int PreferredPort = 47832;
    public const int ReadTimeoutSeconds = 30;
    public const int SessionTimeoutMinutes = 2;

    private readonly BillRepository _repository;
    private readonly Func<LanSyncIdentity> _identityProvider;
    private readonly Func<PeerAppliedInfo, Task> _onApplied;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly ConcurrentDictionary<Task, byte> _sessions = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private TcpClient? _activeClient;
    private int _port;

    public LanSyncServer(
        BillRepository repository,
        Func<LanSyncIdentity> identityProvider,
        Func<PeerAppliedInfo, Task> onApplied)
    {
        _repository = repository;
        _identityProvider = identityProvider;
        _onApplied = onApplied;
    }

    /// <summary>实际监听的端口。预选端口被占用时会退回系统分配的端口。</summary>
    public int Port => _port;

    public bool IsRunning => _listener is not null;

    public Task StartAsync(int preferredPort = PreferredPort, CancellationToken ct = default)
    {
        if (_listener is not null) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        TcpListener listener;

        try
        {
            listener = new TcpListener(IPAddress.Any, preferredPort);
            listener.Start();
        }
        catch (SocketException) when (preferredPort != 0)
        {
            // 协议：首选端口被占用时绑定 0，并把实际端口放进 discover/offer。
            listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
        }

        _listener = listener;
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        Log.Information("局域网同步 TCP 服务监听 {Port}", _port);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        _cts = null;

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经停过
        }

        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // 已关闭
        }

        _listener = null;

        try
        {
            _activeClient?.Close();
        }
        catch (ObjectDisposedException)
        {
            // 已关闭
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                // 退出阶段不阻塞
            }
        }

        _acceptLoop = null;

        var sessions = _sessions.Keys.ToArray();
        if (sessions.Length > 0)
        {
            try
            {
                await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // 退出阶段不阻塞
            }
        }

        cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Warning(ex, "接受同步连接失败");
                continue;
            }

            var task = HandleAcceptedAsync(client, ct);
            _sessions[task] = 0;
            _ = task.ContinueWith(t => _sessions.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleAcceptedAsync(TcpClient client, CancellationToken serverToken)
    {
        if (!_sessionGate.Wait(0))
        {
            await SendBusyAndCloseAsync(client).ConfigureAwait(false);
            return;
        }

        _activeClient = client;

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        sessionCts.CancelAfter(TimeSpan.FromMinutes(SessionTimeoutMinutes));

        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            await RunSessionAsync(stream, sessionCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 超时或停止
        }
        catch (IOException)
        {
            // 对方关闭
        }
        catch (SocketException)
        {
            // 对方关闭
        }
        catch (ObjectDisposedException)
        {
            // 停止时关闭了 socket
        }
        catch (InvalidDataException ex)
        {
            await TrySendErrorAsync(client, "invalid", ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "同步服务端会话异常");
            await TrySendErrorAsync(client, "internal", "同步服务端内部错误").ConfigureAwait(false);
        }
        finally
        {
            _activeClient = null;

            try
            {
                client.Close();
            }
            catch (ObjectDisposedException)
            {
                // 已关闭
            }

            _sessionGate.Release();
        }
    }

    private async Task RunSessionAsync(NetworkStream stream, CancellationToken ct)
    {
        var hello = await ReadFrameAsync(stream, ct).ConfigureAwait(false);

        if (!string.Equals(hello.Type, "hello", StringComparison.Ordinal))
        {
            await WriteErrorAsync(stream, "invalid", "expected hello", ct).ConfigureAwait(false);
            return;
        }

        if (hello.Protocol != SyncMessage.ProtocolVersion)
        {
            await WriteErrorAsync(stream, "protocol", "unsupported protocol", ct).ConfigureAwait(false);
            return;
        }

        if (!SyncEntry.IsValidId(hello.DeviceId))
        {
            await WriteErrorAsync(stream, "invalid", "invalid deviceId", ct).ConfigureAwait(false);
            return;
        }

        var clientDeviceId = SyncEntry.NormalizeId(hello.DeviceId!);
        var clientDeviceName = NormalizeDeviceName(hello.DeviceName);

        var identity = _identityProvider();

        if (!SyncStore.IsValidCode(identity.Code))
        {
            await WriteErrorAsync(stream, "internal", "sync disabled", ct).ConfigureAwait(false);
            return;
        }

        var nonce = RandomNumberGenerator.GetBytes(16);
        await SyncFraming.WriteAsync(stream, SyncMessage.Challenge(nonce), ct).ConfigureAwait(false);

        var auth = await ReadFrameAsync(stream, ct).ConfigureAwait(false);

        if (!string.Equals(auth.Type, "auth", StringComparison.Ordinal))
        {
            await WriteErrorAsync(stream, "invalid", "expected auth", ct).ConfigureAwait(false);
            return;
        }

        var expectedMac = LanSyncHash.ComputeAuthMac(identity.Code, nonce, clientDeviceId);

        // 常量时间比较；格式不对也按认证失败处理。
        if (!LanSyncHash.FixedTimeEqualsHex(auth.Mac, expectedMac))
        {
            await WriteErrorAsync(stream, "auth", "sync code mismatch", ct).ConfigureAwait(false);
            return;
        }

        await SyncFraming.WriteAsync(
            stream, SyncMessage.Welcome(identity.DeviceId, identity.DeviceName), ct).ConfigureAwait(false);

        var snapshotMessage = await ReadFrameAsync(stream, ct).ConfigureAwait(false);

        if (string.Equals(snapshotMessage.Type, "abort", StringComparison.Ordinal)) return;

        if (!string.Equals(snapshotMessage.Type, "snapshot", StringComparison.Ordinal))
        {
            await WriteErrorAsync(stream, "invalid", "expected snapshot", ct).ConfigureAwait(false);
            return;
        }

        if (!WireConvert.TryToSnapshot(snapshotMessage, out _, out var snapshotError))
        {
            await WriteErrorAsync(stream, "invalid", snapshotError, ct).ConfigureAwait(false);
            return;
        }

        // 服务端只负责把自己的快照发过去；合并由客户端完成。
        var localSnapshot = await _repository.ReadSnapshotAsync(ct).ConfigureAwait(false);
        await SyncFraming.WriteAsync(stream, SyncMessage.Snapshot(localSnapshot), ct).ConfigureAwait(false);

        var command = await ReadFrameAsync(stream, ct).ConfigureAwait(false);

        if (string.Equals(command.Type, "abort", StringComparison.Ordinal))
        {
            Log.Information("同步会话被客户端中止: {Reason}", command.Reason);
            return;
        }

        if (!string.Equals(command.Type, "apply", StringComparison.Ordinal))
        {
            await WriteErrorAsync(stream, "invalid", "expected apply", ct).ConfigureAwait(false);
            return;
        }

        if (!LanSyncHash.IsHexHash(command.ExpectedSnapshotHash)
            || !LanSyncHash.IsHexHash(command.FinalSnapshotHash))
        {
            await WriteErrorAsync(stream, "invalid", "invalid snapshot hash", ct).ConfigureAwait(false);
            return;
        }

        if (!WireConvert.TryToActions(command.Actions, out var actions, out var actionError))
        {
            await WriteErrorAsync(stream, "invalid", actionError, ct).ConfigureAwait(false);
            return;
        }

        var apply = await _repository.ApplyChangesAsync(
            command.ExpectedSnapshotHash!,
            actions,
            command.FinalSnapshotHash!,
            ct).ConfigureAwait(false);

        switch (apply.Status)
        {
            case BillApplyStatus.SnapshotChanged:
                await WriteErrorAsync(stream, "snapshot-changed", apply.Message, ct).ConfigureAwait(false);
                return;

            case BillApplyStatus.Invalid:
                await WriteErrorAsync(stream, "invalid", apply.Message, ct).ConfigureAwait(false);
                return;

            case BillApplyStatus.Internal:
                await WriteErrorAsync(stream, "internal", apply.Message, ct).ConfigureAwait(false);
                return;
        }

        // apply.baseline 是协议要求携带的；缺失或损坏时从已校验的最终快照重建，
        // 不因为一个可推导的字段让整个已经成功的 apply 变成错误。
        if (!WireConvert.TryToBaseline(command.Baseline, out var baseline, out _))
        {
            var after = await _repository.ReadSnapshotAsync(ct).ConfigureAwait(false);
            baseline = MergeEngine.BaselineMap(after);
        }

        var applied = new PeerAppliedInfo(
            clientDeviceId,
            clientDeviceName,
            baseline,
            command.FinalSnapshotHash!,
            actions.Count);

        await _onApplied(applied).ConfigureAwait(false);

        await SyncFraming.WriteAsync(
            stream,
            SyncMessage.Applied(actions.Count, command.FinalSnapshotHash!),
            ct).ConfigureAwait(false);

        Log.Information("同步服务端应用完成: {Peer} {Count} 条动作", clientDeviceName, actions.Count);
    }

    private static string NormalizeDeviceName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "未知设备";

        return trimmed.Length <= SyncStore.MaxDeviceNameLength
            ? trimmed
            : trimmed[..SyncStore.MaxDeviceNameLength];
    }

    private static async Task<SyncMessage> ReadFrameAsync(NetworkStream stream, CancellationToken sessionToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ReadTimeoutSeconds));

        return await SyncFraming.ReadAsync(stream, cts.Token).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(
        NetworkStream stream, string code, string message, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await SyncFraming.WriteAsync(stream, SyncMessage.Error(code, message), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                       or ObjectDisposedException)
        {
            // 对方已经断开，错误消息发不出去也无所谓
        }
    }

    private static async Task SendBusyAndCloseAsync(TcpClient client)
    {
        try
        {
            client.NoDelay = true;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await SyncFraming.WriteAsync(
                client.GetStream(),
                SyncMessage.Error("busy", "another session is running"),
                cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                       or ObjectDisposedException)
        {
            // 对方已断开
        }
        finally
        {
            try
            {
                client.Close();
            }
            catch (ObjectDisposedException)
            {
                // 已关闭
            }
        }
    }

    private static Task TrySendErrorAsync(TcpClient client, string code, string message)
    {
        try
        {
            return WriteErrorAsync(
                client.GetStream(), code, message, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
                                       or InvalidOperationException)
        {
            return Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _sessionGate.Dispose();
    }
}
