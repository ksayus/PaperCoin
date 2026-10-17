using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaperCoin.Core.Models;
using PaperCoin.Service;

namespace PaperCoin.ViewModels;

/// <summary>日历上的一格</summary>
public partial class CalendarCell : ObservableObject
{
    public required DateOnly Date { get; init; }

    public required bool IsCurrentMonth { get; init; }

    public required bool IsToday { get; init; }

    /// <summary>日号，如 5</summary>
    public int Day => Date.Day;

    private decimal _total;
    public decimal Total
    {
        get => _total;
        set
        {
            if (SetProperty(ref _total, value))
                OnPropertyChanged(nameof(TotalText));
        }
    }

    private int _count;
    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
                OnPropertyChanged(nameof(HasItems));
        }
    }

    public bool HasItems => Count > 0;

    /// <summary>格子里显示的金额，无记录时留空</summary>
    public string TotalText => Count == 0
        ? ""
        : Total >= 0 ? $"+{Total:0.##}" : $"-{Math.Abs(Total):0.##}";

    /// <summary>是否正在被选中（日视图下与 SelectedDate 同步）</summary>
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>年视图里的一个月</summary>
public class MonthSummary
{
    public required int Month { get; init; }
    public required decimal Total { get; init; }
    public required int Count { get; init; }

    public string Title => $"{Month} 月";
    public bool HasItems => Count > 0;
    public string TotalText => Count == 0
        ? "—"
        : Total >= 0 ? $"+{Total:N2}" : $"-{Math.Abs(Total):N2}";
}

/// <summary>日历的三种浏览粒度</summary>
public enum CalendarViewMode
{
    Day,
    Month,
    Year
}

/// <summary>
/// 日历账单页面。日 / 月 / 年三种粒度共用同一个当前定位 <see cref="_anchor"/>，
/// 切换粒度只改变聚合方式；上一页 / 下一页的步长由当前粒度决定。
/// 日视图下可以像今日页那样直接记一笔，草稿日期默认是当前定位。
/// </summary>
public partial class CalenderBillViewModel : ObservableObject
{
    private readonly BillRepository _repository;

    /// <summary>当前定位的日期，三种视图共同的基准</summary>
    private DateOnly _anchor = DateOnly.FromDateTime(DateTime.Today);

    public CalenderBillViewModel(BillRepository repository)
    {
        _repository = repository;

        Editor = new BillEditorViewModel(repository)
        {
            Saved = OnEntrySaved,
            SaveFailed = message => Notice = message
        };

        PreviousCommand = new RelayCommand(() => Shift(-1));
        NextCommand = new RelayCommand(() => Shift(1));
        TodayCommand = new RelayCommand(GoToday);
        ShowDayCommand = new RelayCommand(() => SetMode(CalendarViewMode.Day));
        ShowMonthCommand = new RelayCommand(() => SetMode(CalendarViewMode.Month));
        ShowYearCommand = new RelayCommand(() => SetMode(CalendarViewMode.Year));
        SelectDayCommand = new RelayCommand<CalendarCell>(SelectDay);
        AddCommand = new RelayCommand(OpenEditor, () => !Editor.IsOpen);
        EditCommand = new RelayCommand<BillEntry>(OpenEditorFor);
        RemoveCommand = new RelayCommand<BillEntry>(Remove);

        _ = RefreshAsync();
    }

    /// <summary>由今日页与日历页共用的编辑面板</summary>
    public BillEditorViewModel Editor { get; }

    // ---- 命令 ----

    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand TodayCommand { get; }
    public ICommand ShowDayCommand { get; }
    public ICommand ShowMonthCommand { get; }
    public ICommand ShowYearCommand { get; }
    public ICommand SelectDayCommand { get; }

    /// <summary>右下角的新增按钮</summary>
    public ICommand AddCommand { get; }

    /// <summary>点明细卡片：改这一笔</summary>
    public ICommand EditCommand { get; }

    /// <summary>点明细卡片上的删除按钮：删这一笔</summary>
    public ICommand RemoveCommand { get; }

    // ---- 编辑面板 ----

    private string _notice = "";

    /// <summary>保存结果的一次性提示</summary>
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

    /// <summary>
    /// 测试入口：把定位直接跳到某一天，并刷新。
    /// 翻页命令只能按月 / 年走，"当天是否落在当前范围里"这类分支用翻页很难精确构造。
    /// </summary>
    internal async Task GoToForTestAsync(DateOnly date)
    {
        _anchor = date;
        _selectedDate = date;

        await UiThread.RunAsync(RefreshAsync);
    }

