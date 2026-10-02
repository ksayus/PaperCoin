using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaperCoin.Core.Models;
using PaperCoin.Service;

namespace PaperCoin.ViewModels;

/// <summary>
/// 新增 / 编辑一笔流水。今日页与日历页各持有一个实例，面板外观与开合动画由
/// <c>Views/BillEditorOverlay</c> 提供，本类不含任何 UIElement。
/// </summary>
public partial class BillEditorViewModel : ObservableObject
{
    private readonly BillRepository _repository;

    /// <summary>正在编辑的流水，为 <c>null</c> 表示新增模式。编辑就地改原对象，输入实时写回列表那一行。</summary>
    private BillEntry? _editingEntry;

    public BillEditorViewModel(BillRepository repository)
    {
        _repository = repository;

        // 命令显式构造，不用 [RelayCommand] 源生成器
        SaveCommand = new RelayCommand(Commit, () => !HasError);
        CancelCommand = new RelayCommand(Close);
    }

    /// <summary>
    /// 落盘成功后调用，宿主在这里刷新自己的列表。返回值会被 await，
    /// 因此"先落盘、再刷新"的顺序由这里保证。
    /// </summary>
    public Func<BillEntry, Task>? Saved { get; set; }

    /// <summary>落盘失败时调用，参数是给用户看的提示文本</summary>
    public Action<string>? SaveFailed { get; set; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>
    /// 下拉框里的分类候选，来自账单里已经用过的分类。面板每次打开时刷新，写盘成功后也再刷一次，
    /// 用户直接输入的新分类因此马上就能在下次选择时看到。
    /// </summary>
    public ObservableCollection<string> Categories { get; } = [];

    private bool _isOpen;

    /// <summary>面板是否打开，<c>BillEditorOverlay</c> 监听它播开合动画</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (!SetProperty(ref _isOpen, value)) return;

            OnPropertyChanged(nameof(EditorTitle));

            // 宿主的新增按钮 CanExecute 依赖本属性，而 CommandManager.RequerySuggested
            // 不会因普通属性变更重算，需显式要求重新查询
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>面板标题，新增与编辑不同</summary>
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

    /// <summary>草稿是否不合法，依据真实校验结果而非界面提示文本，后者在面板刚打开时被隐藏</summary>
    public bool HasError => !string.IsNullOrEmpty(_validationError);

    /// <summary>真实校验结果，不受是否已提示影响</summary>
    private string _validationError = "";

    /// <summary>打开面板新增一笔，日期默认为 <paramref name="date"/></summary>
    public void OpenForCreate(DateOnly date)
    {
        _editingEntry = null;
        _draftTouched = false;

        DraftDescription = "";
        DraftAmountText = "";
        DraftCategory = "";
        DraftDate = date.ToDateTime(TimeOnly.MinValue);

        // 草稿被赋成相同值时 SetProperty 不会触发 Validate，这里需显式校验一次
        Validate();

        // 候选只是辅助信息，取候选失败不该拖住面板打开，所以这里不等它
        _ = RefreshCategoriesAsync();

        IsOpen = true;
    }

    /// <summary>打开面板编辑已有流水</summary>
    public void OpenForEdit(BillEntry entry)
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

        _ = RefreshCategoriesAsync();

        IsOpen = true;
    }

    /// <summary>关闭面板并丢弃草稿</summary>
    public void Close()
    {
        _editingEntry = null;
        ErrorMessage = "";
        IsOpen = false;
    }

    private async void Commit()
    {
        if (HasError) return;

        var description = DraftDescription.Trim();
        var amount = decimal.Parse(DraftAmountText.Trim());
        var category = string.IsNullOrWhiteSpace(DraftCategory) ? null : DraftCategory.Trim();
        var date = DateOnly.FromDateTime(DraftDate ?? DateTime.Today);

        var entry = _editingEntry;
        var previousDate = entry?.Date;

        if (entry is null)
        {
            entry = new BillEntry
            {
                Description = description,
                Amount = amount,
                Category = category,
                Date = date
            };
        }
        else
        {
            // 就地改字段，替换集合元素会重建行容器并丢掉选中 / 焦点状态
            entry.Description = description;
            entry.Amount = amount;
            entry.Category = category;
            entry.Date = date;
        }

        _editingEntry = null;
        ErrorMessage = "";
        IsOpen = false;

        bool saved;
        try
        {
            saved = await _repository.UpsertEntryAsync(entry, previousDate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 本方法是 async void，异常外抛会成为未处理异常
            saved = false;
        }

        if (!saved)
        {
            SaveFailed?.Invoke("保存失败，请检查磁盘权限或空间");
            return;
        }

        // 新分类此刻已经进了账单，刷新候选让它马上可选
        await RefreshCategoriesAsync();

        if (Saved is not null)
            await Saved(entry);
    }

    /// <summary>
    /// 重新拉取分类候选。读不到时保持原样，用户照样可以手打分类：
    /// 候选列表是辅助信息，不该因为一次读取失败就打断记账。
    /// </summary>
    private async Task RefreshCategoriesAsync()
    {
        IReadOnlyList<string> categories;
        try
        {
            categories = await _repository.GetCategoriesAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        await UiThread.RunAsync(() =>
        {
            Categories.Clear();
            foreach (var category in categories)
                Categories.Add(category);

            return Task.CompletedTask;
        });
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
}
