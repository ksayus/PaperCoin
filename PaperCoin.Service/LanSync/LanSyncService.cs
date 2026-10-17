using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using Serilog;

namespace PaperCoin.Service.LanSync;

/// <summary>
/// 局域网同步的进程内单例。负责状态文件、TCP 服务端、UDP 发现、20 秒自动同步、
/// 手动同步、冲突保留与用户选择。所有网络操作都在后台异步执行；对外事件可能在
/// 任意线程触发，WPF 侧必须自行 Dispatcher 编组。
/// </summary>
public sealed class LanSyncService : IAsyncDisposable
{
    public const int AutoSyncIntervalSeconds = 20;
    public const int AutoSyncThrottleSeconds = 20;
    public const int DiscoveredStaleSeconds = 60;
    public const int ListenerPort = LanSyncServer.PreferredPort;
    public const int DiscoveryPort = LanSyncDiscovery.DiscoveryPort;

    private const string CodeAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private readonly BillRepository _repository;
    private readonly SyncStore _store;
    private readonly LanSyncClient _client;

    private readonly object _stateLock = new();
    private readonly object _statusLock = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private readonly ConcurrentDictionary<string, DiscoveredPeer> _discovered = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAutoAttempt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PeerEndpoint> _endpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SyncConflictSession> _pendingConflicts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ConflictChoice>> _choices =
        new(StringComparer.Ordinal);

    private SyncState _state = new();
    private CancellationTokenSource? _networkCts;
    private LanSyncServer? _server;
    private LanSyncDiscovery? _discovery;
    private Task? _autoLoop;
    private volatile bool _running;
    private volatile bool _discoveryFailed;
    private bool _started;
    private string _status = "已关闭";
    private string _lastMessage = "";

    public LanSyncService(BillRepository repository, SyncStore store)
    {
        _repository = repository;
        _store = store;
        _client = new LanSyncClient(repository);
    }

    // ---- 事件 ----

    public event EventHandler? StateChanged;
    public event EventHandler? StatusChanged;
    public event EventHandler? DiscoveredPeersChanged;
    public event EventHandler? PendingConflictsChanged;
    public event EventHandler<ConflictsDetectedEventArgs>? ConflictsDetected;
    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

    // ---- 只读状态 ----

    public bool IsRunning => _running;

    public int ListeningPort => _server?.Port ?? 0;

    public string StatusText
    {
        get
        {
            lock (_statusLock) return _status;
        }
    }

    public string LastMessage
    {
        get
        {
            lock (_statusLock) return _lastMessage;
        }
    }

    public SyncState GetState()
    {
        lock (_stateLock) return _state.Clone();
    }

    public IReadOnlyList<DiscoveredPeer> GetDiscoveredPeers()
        => _discovered.Values
            .OrderBy(p => p.DeviceName, StringComparer.Ordinal)
            .ThenBy(p => p.DeviceId, StringComparer.Ordinal)
            .ToList();

    public IReadOnlyList<SyncConflictSession> GetPendingConflicts()
        => _pendingConflicts.Values
            .OrderBy(p => p.DetectedAt)
            .ToList();

    public SyncConflictSession? GetPendingConflict(string peerId)
        => _pendingConflicts.TryGetValue(peerId, out var session) ? session : null;

    public string LastSyncText
        => SyncStore.FormatLastSync(GetState().LastSyncAt);

