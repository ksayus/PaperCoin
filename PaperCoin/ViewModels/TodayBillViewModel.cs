using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaperCoin.Core.Models;
using PaperCoin.Service;

namespace PaperCoin.ViewModels;

/// <summary>
/// 今日账单页面的 ViewModel，只维护 <see cref="IsEditorOpen"/> 状态位，
/// 面板动画由 TodayBill.xaml.cs 监听该属性驱动。
/// </summary>
public partial class TodayBillViewModel : ObservableObject
{
    private readonly BillRepository _repository;

    /// <summary>当前正在编辑的流水，为 <c>null</c> 表示新增模式。直接编辑列表中的原对象，输入会实时写回该行。</summary>
    private BillEntry? _editingEntry;

    public TodayBillViewModel(BillRepository repository)
    {
        _repository = repository;

        // 命令显式构造，不用 [RelayCommand] 源生成器
        AddCommand = new RelayCommand(OpenEditor, () => !IsEditorOpen);
        EditCommand = new RelayCommand<BillEntry>(OpenEditorFor);
        RemoveCommand = new RelayCommand<BillEntry>(Remove);
        SaveCommand = new RelayCommand(CommitEditor, () => !HasError);
        CancelCommand = new RelayCommand(DiscardEditor);

        _ = LoadAsync();
    }

    // ---- 数据 ----

    /// <summary>当日流水，界面直接绑定此实例</summary>
    public ObservableCollection<BillEntry> Entries { get; } = [];

    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>列表为空时展示引导文案</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>当日合计</summary>
    public decimal Total => Entries.Sum(e => e.Amount);

    /// <summary>带符号的合计文本，与单条流水保持同一套正负约定</summary>
    public string TotalText => (Total >= 0 ? "+" : "-") + Math.Abs(Total).ToString("N2");

    /// <summary>合计为正（收入）时显示绿色，否则红色</summary>
    public bool IsPositive => Total >= 0;

    public string CountText => $"共 {Entries.Count} 笔";

    // ---- 编辑面板状态 ----

    private bool _isEditorOpen;

