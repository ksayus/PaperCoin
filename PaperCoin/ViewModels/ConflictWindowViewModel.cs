using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaperCoin.Service.LanSync;
using Serilog;

namespace PaperCoin.ViewModels;

/// <summary>冲突窗口里的一行：左边本机、右边对方，单选一个保留。</summary>
public sealed class ConflictRowViewModel : ObservableObject
{
    private readonly Action _onChoiceChanged;
    private ConflictChoice? _choice;

    public ConflictRowViewModel(MergeConflict conflict, string groupName, Action onChoiceChanged)
    {
        Conflict = conflict;
        GroupName = groupName;
        _onChoiceChanged = onChoiceChanged;

        var local = Describe(conflict.Local, "本机已删除");
        var remote = Describe(conflict.Remote, "对方已删除");

        LocalTitle = local.Title;
        LocalDetail = local.Detail;
        RemoteTitle = remote.Title;
        RemoteDetail = remote.Detail;
    }

    public MergeConflict Conflict { get; }

    public string Id => Conflict.Id;

    /// <summary>RadioButton 的 GroupName 必须每行唯一，否则勾一行会取消另一行。</summary>
    public string GroupName { get; }

    public string LocalTitle { get; }
    public string LocalDetail { get; }
    public string RemoteTitle { get; }
    public string RemoteDetail { get; }

    public bool ChooseLocal
    {
        get => _choice == ConflictChoice.Local;
        set
        {
            if (value) SetChoice(ConflictChoice.Local);
        }
    }

    public bool ChooseRemote
    {
        get => _choice == ConflictChoice.Remote;
        set
        {
            if (value) SetChoice(ConflictChoice.Remote);
        }
    }

    public ConflictChoice? Choice => _choice;

    public void SetChoice(ConflictChoice? choice)
    {
        if (_choice == choice) return;

        _choice = choice;
        OnPropertyChanged(nameof(ChooseLocal));
        OnPropertyChanged(nameof(ChooseRemote));
        _onChoiceChanged();
    }

    private static (string Title, string Detail) Describe(SyncEntry? entry, string deletedText)
    {
        if (entry is null) return (deletedText, "");

        var title = string.IsNullOrWhiteSpace(entry.Description) ? "（无描述）" : entry.Description;
        var amount = (entry.Amount >= 0 ? "+" : "") + LanSyncHash.FormatAmount(entry.Amount);
        var category = string.IsNullOrWhiteSpace(entry.Category) ? "未分类" : entry.Category;

        return (title, $"{amount} · {category} · {entry.Date}");
    }
}

/// <summary>
/// 冲突窗口的数据：每项必须做出选择后才能 Apply；Cancel/关闭窗口会丢弃选择且不写任何数据。
/// </summary>
public sealed class ConflictWindowViewModel : ObservableObject
{
    private readonly LanSyncService _service;
    private SyncConflictSession _session;

    public ConflictWindowViewModel(LanSyncService service, SyncConflictSession session)
    {
        _service = service;
        _session = session;

        ApplyCommand = new RelayCommand(Apply, () => CanApply);
        CancelCommand = new RelayCommand(Cancel);

        Rebuild(new Dictionary<string, ConflictChoice>(StringComparer.Ordinal));
    }

    public event Action? RequestClose;

    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public ObservableCollection<ConflictRowViewModel> Conflicts { get; } = [];

    public string PeerText => $"对方：{_session.PeerName} · {_session.EndpointText}";

    public string CountText => $"共 {Conflicts.Count} 处冲突；每项都要选择保留哪一边";

    public bool CanApply => Conflicts.Count > 0 && Conflicts.All(c => c.Choice is not null);

    public bool Applied { get; private set; }

    public bool Cancelled { get; private set; }

    public string PeerId => _session.PeerId;

    public SyncConflictSession Session => _session;

    /// <summary>
    /// 同一条会话又发现新冲突时更新内容。按 Id 保留用户已经点过的选择，
    /// 新出现的 Id 仍然要求重新选择。
    /// </summary>
    public void UpdateSession(SyncConflictSession session)
    {
        var previous = new Dictionary<string, ConflictChoice>(StringComparer.Ordinal);

        foreach (var row in Conflicts)
        {
            if (row.Choice is { } choice) previous[row.Id] = choice;
        }

        _session = session;
        Rebuild(previous);

        OnPropertyChanged(nameof(PeerText));
        OnPropertyChanged(nameof(CountText));
    }

    private void Rebuild(IReadOnlyDictionary<string, ConflictChoice> previousChoices)
    {
        Conflicts.Clear();

        int index = 0;

        foreach (var conflict in _session.Conflicts.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            var row = new ConflictRowViewModel(
                conflict,
                "sync-conflict-" + index++,
                OnChoiceChanged);

            if (previousChoices.TryGetValue(conflict.Id, out var choice))
                row.SetChoice(choice);

            Conflicts.Add(row);
        }

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CanApply));
        (ApplyCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void OnChoiceChanged()
    {
        OnPropertyChanged(nameof(CanApply));
        (ApplyCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void Apply()
    {
        if (!CanApply) return;

        var choices = Conflicts
            .Where(c => c.Choice is not null)
            .ToDictionary(c => c.Id, c => c.Choice!.Value, StringComparer.Ordinal);

        Applied = true;
        RequestClose?.Invoke();

        _ = RetryAsync(_session.PeerId, choices);
    }

    private void Cancel()
    {
        Cancelled = true;
        _service.CancelConflicts(_session.PeerId);
        RequestClose?.Invoke();
    }

    private async Task RetryAsync(string peerId, IReadOnlyDictionary<string, ConflictChoice> choices)
    {
        try
        {
            await _service.ResolveConflictsAsync(peerId, choices);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "按冲突选择重新同步失败");
        }
    }
}
