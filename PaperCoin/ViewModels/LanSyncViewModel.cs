using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaperCoin.Service.LanSync;

namespace PaperCoin.ViewModels;

/// <summary>已配对设备的一行。</summary>
public sealed class KnownPeerRow
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Detail { get; init; }
}

/// <summary>UDP 发现到、但还没配对（或还没同步成功）的设备。</summary>
public sealed class DiscoveredPeerRow
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string Endpoint { get; init; }
}

/// <summary>
/// 同步窗口的 ViewModel。LanSyncService 的事件来自网络线程，这里统一编组回
/// Dispatcher 后再改 ObservableCollection / 绑定属性。
/// </summary>
public sealed class LanSyncViewModel : ObservableObject, IDisposable
{
    private readonly LanSyncService _service;
    private readonly Dispatcher _dispatcher;

    private bool _applyingState;
    private bool _codeDirty;
    private bool _deviceNameDirty;
    private bool _disposed;

    public LanSyncViewModel(LanSyncService service)
    {
        _service = service;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        ToggleEnabledCommand = new AsyncRelayCommand(ToggleEnabledAsync);
        ToggleAutoSyncCommand = new AsyncRelayCommand(ToggleAutoSyncAsync);
        GenerateCodeCommand = new AsyncRelayCommand(GenerateCodeAsync);
        SaveCodeCommand = new AsyncRelayCommand(SaveCodeAsync);
        SaveDeviceNameCommand = new AsyncRelayCommand(SaveDeviceNameAsync);
        SyncManualCommand = new AsyncRelayCommand(SyncManualAsync);
        SyncPeerCommand = new AsyncRelayCommand<KnownPeerRow?>(SyncPeerAsync);
        SyncDiscoveredCommand = new AsyncRelayCommand<DiscoveredPeerRow?>(SyncDiscoveredAsync);
        ForgetPeerCommand = new AsyncRelayCommand<KnownPeerRow?>(ForgetPeerAsync);

        _service.StateChanged += OnStateChanged;
        _service.StatusChanged += OnStatusChanged;
        _service.DiscoveredPeersChanged += OnDiscoveredChanged;

        RefreshAll();
    }

    // ---- 命令 ----

    public IAsyncRelayCommand ToggleEnabledCommand { get; }
    public IAsyncRelayCommand ToggleAutoSyncCommand { get; }
    public IAsyncRelayCommand GenerateCodeCommand { get; }
    public IAsyncRelayCommand SaveCodeCommand { get; }
    public IAsyncRelayCommand SaveDeviceNameCommand { get; }
    public IAsyncRelayCommand SyncManualCommand { get; }
    public IAsyncRelayCommand<KnownPeerRow?> SyncPeerCommand { get; }
    public IAsyncRelayCommand<DiscoveredPeerRow?> SyncDiscoveredCommand { get; }
    public IAsyncRelayCommand<KnownPeerRow?> ForgetPeerCommand { get; }

    // ---- 状态 ----