    private void OpenEditor()
    {
        Notice = "";
        Editor.OpenForCreate(DraftDate);
    }

    /// <summary>
    /// 新增时的默认日期。三种粒度下都要有唯一答案，右下角的 + 才能一直可用。
    /// <list type="bullet">
    /// <item>日视图：就是当前定位的那一天。</item>
    /// <item>月 / 年视图：当天落在当前范围内就用当天，否则用范围的起始日。
    /// 用"起始日"而不是"定位日"是因为 <see cref="_anchor"/> 在月视图下可能被
    /// <see cref="SelectDay"/> 改成别的月份的日子，那不是用户此刻在看的范围。</item>
    /// </list>
    /// </summary>
    private DateOnly DraftDate
    {
        get
        {
            if (Mode == CalendarViewMode.Day) return _anchor;

            var today = DateOnly.FromDateTime(DateTime.Today);

            return Mode switch
            {
                CalendarViewMode.Month =>
                    today.Year == _anchor.Year && today.Month == _anchor.Month
                        ? today
                        : new DateOnly(_anchor.Year, _anchor.Month, 1),
                _ => today.Year == _anchor.Year ? today : new DateOnly(_anchor.Year, 1, 1)
            };
        }
    }

    /// <summary>编辑某一笔。面板的日期会跟着这条记录走，改完由 <see cref="OnEntrySaved"/> 刷新本页。</summary>
    private void OpenEditorFor(BillEntry? entry)
    {
        if (entry is null) return;

        Notice = "";
        Editor.OpenForEdit(entry);
    }

    /// <summary>
    /// 删除某一笔。界面先移除该行，再落盘；失败时把列表整体重载回真实状态，
    /// 免得界面显示"已经删了"而磁盘上还在。
    /// </summary>
    private async void Remove(BillEntry? entry)
    {
        if (entry is null) return;

        Notice = "";

        // 按 Id 定位后移除，不依赖引用相等
        var target = Entries.FirstOrDefault(e => e.Id == entry.Id);
        if (target is not null) Entries.Remove(target);

        RefreshTotals();

        bool ok = await _repository.DeleteEntryAsync(entry.Date, entry.Id);

        if (!ok)
        {
            Notice = "删除失败，请检查磁盘权限或空间";

            await UiThread.RunAsync(RefreshAsync);
            return;
        }

        // 月 / 年视图下格子与月份的合计是从条目算出来的，删一条要重算，
        // 不然被删的那笔还留在格子金额里
        await UiThread.RunAsync(RefreshAsync);
    }

    /// <summary>
    /// 面板写盘后回调。本页能看到任意一天，所以刷新当前粒度即可；
    /// 只有这笔没落在当前定位上时才提示，否则提示是多余的。
    /// </summary>
    private async Task OnEntrySaved(BillEntry entry)
    {
        await UiThread.RunAsync(RefreshAsync);

        Notice = entry.Date == _anchor ? "" : $"已记到 {entry.Date:yyyy-MM-dd}";
    }

    // ---- 视图状态 ----

    private CalendarViewMode _mode = CalendarViewMode.Day;

