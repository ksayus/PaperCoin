using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaperCoin.Core.Models;
using PaperCoin.Service;

namespace PaperCoin.ViewModels;

/// <summary>
/// 今日账单页面。列表只装今天的流水，新增 / 编辑交给共享的 <see cref="Editor"/>。
/// </summary>
public partial class TodayBillViewModel : ObservableObject
{
    private readonly BillRepository _repository;

    public TodayBillViewModel(BillRepository repository)
    {
        _repository = repository;

        Editor = new BillEditorViewModel(repository)
        {
            Saved = OnEntrySaved,
            SaveFailed = message => Notice = message
        };

        // 命令显式构造，不用 [RelayCommand] 源生成器
        AddCommand = new RelayCommand(OpenEditor, () => !Editor.IsOpen);
        EditCommand = new RelayCommand<BillEntry>(OpenEditorFor);
        RemoveCommand = new RelayCommand<BillEntry>(Remove);

        _ = LoadAsync();
    }

    /// <summary>由今日页与日历页共用的编辑面板</summary>
    public BillEditorViewModel Editor { get; }

    // ---- 数据 ----

    /// <summary>当日流水，界面直接绑定此实例</summary>
    public ObservableCollection<BillEntry> Entries { get; } = [];

    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RemoveCommand { get; }

    /// <summary>列表为空时展示引导文案</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>当日合计</summary>
    public decimal Total => Entries.Sum(e => e.Amount);

    /// <summary>带符号的合计文本，与单条流水保持同一套正负约定</summary>
    public string TotalText => (Total >= 0 ? "+" : "-") + Math.Abs(Total).ToString("N2");

    /// <summary>合计为正（收入）时显示绿色，否则红色</summary>
    public bool IsPositive => Total >= 0;

    public string CountText => $"共 {Entries.Count} 笔";

    private string _notice = "";

    /// <summary>一次性提示（例如把账记到了非今天的日期），下一次打开面板时清除</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
                OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>本页固定的今天，启动时取一次</summary>
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    // ---- 编辑面板动作 ----

    private void OpenEditor()
    {
        Notice = "";
        Editor.OpenForCreate(Today);
    }

    private void OpenEditorFor(BillEntry? entry)
    {
        Notice = "";
        if (entry is not null) Editor.OpenForEdit(entry);
    }

    /// <summary>
    /// 面板把记录写进它自身的日期后回调这里。本页只显示今天，所以先与磁盘同步一次，
    /// 再把"这笔没落在今天"告诉用户。
    /// </summary>
    private async Task OnEntrySaved(BillEntry entry)
    {
        await UiThread.RunAsync(ReloadTodayAsync);

        Notice = entry.Date == Today ? "" : $"已记到 {entry.Date:yyyy-MM-dd}，本页只显示今天";
    }

    /// <summary>
    /// 让本页与磁盘保持一致。按 <see cref="BillEntry.Id"/> 做增量同步，不整表重建：
    /// 整表 Clear 会触发 Reset 通知，WPF 会丢弃并重新生成全部行容器。
    /// </summary>
    private async Task ReloadTodayAsync()
    {
        var today = await _repository.LoadDayAsync(Today);

        SyncEntries(today);
        RefreshSummary();
    }

    /// <summary>同步服务在后台改完账单后由 MainWindow 调用，保持当前可见页与磁盘一致。</summary>
    internal Task RefreshFromDiskAsync() => ReloadTodayAsync();

    /// <summary>把当前集合同步成目标内容，尽量保留已有元素实例</summary>
    private void SyncEntries(IReadOnlyList<BillEntry> target)
    {
        // 逆序遍历，正序删除会因下标位移漏掉元素
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            if (!target.Any(t => t.Id == Entries[i].Id))
                Entries.RemoveAt(i);
        }

        for (int i = 0; i < target.Count; i++)
        {
            var wanted = target[i];
            int existing = IndexOfId(wanted.Id);

            if (existing < 0)
            {
                Entries.Insert(Math.Min(i, Entries.Count), wanted);
            }
            else if (existing != i && existing > i)
            {
                Entries.Move(existing, i);
            }
        }
    }

    private int IndexOfId(Guid id)
    {
        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Id == id) return i;
        }
        return -1;
    }

    /// <summary>
    /// 删除一条流水。界面同步移除该行；删到一条不剩时要等待
    /// <see cref="BillRepository.DeleteDayAsync"/> 的结果以便提示用户。
    /// </summary>
    private async void Remove(BillEntry? entry)
    {
        if (entry is null) return;

        // 按 Id 定位后移除，不依赖引用相等
        var target = Entries.FirstOrDefault(e => e.Id == entry.Id);
        if (target is null) return;

        Entries.Remove(target);
        RefreshSummary();

        try
        {
            // SaveAsync 按记录自身的 Date 分组落盘，空集合没有任何分组，
            // 不会删掉磁盘上的当日文件，因此删空当日时要显式删除
            if (Entries.Count == 0)
            {
                if (!await _repository.DeleteDayAsync(Today))
                    Notice = "删除失败，请检查磁盘权限或空间";

                return;
            }

            // 本页只装今天的流水，整日覆盖写是安全的
            await _repository.SaveAsync(Entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 本方法是 async void，异常外抛会成为未处理异常
            Notice = "删除失败，请检查磁盘权限或空间";
        }
    }

    // ---- 加载 / 汇总 ----

    /// <summary>
    /// 载入当日流水。依赖 await 恢复到 UI 线程的 SynchronizationContext 才能修改
    /// <see cref="Entries"/>，加 ConfigureAwait(false) 时须改回 Dispatcher 编组。
    /// </summary>
    private async Task LoadAsync()
    {
        var loaded = await _repository.LoadDayAsync(Today);

        foreach (var entry in loaded)
            Entries.Add(entry);

        RefreshSummary();
    }

    /// <summary>
    /// 刷新所有从 <see cref="Entries"/> 派生的只读属性。集合只通知增删，
    /// 不会通知 Total / CountText 这类聚合值，结构性变更后需主动调用。
    /// </summary>
    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(IsPositive));
        OnPropertyChanged(nameof(CountText));
    }
}
