using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Serilog;

namespace PaperCoin.Service.LanSync;

/// <summary>
/// UDP 发现。固定端口 47831，每 2 秒向所有非回环 IPv4 接口的广播地址与 255.255.255.255
/// 发 discover；收到 discover 时只回一条 offer，不触发进一步的 discover（不递归）。
/// </summary>
public sealed class LanSyncDiscovery : IAsyncDisposable
{
    public const int DiscoveryPort = 47831;
    public const int BroadcastIntervalSeconds = 2;
    private const int MaxDatagramBytes = 16 * 1024;

    private readonly Func<LanSyncIdentity> _identityProvider;
    private readonly Action<DiscoveredPeer, bool> _onPeer;
    private readonly Action<string>? _onError;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _broadcastLoop;
    private int _tcpPort;

    /// <param name="onPeer">参数为发现到的设备，以及这条信息是否来自 offer。</param>
    public LanSyncDiscovery(
        Func<LanSyncIdentity> identityProvider,
        Action<DiscoveredPeer, bool> onPeer,
        Action<string>? onError = null)
    {
        _identityProvider = identityProvider;
        _onPeer = onPeer;
        _onError = onError;
    }

    public bool IsRunning => _udp is not null;

    public Task StartAsync(int tcpPort, CancellationToken ct = default)
    {
        if (_udp is not null) return Task.CompletedTask;

        _tcpPort = tcpPort;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var udp = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };

        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        }
        catch
        {
            udp.Dispose();
            throw;
        }

        _udp = udp;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(udp, _cts.Token));
        _broadcastLoop = Task.Run(() => BroadcastLoopAsync(udp, _cts.Token));

        Log.Information("局域网同步 UDP 发现监听 {Port}", DiscoveryPort);
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
            // 已停止
        }

        try
        {
            _udp?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已停止
        }

        _udp = null;

        foreach (var task in new[] { _receiveLoop, _broadcastLoop })
        {
            if (task is null) continue;

            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                // 退出阶段不阻塞
            }
        }

        _receiveLoop = null;
        _broadcastLoop = null;
        cts?.Dispose();
    }

    private async Task BroadcastLoopAsync(UdpClient udp, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(BroadcastIntervalSeconds));

        try
        {
            // 先立即广播一次，别让用户等一个周期。
            await SendDiscoverAsync(udp, ct).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await SendDiscoverAsync(udp, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (ObjectDisposedException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UDP 发现广播循环退出");
            _onError?.Invoke("局域网发现已停止");
        }
    }

    private async Task SendDiscoverAsync(UdpClient udp, CancellationToken ct)
    {
        var identity = _identityProvider();
        var message = SyncMessage.Discover(identity.DeviceId, identity.DeviceName, _tcpPort);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SyncMessage.JsonOptions);

        foreach (var endpoint in BroadcastEndpoints())
        {
            try
            {
                await udp.SendAsync(payload, endpoint, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // 某个网卡不可用不影响其它网卡
            }
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;

            try
            {
                result = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;

                Log.Warning(ex, "UDP 发现接收失败");
                continue;
            }

            if (result.Buffer.Length is 0 or > MaxDatagramBytes) continue;

            SyncMessage? message;

            try
            {
                message = JsonSerializer.Deserialize<SyncMessage>(result.Buffer, SyncMessage.JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (message is null) continue;
            if (message.Protocol is not null && message.Protocol != SyncMessage.ProtocolVersion) continue;
            if (!SyncEntry.IsValidId(message.DeviceId)) continue;
            if (message.TcpPort is not (>= 1 and <= 65535)) continue;

            var identity = _identityProvider();

            if (string.Equals(SyncEntry.NormalizeId(message.DeviceId!), identity.DeviceId, StringComparison.Ordinal))
                continue;

            var peer = new DiscoveredPeer
            {
                DeviceId = SyncEntry.NormalizeId(message.DeviceId!),
                DeviceName = NormalizeName(message.DeviceName),
                Host = result.RemoteEndPoint.Address.ToString(),
                TcpPort = message.TcpPort!.Value,
                LastSeen = DateTimeOffset.UtcNow
            };

            var isOffer = string.Equals(message.Type, "offer", StringComparison.Ordinal);
            var isDiscover = string.Equals(message.Type, "discover", StringComparison.Ordinal);

            if (!isOffer && !isDiscover) continue;

            if (isDiscover)
            {
                // 只回 offer，不发 discover；否则两台设备会无限互相触发。
                await SendOfferAsync(udp, result.RemoteEndPoint, identity, ct).ConfigureAwait(false);
            }

            try
            {
                _onPeer(peer, isOffer);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "处理发现到的设备失败");
            }
        }
    }

    private async Task SendOfferAsync(
        UdpClient udp,
        IPEndPoint target,
        LanSyncIdentity identity,
        CancellationToken ct)
    {
        var message = SyncMessage.Offer(identity.DeviceId, identity.DeviceName, _tcpPort);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SyncMessage.JsonOptions);

        try
        {
            await udp.SendAsync(payload, target, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // 对方刚好消失或本机正在停止
        }
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "未知设备";

        return trimmed.Length <= SyncStore.MaxDeviceNameLength
            ? trimmed
            : trimmed[..SyncStore.MaxDeviceNameLength];
    }

    /// <summary>
    /// 全部非回环 IPv4 接口的广播地址，外加 255.255.255.255。
    /// 每次广播重新枚举，网卡切换后不用重启同步。
    /// </summary>
    private static IEnumerable<IPEndPoint> BroadcastEndpoints()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var endpoints = new List<IPEndPoint>();

        void Add(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return;
            if (!seen.Add(address.ToString())) return;

            endpoints.Add(new IPEndPoint(address, DiscoveryPort));
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (!nic.Supports(NetworkInterfaceComponent.IPv4)) continue;

                IPInterfaceProperties properties;

                try
                {
                    properties = nic.GetIPProperties();
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(unicast.Address)) continue;

                    var mask = unicast.IPv4Mask;
                    if (mask is null) continue;

                    var ipBytes = unicast.Address.GetAddressBytes();
                    var maskBytes = mask.GetAddressBytes();

                    if (ipBytes.Length != 4 || maskBytes.Length != 4) continue;

                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                        broadcast[i] = (byte)(ipBytes[i] | ~maskBytes[i]);

                    Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Log.Warning(ex, "枚举网络接口失败");
        }

        Add(IPAddress.Broadcast);
        return endpoints;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