    private bool _isEnabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (!SetProperty(ref _isEnabled, value)) return;
            OnPropertyChanged(nameof(EnableHint));
        }
    }

    private bool _autoSyncEnabled;

    public bool AutoSyncEnabled
    {
        get => _autoSyncEnabled;
        private set => SetProperty(ref _autoSyncEnabled, value);
    }

    private string _code = "";

    public string Code
    {
        get => _code;
        set
        {
            if (!SetProperty(ref _code, value)) return;
            if (!_applyingState) _codeDirty = true;
        }
    }

    private string _deviceName = "";

    public string DeviceName
    {
        get => _deviceName;
        set
        {
            if (!SetProperty(ref _deviceName, value)) return;
            if (!_applyingState) _deviceNameDirty = true;
        }
    }

    private string _statusText = "";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _lastMessage = "";

    public string LastMessage
    {
        get => _lastMessage;
        private set => SetProperty(ref _lastMessage, value);
    }

    private string _lastSyncText = "";

    public string LastSyncText
    {
        get => _lastSyncText;
        private set => SetProperty(ref _lastSyncText, value);
    }

    private string _manualEndpoint = "";

    public string ManualEndpoint
    {
        get => _manualEndpoint;
        set => SetProperty(ref _manualEndpoint, value);
    }

    private string _errorText = "";

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (!SetProperty(ref _errorText, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    private bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(BusyText));
        }
    }

    public string BusyText => IsBusy ? "正在处理…" : "";

    public string EnableHint => IsEnabled ? "已启用：本机监听并按配对设备自动同步" : "已关闭：只保留设置，不监听也不自动同步";

    public ObservableCollection<KnownPeerRow> KnownPeers { get; } = [];

    public ObservableCollection<DiscoveredPeerRow> DiscoveredPeers { get; } = [];

    public int ListeningPort => _service.ListeningPort;

    public string ListenerText => _service.IsRunning && ListeningPort > 0
        ? $"本机 TCP 端口 {ListeningPort} · 发现端口 {LanSyncService.DiscoveryPort}"
        : "未监听";

    // ---- 命令实现 ----

    private async Task ToggleEnabledAsync()
    {
        await RunAsync(async () =>
        {
            var state = _service.GetState();

            if (state.Enabled)
                return await _service.SetEnabledAsync(false);

            if (!SyncStore.IsValidCode(SyncStore.NormalizeCode(Code)))
                return SyncSettingsResult.Fail("请先生成或输入 6–12 位同步码");

            if (_codeDirty)
            {
                var saved = await _service.SetCodeAsync(Code);
                if (!saved.Success) return saved;
                _codeDirty = false;
            }

            return await _service.SetEnabledAsync(true);
        });
    }

    private async Task ToggleAutoSyncAsync()
    {
        await RunAsync(async () =>
            await _service.SetAutoSyncAsync(!_service.GetState().AutoSync));
    }

    private async Task GenerateCodeAsync()
    {
        await RunAsync(async () =>
        {
            var result = await _service.GenerateCodeAsync();
            if (result.Success) _codeDirty = false;
            return result;
        });
    }

    private async Task SaveCodeAsync()
    {
        await RunAsync(async () =>
        {
            var result = await _service.SetCodeAsync(Code);
            if (result.Success) _codeDirty = false;
            return result;
        });
    }

    private async Task SaveDeviceNameAsync()
    {
        await RunAsync(async () =>
        {
            var result = await _service.SetDeviceNameAsync(DeviceName);
            if (result.Success) _deviceNameDirty = false;
            return result;
        });
    }

    private async Task SyncPeerAsync(KnownPeerRow? row)
    {
        if (row is null) return;
        await RunAsync(() => _service.SyncPeerAsync(row.DeviceId));
    }

    private async Task SyncDiscoveredAsync(DiscoveredPeerRow? row)
    {
        if (row is null) return;
        await RunAsync(() => _service.SyncPeerAsync(row.DeviceId));
    }

    private async Task ForgetPeerAsync(KnownPeerRow? row)
    {
        if (row is null) return;
        await RunAsync(() => _service.ForgetPeerAsync(row.DeviceId));
    }

    private async Task SyncManualAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualEndpoint))
        {
            ErrorText = "请输入 host:port，例如 192.168.1.20:47832";
            return;
        }

        await RunAsync(() => _service.SyncNowAsync(ManualEndpoint));
    }

    private async Task RunAsync(Func<Task<SyncSettingsResult>> action)
    {
        if (IsBusy) return;

        IsBusy = true;
        ErrorText = "";

        try
        {
            var result = await action();

            if (!result.Success) ErrorText = result.Message;
        }
        catch (Exception ex)
        {
            ErrorText = "操作失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
            RefreshAll();
        }
    }

    // ---- 事件 ----

    private void OnStateChanged(object? sender, EventArgs e) => Post(RefreshAll);

    private void OnStatusChanged(object? sender, EventArgs e) => Post(RefreshStatus);

    private void OnDiscoveredChanged(object? sender, EventArgs e) => Post(RefreshDiscovered);

    private void Post(Action action)
    {
        if (_disposed) return;

        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    // ---- 刷新 ----

    private void RefreshAll()
    {
        if (_disposed) return;

        var state = _service.GetState();

        _applyingState = true;
        IsEnabled = state.Enabled;
        AutoSyncEnabled = state.AutoSync;
        if (!_codeDirty) Code = state.Code;
        if (!_deviceNameDirty) DeviceName = state.DeviceName;
        _applyingState = false;

        RefreshStatus();
        RefreshKnownPeers(state);
        RefreshDiscovered();
        OnPropertyChanged(nameof(ListenerText));
    }

    private void RefreshStatus()
    {
        if (_disposed) return;

        StatusText = _service.StatusText;
        LastMessage = _service.LastMessage;
        LastSyncText = "上次同步：" + _service.LastSyncText;
        OnPropertyChanged(nameof(ListenerText));
    }

    private void RefreshKnownPeers(SyncState state)
    {
        KnownPeers.Clear();

        foreach (var peer in state.Peers.OrderBy(p => p.DeviceName, StringComparer.Ordinal))
        {
            KnownPeers.Add(new KnownPeerRow
            {
                DeviceId = peer.DeviceId,
                DeviceName = peer.DeviceName,
                Detail = $"上次同步：{SyncStore.FormatLastSync(peer.LastSyncAt)} · 基线 {peer.Baseline.Count} 条"
            });
        }
    }

    private void RefreshDiscovered()
    {
        if (_disposed) return;

        DiscoveredPeers.Clear();

        var known = _service.GetState().Peers
            .Select(p => p.DeviceId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var peer in _service.GetDiscoveredPeers())
        {
            // 已配对设备在上一节展示；这里只列还陌生的设备，避免同一个设备出现两次。
            if (known.Contains(peer.DeviceId)) continue;

            DiscoveredPeers.Add(new DiscoveredPeerRow
            {
                DeviceId = peer.DeviceId,
                DeviceName = peer.DeviceName,
                Endpoint = peer.EndpointText
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _service.StateChanged -= OnStateChanged;
        _service.StatusChanged -= OnStatusChanged;
        _service.DiscoveredPeersChanged -= OnDiscoveredChanged;
    }
}