    // ---- 生命周期 ----

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_started) return;
            _started = true;

            try
            {
                var loaded = await _store.LoadAsync(ct).ConfigureAwait(false);

                lock (_stateLock) _state = loaded;

                RaiseStateChanged();
                await ApplyNetworkStateCoreAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "局域网同步启动失败");
                SetStatus("启动失败", "无法读取同步设置");
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_started) return;
            _started = false;

            await StopNetworkCoreAsync().ConfigureAwait(false);
            ClearConflictState();
            SetStatus("已关闭", "");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // ---- 设置 ----

    public async Task<SyncSettingsResult> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (enabled && !SyncStore.IsValidCode(GetState().Code))
            return SyncSettingsResult.Fail("请先生成或输入 6–12 位同步码");

        return await UpdateStateAsync(state =>
        {
            state.Enabled = enabled;
            return SyncSettingsResult.Ok(enabled ? "局域网同步已启用" : "局域网同步已关闭");
        }, ct).ConfigureAwait(false);
    }

    public async Task<SyncSettingsResult> SetAutoSyncAsync(bool enabled, CancellationToken ct = default)
        => await UpdateStateAsync(state =>
        {
            state.AutoSync = enabled;
            return SyncSettingsResult.Ok(enabled ? "自动同步已开启" : "自动同步已关闭");
        }, ct).ConfigureAwait(false);

    public async Task<SyncSettingsResult> SetCodeAsync(string code, CancellationToken ct = default)
    {
        var normalized = SyncStore.NormalizeCode(code);

        if (normalized.Length == 0)
        {
            return await UpdateStateAsync(state =>
            {
                state.Code = "";
                state.Enabled = false;
                return SyncSettingsResult.Ok("同步码已清空，局域网同步已关闭");
            }, ct).ConfigureAwait(false);
        }

        if (!SyncStore.IsValidCode(normalized))
            return SyncSettingsResult.Fail("同步码必须是 6–12 位大写字母或数字");

        return await UpdateStateAsync(state =>
        {
            state.Code = normalized;
            return SyncSettingsResult.Ok("同步码已保存");
        }, ct).ConfigureAwait(false);
    }

    public async Task<SyncSettingsResult> GenerateCodeAsync(CancellationToken ct = default)
    {
        var code = GenerateCode();
        var result = await SetCodeAsync(code, ct).ConfigureAwait(false);

        return result.Success
            ? SyncSettingsResult.Ok($"已生成同步码 {code}（还需打开“启用同步”）")
            : result;
    }

    public async Task<SyncSettingsResult> SetDeviceNameAsync(string name, CancellationToken ct = default)
    {
        var trimmed = (name ?? "").Trim();

        if (!SyncStore.IsValidDeviceName(trimmed))
            return SyncSettingsResult.Fail("设备名需要 1–48 个字符");

        return await UpdateStateAsync(state =>
        {
            state.DeviceName = trimmed;
            return SyncSettingsResult.Ok("设备名已保存");
        }, ct).ConfigureAwait(false);
    }

    public async Task<SyncSettingsResult> ForgetPeerAsync(string peerId, CancellationToken ct = default)
    {
        var normalized = TryNormalizePeerId(peerId);

        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var removed = await MutateStateAsync(state =>
            {
                var peer = state.Peers.FirstOrDefault(p =>
                    string.Equals(p.DeviceId, normalized, StringComparison.Ordinal));

                if (peer is null) return SyncSettingsResult.Fail("没有找到这个设备");

                state.Peers.Remove(peer);
                return SyncSettingsResult.Ok("已忘记该设备");
            }, ct).ConfigureAwait(false);

            if (!removed.Success) return removed;

            _endpoints.TryRemove(normalized, out _);
            _lastAutoAttempt.TryRemove(normalized, out _);
            _choices.TryRemove(normalized, out _);
            _pendingConflicts.TryRemove(normalized, out _);

            RaisePendingConflictsChanged();
            SetIdleStatus();
            return removed;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // ---- 同步 ----

    /// <summary>
    /// 手动同步。参数可以是已知 / 已发现设备的 Id（GUID），也可以是 <c>host:port</c>。
    /// 没有端口时按首选端口 47832 处理。
    /// </summary>
    public async Task<SyncSettingsResult> SyncNowAsync(string target, CancellationToken ct = default)
    {
        var state = GetState();

        if (!state.Enabled) return SyncSettingsResult.Fail("请先启用局域网同步");
        if (!SyncStore.IsValidCode(state.Code)) return SyncSettingsResult.Fail("同步码为空或不合法");

        var text = (target ?? "").Trim();
        if (text.Length == 0) return SyncSettingsResult.Fail("请输入对方地址或选择设备");

        if (Guid.TryParse(text, out _))
            return await SyncPeerAsync(text, ct).ConfigureAwait(false);

        if (!TryParseEndpoint(text, out var host, out var port, out var error))
            return SyncSettingsResult.Fail(error);

        return await RunSyncAsync(host, port, expectedPeerId: null, ct).ConfigureAwait(false);
    }

    public async Task<SyncSettingsResult> SyncPeerAsync(string peerId, CancellationToken ct = default)
    {
        var normalized = TryNormalizePeerId(peerId);
        var state = GetState();

        if (!state.Enabled) return SyncSettingsResult.Fail("请先启用局域网同步");
        if (!SyncStore.IsValidCode(state.Code)) return SyncSettingsResult.Fail("同步码为空或不合法");

        if (state.FindPeer(normalized) is null && _discovered.ContainsKey(normalized) is false)
            return SyncSettingsResult.Fail("没有找到这个设备，请等待自动发现或输入 host:port");

        if (!TryResolveEndpoint(normalized, out var host, out var port))
            return SyncSettingsResult.Fail("对方当前不在线，请等待自动发现或输入 host:port");

        return await RunSyncAsync(host, port, normalized, ct).ConfigureAwait(false);
    }

    /// <summary>用户对某台设备的冲突做出了选择：保存选择并立即重连重试。</summary>
    public async Task<SyncSettingsResult> ResolveConflictsAsync(
        string peerId,
        IReadOnlyDictionary<string, ConflictChoice> choices,
        CancellationToken ct = default)
    {
        var normalized = TryNormalizePeerId(peerId);

        if (!_pendingConflicts.TryRemove(normalized, out var session))
            return SyncSettingsResult.Fail("没有待处理的冲突");

        if (choices.Count == 0)
        {
            RaisePendingConflictsChanged();
            return SyncSettingsResult.Fail("请为每一处冲突选择保留哪一边");
        }

        _choices.AddOrUpdate(
            normalized,
            _ => new Dictionary<string, ConflictChoice>(choices, StringComparer.Ordinal),
            (_, existing) =>
            {
                var merged = new Dictionary<string, ConflictChoice>(existing, StringComparer.Ordinal);
                foreach (var (id, choice) in choices) merged[id] = choice;
                return merged;
            });

        RaisePendingConflictsChanged();
        SetStatus("正在重新同步…", $"按选择重试：{session.PeerName}");

        var result = await RunSyncAsync(
            session.Host,
            session.Port,
            normalized,
            ct).ConfigureAwait(false);

        if (result.Success) _choices.TryRemove(normalized, out _);

        return result;
    }

    /// <summary>放弃这次冲突处理。协议要求取消不得改变任何一侧的数据。</summary>
    public SyncSettingsResult CancelConflicts(string peerId)
    {
        var normalized = TryNormalizePeerId(peerId);

        _pendingConflicts.TryRemove(normalized, out _);
        _choices.TryRemove(normalized, out _);

        RaisePendingConflictsChanged();
        SetStatus("已取消冲突处理", "本机与对方都没有写入任何更改");

        return SyncSettingsResult.Ok("已取消，未应用任何更改");
    }

    // ---- 状态更新 ----

    private async Task<SyncSettingsResult> UpdateStateAsync(
        Func<SyncState, SyncSettingsResult> mutate,
        CancellationToken ct)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await MutateStateAsync(mutate, ct).ConfigureAwait(false);
            if (!result.Success) return result;

            if (_started)
                await ApplyNetworkStateCoreAsync(ct).ConfigureAwait(false);
            else
                SetIdleStatus();

            return result;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<SyncSettingsResult> MutateStateAsync(
        Func<SyncState, SyncSettingsResult> mutate,
        CancellationToken ct)
    {
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SyncState draft;

            lock (_stateLock) draft = _state.Clone();

            var result = mutate(draft);
            if (!result.Success) return result;

            draft = SyncStore.Normalize(draft);

            await _store.SaveAsync(draft, ct).ConfigureAwait(false);

            lock (_stateLock) _state = draft;

            RaiseStateChanged();
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存同步设置失败");
            return SyncSettingsResult.Fail("保存同步设置失败");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task ApplyNetworkStateCoreAsync(CancellationToken ct)
    {
        var state = GetState();
        var shouldRun = state.Enabled && SyncStore.IsValidCode(state.Code);

        if (shouldRun)
        {
            if (_running)
            {
                SetIdleStatus();
                return;
            }

            await StartNetworkCoreAsync(ct).ConfigureAwait(false);
            return;
        }

        if (_running || _server is not null || _discovery is not null)
            await StopNetworkCoreAsync().ConfigureAwait(false);

        SetIdleStatus();
    }

    private async Task StartNetworkCoreAsync(CancellationToken ct)
    {
        var cts = new CancellationTokenSource();
        _networkCts = cts;

        _discovered.Clear();
        _lastAutoAttempt.Clear();
        _discoveryFailed = false;

        var server = new LanSyncServer(_repository, GetIdentity, OnPeerAppliedAsync);

        try
        {
            await server.StartAsync(ListenerPort, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            Log.Warning(ex, "局域网同步 TCP 服务启动失败");
            await server.DisposeAsync().ConfigureAwait(false);
            _networkCts = null;
            cts.Dispose();
            _running = false;

            SetStatus("启动失败", "TCP 端口无法监听，请查看日志");
            return;
        }

        _server = server;

        var discovery = new LanSyncDiscovery(
            GetIdentity,
            OnPeerDiscovered,
            message => SetStatus("发现服务异常", message));

        try
        {
            await discovery.StartAsync(server.Port, cts.Token).ConfigureAwait(false);
            _discovery = discovery;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            Log.Warning(ex, "局域网同步 UDP 发现启动失败（手动 host:port 仍可用）");
            await discovery.DisposeAsync().ConfigureAwait(false);
            _discovery = null;
            _discoveryFailed = true;
        }

        _running = true;
        _autoLoop = Task.Run(() => AutoLoopAsync(cts.Token));

        RaiseDiscoveredPeersChanged();
        SetIdleStatus();
    }

    private async Task StopNetworkCoreAsync()
    {
        _running = false;
        _discoveryFailed = false;

        var cts = _networkCts;
        _networkCts = null;

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已停止
        }

        var discovery = _discovery;
        _discovery = null;

        if (discovery is not null)
            await discovery.StopAsync().ConfigureAwait(false);

        var server = _server;
        _server = null;

        if (server is not null)
            await server.DisposeAsync().ConfigureAwait(false);

        var loop = _autoLoop;
        _autoLoop = null;

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // 退出阶段不阻塞
            }
        }

        cts?.Dispose();

        _discovered.Clear();
        _lastAutoAttempt.Clear();
        RaiseDiscoveredPeersChanged();
        ClearConflictState();
    }

    private void SetIdleStatus()
    {
        var state = GetState();

        if (!state.Enabled)
        {
            SetStatus("已关闭", "");
            return;
        }

        var auto = state.AutoSync ? "自动同步开" : "仅手动";
        var port = _server?.Port ?? 0;
        var discovery = _discoveryFailed ? " · 发现端口 47831 不可用（可手动 host:port）" : "";

        SetStatus(port > 0 ? $"已启用 · TCP {port} · {auto}{discovery}" : $"已启用 · {auto}{discovery}", "");
    }

    // ---- 自动同步 ----

    private async Task AutoLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(AutoSyncIntervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var state = GetState();

                if (!state.Enabled || !state.AutoSync || !SyncStore.IsValidCode(state.Code))
                    continue;

                PruneDiscovered();

                foreach (var peer in GetDiscoveredPeers())
                {
                    if (ct.IsCancellationRequested) return;

                    if (state.FindPeer(peer.DeviceId) is null) continue;
                    if (peer.LastSeen < DateTimeOffset.UtcNow.AddSeconds(-DiscoveredStaleSeconds)) continue;
                    if (string.CompareOrdinal(state.DeviceId, peer.DeviceId) >= 0) continue;
                    if (_pendingConflicts.ContainsKey(peer.DeviceId)) continue;
                    if (!TryThrottle(peer.DeviceId)) continue;

                    await RunAutoSyncAsync(peer, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "自动同步循环退出");
            SetStatus("自动同步已停止", ex.Message);
        }
    }

    private async Task RunAutoSyncAsync(DiscoveredPeer peer, CancellationToken ct)
    {
        try
        {
            await RunSyncAsync(peer.Host, peer.TcpPort, peer.DeviceId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "自动同步 {Peer} 失败", peer.DeviceName);
        }
    }

    private void OnPeerDiscovered(DiscoveredPeer peer, bool isOffer)
    {
        var changed = false;

        if (_discovered.TryGetValue(peer.DeviceId, out var existing))
        {
            changed = !string.Equals(existing.DeviceName, peer.DeviceName, StringComparison.Ordinal)
                      || !string.Equals(existing.Host, peer.Host, StringComparison.Ordinal)
                      || existing.TcpPort != peer.TcpPort;
        }
        else
        {
            changed = true;
        }

        _discovered[peer.DeviceId] = peer;
        _endpoints[peer.DeviceId] = new PeerEndpoint(peer.Host, peer.TcpPort);

        if (changed) RaiseDiscoveredPeersChanged();

        if (!isOffer) return;

        var state = GetState();

        if (!state.Enabled || !state.AutoSync) return;
        if (!SyncStore.IsValidCode(state.Code)) return;

        // 已知设备 + 本机 Id 更小才主动连：另一侧等待，避免双方同时连。
        if (state.FindPeer(peer.DeviceId) is null) return;
        if (string.CompareOrdinal(state.DeviceId, peer.DeviceId) >= 0) return;
        if (_pendingConflicts.ContainsKey(peer.DeviceId)) return;
        if (!TryThrottle(peer.DeviceId)) return;

        var token = _networkCts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => RunAutoSyncAsync(peer, token), token);
    }

    private bool TryThrottle(string peerId)
    {
        var now = DateTimeOffset.UtcNow;

        if (_lastAutoAttempt.TryGetValue(peerId, out var last)
            && now - last < TimeSpan.FromSeconds(AutoSyncThrottleSeconds))
            return false;

        _lastAutoAttempt[peerId] = now;
        return true;
    }

    private void PruneDiscovered()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-DiscoveredStaleSeconds);
        var changed = false;

        foreach (var (id, peer) in _discovered)
        {
            if (peer.LastSeen >= cutoff) continue;

            if (_discovered.TryRemove(id, out _))
            {
                changed = true;

                // 保留 _endpoints，这样用户还能按 Id 手动重试上次的地址。
            }
        }

        if (changed) RaiseDiscoveredPeersChanged();
    }

    // ---- 会话 ----

    private async Task<SyncSettingsResult> RunSyncAsync(
        string host,
        int port,
        string? expectedPeerId,
        CancellationToken ct)
    {
        if (!_sessionGate.Wait(0))
        {
            SetStatus("正在同步…", "已有同步会话在进行中");
            return SyncSettingsResult.Fail("已有同步会话在进行中");
        }

        try
        {
            var state = GetState();

            if (!state.Enabled) return SyncSettingsResult.Fail("请先启用局域网同步");
            if (!SyncStore.IsValidCode(state.Code)) return SyncSettingsResult.Fail("同步码为空或不合法");

            if (expectedPeerId is not null) _lastAutoAttempt[expectedPeerId] = DateTimeOffset.UtcNow;

            var peerName = state.FindPeer(expectedPeerId ?? "")?.DeviceName;

            if (string.IsNullOrEmpty(peerName)
                && _discovered.TryGetValue(expectedPeerId ?? "", out var discoveredPeer))
                peerName = discoveredPeer.DeviceName;

            if (string.IsNullOrEmpty(peerName))
                peerName = expectedPeerId ?? $"{host}:{port}";

            SetStatus("正在同步…", $"正在与 {peerName} 同步");

            var options = new LanSyncClientOptions(
                host,
                port,
                state.DeviceId,
                state.DeviceName,
                state.Code,
                _server?.Port ?? 0,
                Baseline: new Dictionary<string, string>(StringComparer.Ordinal),
                ExpectedPeerId: expectedPeerId,
                BaselineLookup: GetBaseline,
                ChoicesLookup: GetChoices);

            var result = await _client.SyncAsync(options, ct).ConfigureAwait(false);

            if (result.ServerDeviceId.Length > 0)
                _endpoints[result.ServerDeviceId] = new PeerEndpoint(host, port);

            if (result.HasConflicts)
            {
                var unresolved = result.Conflicts.Where(c => !c.IsResolved).ToList();

                if (unresolved.Count > 0 && result.ServerDeviceId.Length > 0)
                {
                    var session = new SyncConflictSession
                    {
                        PeerId = result.ServerDeviceId,
                        PeerName = result.ServerDeviceName.Length > 0
                            ? result.ServerDeviceName
                            : peerName,
                        Host = host,
                        Port = port,
                        Conflicts = unresolved,
                        DetectedAt = DateTimeOffset.UtcNow
                    };

                    _pendingConflicts[result.ServerDeviceId] = session;
                    RaisePendingConflictsChanged();

                    SetStatus("发现冲突", $"与 {session.PeerName} 有 {unresolved.Count} 处冲突，请选择保留哪一边");
                    RaiseConflictsDetected(session);

                    return SyncSettingsResult.Fail(
                        $"发现 {unresolved.Count} 处冲突，请选择保留本机还是对方");
                }

                return SyncSettingsResult.Fail("同步冲突但无法确定对方设备");
            }

            if (!result.Success)
            {
                var message = DescribeError(result.ErrorCode, result.ErrorMessage);
                SetStatus("同步失败", message);
                return SyncSettingsResult.Fail(message);
            }

            await RecordPeerAsync(
                result.ServerDeviceId,
                result.ServerDeviceName,
                result.Baseline).ConfigureAwait(false);

            _pendingConflicts.TryRemove(result.ServerDeviceId, out _);
            _choices.TryRemove(result.ServerDeviceId, out _);
            RaisePendingConflictsChanged();

            var summary = result.AppliedCount > 0 || result.LocalActionCount > 0
                ? $"同步完成：对方 {result.AppliedCount} 条，本机 {result.LocalActionCount} 条变更"
                : "同步完成：两边已经一致";

            SetStatus("同步完成", summary);

            RaiseSyncCompleted(new SyncCompletedEventArgs
            {
                PeerId = result.ServerDeviceId,
                PeerName = result.ServerDeviceName.Length > 0 ? result.ServerDeviceName : peerName,
                FinalSnapshotHash = result.FinalSnapshotHash,
                AppliedCount = result.AppliedCount,
                LocalActionCount = result.LocalActionCount
            });

            return SyncSettingsResult.Ok(summary);
        }
        catch (OperationCanceledException)
        {
            SetStatus("同步已取消", "");
            return SyncSettingsResult.Fail("同步已取消");
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>服务端成功 apply 后的回调：更新该设备的基线，然后服务端才回 applied。</summary>
    private async Task OnPeerAppliedAsync(PeerAppliedInfo info)
    {
        try
        {
            await RecordPeerAsync(info.DeviceId, info.DeviceName, info.Baseline).ConfigureAwait(false);

            SetStatus("同步完成", $"已接收来自 {info.DeviceName} 的 {info.ActionCount} 条变更");

            RaiseSyncCompleted(new SyncCompletedEventArgs
            {
                PeerId = info.DeviceId,
                PeerName = info.DeviceName,
                FinalSnapshotHash = info.FinalSnapshotHash,
                AppliedCount = info.ActionCount,
                LocalActionCount = 0
            });
        }
        catch (Exception ex)
        {
            // 不能把“账单已经落盘”的成功会话变成错误：状态保存失败只记日志。
            Log.Warning(ex, "记录对端基线失败: {Peer}", info.DeviceId);
        }
    }

    private async Task RecordPeerAsync(
        string deviceId,
        string deviceName,
        IReadOnlyDictionary<string, string> baseline)
    {
        if (!Guid.TryParse(deviceId, out var peerId)) return;

        var normalizedId = peerId.ToString("D");
        var name = string.IsNullOrWhiteSpace(deviceName) ? "未知设备" : deviceName.Trim();

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SyncState draft;

            lock (_stateLock) draft = _state.Clone();

            var peer = draft.Peers.FirstOrDefault(p =>
                string.Equals(p.DeviceId, normalizedId, StringComparison.Ordinal));

            if (peer is null)
            {
                peer = new SyncPeer { DeviceId = normalizedId };
                draft.Peers.Add(peer);
            }

            peer.DeviceName = name;
            peer.LastSyncAt = SyncStore.NowUnixMilliseconds();
            peer.Baseline = new Dictionary<string, string>(baseline, StringComparer.Ordinal);

            draft = SyncStore.Normalize(draft);

            await _store.SaveAsync(draft).ConfigureAwait(false);

            lock (_stateLock) _state = draft;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存对端同步基线失败: {Peer}", normalizedId);
        }
        finally
        {
            _stateGate.Release();
        }

        RaiseStateChanged();
    }

    private LanSyncIdentity GetIdentity()
    {
        lock (_stateLock) return new LanSyncIdentity(_state.DeviceId, _state.DeviceName, _state.Code);
    }

    private IReadOnlyDictionary<string, string> GetBaseline(string deviceId)
    {
        lock (_stateLock)
        {
            var peer = _state.FindPeer(deviceId);

            return peer is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(peer.Baseline, StringComparer.Ordinal);
        }
    }

    private IReadOnlyDictionary<string, ConflictChoice>? GetChoices(string deviceId)
        => _choices.TryGetValue(deviceId, out var choices)
            ? new Dictionary<string, ConflictChoice>(choices, StringComparer.Ordinal)
            : null;

    // ---- 工具 ----

    /// <summary>生成 6–12 位 A-Z0-9 同步码；默认 8 位。</summary>
    public static string GenerateCode(int length = 8)
    {
        if (length is < SyncStore.MinCodeLength or > SyncStore.MaxCodeLength)
            length = 8;

        var chars = new char[length];

        for (int i = 0; i < length; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];

        return new string(chars);
    }

    public static bool TryParseEndpoint(
        string? text,
        out string host,
        out int port,
        out string error)
    {
        host = "";
        port = ListenerPort;
        error = "";

        var value = (text ?? "").Trim();

        if (value.Length == 0)
        {
            error = "请输入对方地址";
            return false;
        }

        if (value.StartsWith('['))
        {
            // [IPv6]:port 或 [IPv6]
            int close = value.IndexOf(']');

            if (close < 0)
            {
                error = "地址格式不正确";
                return false;
            }

            host = value[1..close];

            if (value.Length > close + 1)
            {
                if (value[close + 1] != ':' || !int.TryParse(value[(close + 2)..], out port))
                {
                    error = "端口格式不正确";
                    return false;
                }
            }
        }
        else
        {
            int colon = value.LastIndexOf(':');

            if (colon > 0 && value.IndexOf(':') == colon)
            {
                host = value[..colon].Trim();

                if (!int.TryParse(value[(colon + 1)..], out port))
                {
                    error = "端口格式不正确";
                    return false;
                }
            }
            else
            {
                host = value;
                port = ListenerPort;
            }
        }

        if (host.Length == 0)
        {
            error = "请输入对方地址";
            return false;
        }

        if (port is <= 0 or > 65535)
        {
            error = "端口必须在 1–65535 之间";
            return false;
        }

        return true;
    }

    private bool TryResolveEndpoint(string peerId, out string host, out int port)
    {
        if (_discovered.TryGetValue(peerId, out var discovered))
        {
            host = discovered.Host;
            port = discovered.TcpPort;
            return true;
        }

        if (_endpoints.TryGetValue(peerId, out var cached))
        {
            host = cached.Host;
            port = cached.Port;
            return true;
        }

        host = "";
        port = 0;
        return false;
    }

    private static string TryNormalizePeerId(string peerId)
        => Guid.TryParse(peerId, out var id) ? id.ToString("D") : (peerId ?? "").Trim();

    private static string DescribeError(string code, string fallback) => code switch
    {
        "auth" => "同步码不一致",
        "protocol" => "协议版本不兼容",
        "busy" => "对方正在同步，请稍后再试",
        "snapshot-changed" => "账单在同步过程中发生变化，请重试",
        "invalid" => "对方返回的数据不合法",
        "internal" => "同步内部错误",
        "network" => "无法连接对方设备",
        "timeout" => "连接超时或对方无响应",
        "canceled" => "同步已取消",
        _ => string.IsNullOrWhiteSpace(fallback) ? "同步失败" : fallback
    };

    private void ClearConflictState()
    {
        _pendingConflicts.Clear();
        _choices.Clear();
        RaisePendingConflictsChanged();
    }

    // ---- 事件派发 ----

    private void SetStatus(string status, string message)
    {
        lock (_statusLock)
        {
            _status = status;
            _lastMessage = message;
        }

        RaiseStatusChanged();
    }

    private void RaiseStateChanged() => Invoke(StateChanged, EventArgs.Empty);

    private void RaiseStatusChanged() => Invoke(StatusChanged, EventArgs.Empty);

    private void RaiseDiscoveredPeersChanged() => Invoke(DiscoveredPeersChanged, EventArgs.Empty);

    private void RaisePendingConflictsChanged() => Invoke(PendingConflictsChanged, EventArgs.Empty);

    private void RaiseConflictsDetected(SyncConflictSession session)
        => Invoke(ConflictsDetected, new ConflictsDetectedEventArgs { Session = session });

    private void RaiseSyncCompleted(SyncCompletedEventArgs args) => Invoke(SyncCompleted, args);

    private void Invoke(EventHandler? handlers, EventArgs args)
    {
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, args);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "局域网同步事件处理失败");
            }
        }
    }

    private void Invoke<T>(EventHandler<T>? handlers, T args)
        where T : EventArgs
    {
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)handler)(this, args);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "局域网同步事件处理失败");
            }
        }
    }

    private readonly record struct PeerEndpoint(string Host, int Port);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        _sessionGate.Dispose();
        _lifecycleGate.Dispose();
        _stateGate.Dispose();
    }
}