    public CalendarViewMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value)) return;

            OnPropertyChanged(nameof(IsDayMode));
            OnPropertyChanged(nameof(IsMonthMode));
            OnPropertyChanged(nameof(IsYearMode));
        }
    }

    public bool IsDayMode => Mode == CalendarViewMode.Day;
    public bool IsMonthMode => Mode == CalendarViewMode.Month;
    public bool IsYearMode => Mode == CalendarViewMode.Year;

    /// <summary>标题栏文本，随粒度变化</summary>
    public string PeriodTitle => Mode switch
    {
        CalendarViewMode.Day => _anchor.ToString("yyyy 年 M 月 d 日"),
        CalendarViewMode.Month => _anchor.ToString("yyyy 年 M 月"),
        _ => _anchor.ToString("yyyy 年")
    };

    // ---- 数据 ----

    /// <summary>月视图的 42 个格子（6 周 × 7 天）</summary>
    public ObservableCollection<CalendarCell> Cells { get; } = [];

    /// <summary>年视图的 12 个月</summary>
    public ObservableCollection<MonthSummary> Months { get; } = [];

    /// <summary>日视图 / 明细列表</summary>
    public ObservableCollection<BillEntry> Entries { get; } = [];

    /// <summary>当前粒度的合计</summary>
    public decimal Total { get; private set; }

    public string TotalText => (Total >= 0 ? "+" : "-") + Math.Abs(Total).ToString("N2");

    public bool IsPositive => Total >= 0;

    public string CountText => $"共 {Entries.Count} 笔";

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>月视图里当前选中的具体日期，用于高亮格子</summary>
    private DateOnly _selectedDate = DateOnly.FromDateTime(DateTime.Today);

    public string SelectedDateText => _selectedDate.ToString("M 月 d 日");

    // ---- 导航 ----

    /// <summary>按当前粒度前后翻页</summary>
    private void Shift(int direction)
    {
        _anchor = Mode switch
        {
            CalendarViewMode.Day => _anchor.AddDays(direction),
            CalendarViewMode.Month => _anchor.AddMonths(direction),
            _ => _anchor.AddYears(direction)
        };

        // 日视图下选中项跟随锚点
        if (Mode == CalendarViewMode.Day)
            _selectedDate = _anchor;

        _ = RefreshAsync();
    }

    private void GoToday()
    {
        _anchor = DateOnly.FromDateTime(DateTime.Today);
        _selectedDate = _anchor;
        _ = RefreshAsync();
    }

    private void SetMode(CalendarViewMode mode)
    {
        if (Mode == mode) return;

        Mode = mode;
        OnPropertyChanged(nameof(PeriodTitle));

        _ = RefreshAsync();
    }

    /// <summary>点选月视图里的某一格，落到该日并自动切到日视图</summary>
    private void SelectDay(CalendarCell? cell)
    {
        if (cell is null) return;

        _anchor = cell.Date;
        _selectedDate = cell.Date;

        Mode = CalendarViewMode.Day;
        OnPropertyChanged(nameof(PeriodTitle));

        _ = RefreshAsync();
    }

    // ---- 聚合 ----

    private async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(PeriodTitle));

        switch (Mode)
        {
            case CalendarViewMode.Day:
                await LoadDayAsync();
                break;
            case CalendarViewMode.Month:
                await LoadMonthAsync();
                break;
            default:
                await LoadYearAsync();
                break;
        }

        RefreshTotals();
    }

    /// <summary>同步服务在后台改完账单后由 MainWindow 调用；保持当前日历视图与锚点不变。</summary>
    internal Task RefreshFromDiskAsync() => RefreshAsync();

    private async Task LoadDayAsync()
    {
        var day = await _repository.LoadDayAsync(_anchor);

        Entries.Clear();
        foreach (var entry in day)
            Entries.Add(entry);

        Cells.Clear();
        Months.Clear();
    }

    private async Task LoadMonthAsync()
    {
        var first = new DateOnly(_anchor.Year, _anchor.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);

        var monthEntries = await _repository.LoadMonthAsync(_anchor.Year, _anchor.Month);

        // 按日聚合一次，避免为 42 个格子各查一次仓库
        var byDay = monthEntries
            .GroupBy(e => e.Date.Day)
            .ToDictionary(g => g.Key, g => (Total: g.Sum(e => e.Amount), Count: g.Count()));

        Cells.Clear();
        Months.Clear();

        // 网格起点回退到本周周日，DayOfWeek 的 Sunday 为 0，可直接作为回退天数
        var gridStart = first.AddDays(-(int)first.DayOfWeek);
        var today = DateOnly.FromDateTime(DateTime.Today);

        for (int i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            var inMonth = date.Month == first.Month && date.Year == first.Year;

            byDay.TryGetValue(date.Day, out var stat);

            Cells.Add(new CalendarCell
            {
                Date = date,
                IsCurrentMonth = inMonth,
                IsToday = date == today,
                IsSelected = date == _selectedDate,
                // 非本月格子不显示数字，避免邻月金额被误认为本月合计
                Total = inMonth ? stat.Total : 0,
                Count = inMonth ? stat.Count : 0
            });
        }

        // 月视图的明细列表展示整月
        Entries.Clear();
        foreach (var entry in monthEntries)
            Entries.Add(entry);
    }

    private async Task LoadYearAsync()
    {
        var yearEntries = await _repository.LoadYearAsync(_anchor.Year);

        var byMonth = yearEntries
            .GroupBy(e => e.Date.Month)
            .ToDictionary(g => g.Key, g => (Total: g.Sum(e => e.Amount), Count: g.Count()));

        Months.Clear();
        Cells.Clear();

        for (int m = 1; m <= 12; m++)
        {
            byMonth.TryGetValue(m, out var stat);

            Months.Add(new MonthSummary
            {
                Month = m,
                Total = stat.Total,
                Count = stat.Count
            });
        }

        Entries.Clear();
        foreach (var entry in yearEntries)
            Entries.Add(entry);
    }

    private void RefreshTotals()
    {
        Total = Entries.Sum(e => e.Amount);

        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(IsPositive));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SelectedDateText));
    }
}