    /// <summary>编辑面板是否打开，属性变化由 TodayBill.xaml.cs 驱动对应动画</summary>
    public bool IsEditorOpen
    {
        get => _isEditorOpen;
        set
        {
            if (!SetProperty(ref _isEditorOpen, value)) return;

            OnPropertyChanged(nameof(EditorTitle));

            // AddCommand 的 CanExecute 依赖本属性，而 CommandManager.RequerySuggested
            // 不会因普通属性变更重算，需显式要求重新查询
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string EditorTitle => _editingEntry is null ? "新增账单" : "编辑账单";

    /// <summary>用户是否已修改过草稿，未修改时不显示校验提示</summary>
    private bool _draftTouched;

    private string _draftDescription = "";

    /// <summary>草稿描述</summary>
    public string DraftDescription
    {
        get => _draftDescription;
        set
        {
            if (SetProperty(ref _draftDescription, value))
            {
                _draftTouched = true;
                Validate();
            }
        }
    }
    private string _draftAmountText = "";

    /// <summary>
    /// 草稿金额，以字符串承载。用户输入 "-" 或 "12." 这类中间态时不会触发绑定转换失败，
    /// 由 <see cref="Validate"/> 统一判定。
    /// </summary>
    public string DraftAmountText
    {
        get => _draftAmountText;
        set
        {
            if (SetProperty(ref _draftAmountText, value))
            {
                _draftTouched = true;
                Validate();
            }
        }
    }

    private string _draftCategory = "";

    /// <summary>草稿分类</summary>
    public string DraftCategory
    {
        get => _draftCategory;
        set => SetProperty(ref _draftCategory, value);
    }

    private DateTime? _draftDate = DateTime.Today;

    /// <summary>草稿日期，用 <see cref="DateTime"/>? 直接对接 DatePicker.SelectedDate，无需值转换器</summary>
    public DateTime? DraftDate
    {
        get => _draftDate;
        set => SetProperty(ref _draftDate, value);
    }

    private string _errorMessage = "";

    /// <summary>校验失败提示，为空表示当前草稿合法</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>
    /// 草稿是否不合法，依据真实校验结果而非界面提示文本，后者在面板刚打开时被隐藏。
    /// </summary>
    public bool HasError => !string.IsNullOrEmpty(_validationError);

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
        _editingEntry = null;
        _draftTouched = false;

        DraftDescription = "";
        DraftAmountText = "";
        DraftCategory = "";
        DraftDate = DateTime.Today;
        Notice = "";

        // 草稿被赋成相同值时 SetProperty 不会触发 Validate，这里需显式校验一次
        Validate();

        IsEditorOpen = true;
    }

    private void OpenEditorFor(BillEntry? entry)
    {
        // 命令也可被手动 Execute(null)，这里做防御性判空
        if (entry is null) return;

        _editingEntry = entry;

        _draftTouched = true;

        DraftDescription = entry.Description;
        DraftAmountText = entry.Amount.ToString("0.##");
        DraftCategory = entry.Category ?? "";
        DraftDate = entry.Date.ToDateTime(TimeOnly.MinValue);

        Validate();

        IsEditorOpen = true;
    }

    /// <summary>放弃编辑，只丢弃引用</summary>
    private void DiscardEditor()
    {
        _editingEntry = null;
        ErrorMessage = "";
        IsEditorOpen = false;
    }

    /// <summary>
    /// 提交草稿并落盘，由 <see cref="SaveCommand"/> 独占调用。数据先落定，动画只是事后收尾。
    /// </summary>
    private async void CommitEditor()
    {
        if (HasError) return;

        var amount = decimal.Parse(DraftAmountText.Trim());
        var date = DateOnly.FromDateTime(DraftDate ?? DateTime.Today);
        var entry = _editingEntry;

        if (entry is null)
        {
            Entries.Add(new BillEntry
            {
                Description = DraftDescription.Trim(),
                Amount = amount,
                Category = string.IsNullOrWhiteSpace(DraftCategory) ? null : DraftCategory.Trim(),
                Date = date
            });
        }
        else
        {
            // 就地改字段，替换集合元素会重建行容器并丢掉选中/焦点状态
            entry.Description = DraftDescription.Trim();
            entry.Amount = amount;
            entry.Category = string.IsNullOrWhiteSpace(DraftCategory) ? null : DraftCategory.Trim();
            entry.Date = date;
        }

        _editingEntry = null;
        ErrorMessage = "";
        IsEditorOpen = false;

        await SaveAndResyncAsync(date);
    }

    /// <summary>
    /// 落盘后重新同步本页。必须先 await 落盘再同步，否则同步可能读到尚未写入磁盘的旧数据，
    /// 把刚加入的条目从列表里删掉。集合变更统一编组回 UI 线程。
    /// </summary>
    internal async Task SaveAndResyncAsync(DateOnly savedDate)
    {
        try
        {
            await _repository.SaveAsync(Entries);
            await OnUiThreadAsync(ReloadTodayAsync);

            if (savedDate != Today)
                Notice = $"已记到 {savedDate:yyyy-MM-dd}，本页只显示今天";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写盘失败需告知用户，异常不能外抛（调用方 CommitEditor 是 async void）
            await OnUiThreadAsync(() =>
            {
                Notice = "保存失败，请检查磁盘权限或空间";
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>把一段异步操作编组到 UI 线程执行</summary>
    private static Task OnUiThreadAsync(Func<Task> operation)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        // 单测场景下没有 Application，直接执行
        if (dispatcher is null || dispatcher.CheckAccess())
            return operation();

        return dispatcher.InvokeAsync(operation).Task.Unwrap();
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

            // 本页只涉及今天的流水，整日覆盖写是安全的
            await _repository.SaveAsync(Entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 本方法是 async void，异常外抛会成为未处理异常
            Notice = "删除失败，请检查磁盘权限或空间";
        }
    }

    // ---- 加载 / 汇总 / 校验 ----

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

    /// <summary>
    /// 校验草稿。<see cref="ErrorMessage"/> 只在用户动过草稿后显示，
    /// <see cref="HasError"/> 始终反映真实结果。
    /// </summary>
    private void Validate()
    {
        string error;

        if (string.IsNullOrWhiteSpace(DraftDescription))
        {
            error = "请填写账单描述";
        }
        else if (!decimal.TryParse(DraftAmountText.Trim(), out var amount))
        {
            error = "金额需要是数字, 例如 -12.50";
        }
        else if (amount == 0)
        {
            // decimal.TryParse 对空白串返回 true 且结果为 0，此分支同时兜住空输入
            error = "金额不能为 0";
        }
        else
        {
            error = "";
        }

        _validationError = error;
        ErrorMessage = _draftTouched ? error : "";

        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>真实校验结果，不受是否已提示影响</summary>
    private string _validationError = "";
}
