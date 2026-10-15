using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaperCoin.Core;
using PaperCoin.Core.Models;
using PaperCoin.Service;
using Serilog;

namespace PaperCoin.ViewModels;

/// <summary>统计页的时间范围</summary>
public enum StatisticsRange
{
    /// <summary>本月</summary>
    Month,

    /// <summary>本年</summary>
    Year,

    /// <summary>全部</summary>
    All
}

/// <summary>圆环图上的一段。金额与占比都按"这一段代表什么"填好，图表不做任何计算。</summary>
public sealed class DonutSegment
{
    public required string Label { get; init; }

    /// <summary>支出金额，正数</summary>
    public required decimal Amount { get; init; }

    /// <summary>占总量的比例，0~1</summary>
    public required double Fraction { get; init; }

    /// <summary>主题字典里的分类颜色键，图表按它取画刷</summary>
    public required string ColorKey { get; init; }

    public int Count { get; init; }

    /// <summary>环形内的悬浮提示。数值都在这里拼好，避免图表层再引一次格式化规则。</summary>
    public string Tooltip => $"{Label} {AmountText} · {Fraction:P1} · {Count} 笔";

    public string AmountText => Amount.ToString("N2");
}

/// <summary>分类排行里的一行。条形长度是**相对第一名**的占比，文字里的百分比是**占总额**的占比。</summary>
public sealed class CategoryRankRow
{
    public required string Label { get; init; }

    public required decimal Amount { get; init; }

    /// <summary>占总支出的比例，0~1</summary>
    public required double Share { get; init; }

    /// <summary>相对第一名的比例，0~1，用于条形长度</summary>
    public required double BarFraction { get; init; }

    public required string ColorKey { get; init; }

    public int Count { get; init; }

    public string AmountText => Amount.ToString("N2");

    public string ShareText => Share.ToString("P1", CultureInfo.CurrentCulture);

    public string CountText => $"{Count} 笔";
}

/// <summary>月度趋势里的一根柱子</summary>
public sealed class MonthBar
{
    public required string Label { get; init; }

    public required decimal Amount { get; init; }

    /// <summary>相对最高一月的比例，0~1</summary>
    public required double HeightFraction { get; init; }

    public string AmountText => Amount == 0 ? "" : Amount.ToString("N0");

    public string Tooltip => $"{Label}：{Amount:N2}";
}

/// <summary>
/// 统计页面。所有派生数据（圆环分段、分类排行、月度趋势、下钻明细）都在这里一次性算好，
/// 视图只做绑定与动画，不再做任何聚合。
/// <para>
/// 范围口径由本类独占：仓库只负责"按日期取数"和"按分类汇总支出"，
/// "本月是几号到几号""趋势取哪 12 个月"是统计页的选择，不该渗进持久化层。
/// </para>
/// <para>
/// 统计口径不进配置：与日历页的立场一致，切页即回到默认范围。持久化一个"上次看的是本年"
/// 会让用户在某次启动时看到一个自己没选的数字，比每次都从本月开始更难解释。
/// </para>
/// </summary>
public partial class StatisticsViewModel : ObservableObject
{
    /// <summary>圆环最多显示的段数，其余合并成"其他"。再多环上就分不出颜色了。</summary>
    private const int MaxDonutSegments = 8;

    /// <summary>排行默认显示的条数，"查看全部"展开后不受此限</summary>
    private const int DefaultRankRows = 6;

    /// <summary>趋势柱的月数</summary>
    private const int TrendMonths = 12;

    private readonly BillRepository _repository;

    /// <summary>上一次计算所用的索引版本，见 <see cref="BillRepository.Revision"/></summary>
    private int _loadedRevision = -1;

    public StatisticsViewModel(BillRepository repository)
    {
        _repository = repository;

        ShowMonthCommand = new RelayCommand(() => SetRange(StatisticsRange.Month));
        ShowYearCommand = new RelayCommand(() => SetRange(StatisticsRange.Year));
        ShowAllCommand = new RelayCommand(() => SetRange(StatisticsRange.All));
        RefreshCommand = new RelayCommand(() => _ = ReloadAsync());
        ShowAllCategoriesCommand = new RelayCommand(() => IsRankExpanded = true);
        ShowTopCategoriesCommand = new RelayCommand(() => IsRankExpanded = false);
        ExportCsvCommand = new RelayCommand(ExportCsv);
        OpenFocusCommand = new RelayCommand<CategoryRankRow>(OpenFocus);
        CloseFocusCommand = new RelayCommand(CloseFocus);
    }

    // ---- 命令 ----

    public ICommand ShowMonthCommand { get; }
    public ICommand ShowYearCommand { get; }
    public ICommand ShowAllCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ShowAllCategoriesCommand { get; }
    public ICommand ShowTopCategoriesCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand OpenFocusCommand { get; }
    public ICommand CloseFocusCommand { get; }

    // ---- 范围 ----

    private StatisticsRange _range = StatisticsRange.Month;

    public StatisticsRange Range
    {
        get => _range;
        private set
        {
            if (!SetProperty(ref _range, value)) return;

            OnPropertyChanged(nameof(IsMonthRange));
            OnPropertyChanged(nameof(IsYearRange));
            OnPropertyChanged(nameof(IsAllRange));
            OnPropertyChanged(nameof(RangeText));
        }
    }

    public bool IsMonthRange => Range == StatisticsRange.Month;
    public bool IsYearRange => Range == StatisticsRange.Year;
    public bool IsAllRange => Range == StatisticsRange.All;

    public string RangeText => Range switch
    {
        StatisticsRange.Month => DateTime.Today.ToString("yyyy 年 M 月"),
        StatisticsRange.Year => DateTime.Today.ToString("yyyy 年"),
        _ => "全部时间"
    };

    private void SetRange(StatisticsRange range)
    {
        if (Range == range) return;

        Range = range;
        IsRankExpanded = false;
        CloseFocus();

        _ = ReloadNowAsync();
    }

    // ---- 数据 ----

    /// <summary>圆环分段，已按金额降序；空范围时为空集合</summary>
    public IReadOnlyList<DonutSegment> Segments { get; private set; } = [];

    /// <summary>环下方的图例，最多 <see cref="LegendRows"/> 条；与圆环分段同源同色</summary>
    public IReadOnlyList<DonutSegment> LegendRows { get; private set; } = [];

    /// <summary>图例条数上限。多于此数的分段在环上仍画出来，只是不再逐条列名字。</summary>
    private const int MaxLegendRows = 5;

    /// <summary>分类排行，已按金额降序，含全部分类</summary>
    public IReadOnlyList<CategoryRankRow> RankRows { get; private set; } = [];

    private bool _isRankExpanded;

    /// <summary>排行是否展开为全部分类</summary>
    public bool IsRankExpanded
    {
        get => _isRankExpanded;
        private set
        {
            if (!SetProperty(ref _isRankExpanded, value)) return;

            OnPropertyChanged(nameof(VisibleRankRows));
            OnPropertyChanged(nameof(HasMoreRanks));
        }
    }

    /// <summary>当前该显示哪些排行行</summary>
    public IReadOnlyList<CategoryRankRow> VisibleRankRows =>
        IsRankExpanded ? RankRows : [.. RankRows.Take(DefaultRankRows)];

    /// <summary>分类数超过默认条数时才显示"查看全部"</summary>
    public bool HasMoreRanks => RankRows.Count > DefaultRankRows;

    /// <summary>近 12 个月的支出趋势，最早的月份在前</summary>
    public IReadOnlyList<MonthBar> TrendBars { get; private set; } = [];

    /// <summary>趋势里最高的那个月，用于在界面上标一句"最高"</summary>
    public string PeakMonthText { get; private set; } = "";

    // ---- 汇总 ----

    public decimal TotalExpense { get; private set; }
    public decimal TotalIncome { get; private set; }
    public int EntryCount { get; private set; }

    public string TotalExpenseText => TotalExpense.ToString("N2");

    public string TotalIncomeText => TotalIncome.ToString("N2");

    public string EntryCountText => $"{EntryCount} 笔";

    /// <summary>占比最高的分类；范围内没有支出时留空</summary>
    public string TopCategoryText { get; private set; } = "";

    /// <summary>范围内的日均支出。范围是"全部"时按第一天到今天算，不按条目跨过的天数。</summary>
    public string AveragePerDayText { get; private set; } = "";

    /// <summary>范围内没有任何流水</summary>
    public bool IsEmpty => EntryCount == 0;

    /// <summary>范围内没有支出（只有收入）</summary>
    public bool HasNoExpense => TotalExpense == 0;

    /// <summary>范围内有支出。图例与排行只在有支出时才显示，否则空环加空列表是两处噪音。</summary>
    public bool HasExpense => TotalExpense > 0;

    // ---- 下钻 ----

    private CategoryRankRow? _focus;

    /// <summary>当前下钻查看的分类，null 表示没有下钻</summary>
    public CategoryRankRow? Focus
    {
        get => _focus;
        private set
        {
            if (!SetProperty(ref _focus, value)) return;

            OnPropertyChanged(nameof(IsFocusOpen));
            OnPropertyChanged(nameof(FocusTitle));
        }
    }

    public bool IsFocusOpen => Focus is not null;

    public string FocusTitle => Focus is null ? "" : $"{Focus.Label} · {Focus.AmountText}";

    private readonly List<BillEntry> _focusEntries = [];

    /// <summary>下钻分类在当前范围内的流水，按日期升序</summary>
    public IReadOnlyList<BillEntry> FocusEntries => _focusEntries;

    public string FocusSummaryText => Focus is null
        ? ""
        : $"共 {_focusEntries.Count} 笔 · {Focus.ShareText}";

    /// <summary>下钻面板打开过的次数，供 tools/UiSmokeTest 区分"点击没送到"与"状态被重置"</summary>
    internal int FocusOpenCount { get; private set; }



    private void OpenFocus(CategoryRankRow? row)
    {
        if (row is null) return;

        FocusOpenCount++;
        _focusEntries.Clear();

        // 下钻只在已经载入的范围内筛，不重新读盘：范围与分类都已经确定，
        // 数据在 _rangeEntries 里全部就绪。
        _focusEntries.AddRange(_rangeEntries
            .Where(e => e.Amount < 0)
            .Where(e => string.Equals(
                CategoryPalette.Normalize(e.Category), row.Label, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Date));

        // 先记下"原来就是这一行"，再赋值：SetProperty 按引用比较，
        // 关掉再点同一行时新旧引用相同，不会发通知，面板会因为 IsFocusOpen
        // 没被重新通知而打不开。这几条通知因此必须显式发。
        bool alreadyOpen = Focus is not null;

        Focus = row;

        if (alreadyOpen) OnPropertyChanged(nameof(IsFocusOpen));

        OnPropertyChanged(nameof(FocusEntries));
        OnPropertyChanged(nameof(FocusSummaryText));
    }

    private void CloseFocus() => Focus = null;

    // ---- 提示 ----

    private string _notice = "";

    /// <summary>一次性提示（导出结果等），下一次操作时清除</summary>
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

    // ---- 载入 ----

    /// <summary>当前范围里的全部流水，下钻与汇总共用这一份</summary>
    private List<BillEntry> _rangeEntries = [];

    /// <summary>
    /// 按需刷新。统计页实例被 MainWindow 长期缓存，每次切回页面都会调到这里；
    /// 索引版本没变就不重算 —— 但也不能不调，否则记完账再切回来看到的还是旧数字。
    /// </summary>
    public async Task EnsureFreshAsync()
    {
        if (_loadedRevision == _repository.Revision) return;

        await ReloadAsync();
    }

    /// <summary>
    /// 强制重算，不看向缓存。切换时间范围必须有这一步：
    /// 换范围不改索引版本，<see cref="EnsureFreshAsync"/> 会认为"没变"而跳过，
    /// 症状是按钮高亮变了、数字还是上一个范围的。
    /// </summary>
    internal Task ReloadNowAsync() => ReloadAsync();

    private async Task ReloadAsync()
    {
        // 全部范围的起止不可能比"数据库里最早的一天"更早，用 DateOnly 的最小值兜住
        var (from, to) = RangeBounds();

        var rangeEntries = await _repository.LoadAllAsync();

        _rangeEntries = [.. rangeEntries.Where(e => e.Date >= from && e.Date <= to)];

        var breakdown = await _repository.GetCategoryExpenseAsync(from, to);

        BuildSegments(breakdown);
        BuildRanks(breakdown);
        BuildSummary();
        BuildTrend(rangeEntries);

        // 下钻的分类若已不在新范围里，直接关掉，避免面板停在"该分类在此范围内没有记录"
        if (Focus is not null)
        {
            var again = breakdown.FirstOrDefault(
                t => string.Equals(t.Category, Focus.Label, StringComparison.OrdinalIgnoreCase));

            if (again is null) CloseFocus();
            else OpenFocus(RankRows.FirstOrDefault(r => r.Label == again.Category));
        }

        _loadedRevision = _repository.Revision;
    }

    /// <summary>当前范围的首尾两天，都含在内。</summary>
    private (DateOnly From, DateOnly To) RangeBounds()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        return Range switch
        {
            StatisticsRange.Month => (new DateOnly(today.Year, today.Month, 1),
                                      new DateOnly(today.Year, today.Month,
                                          DateTime.DaysInMonth(today.Year, today.Month))),
            StatisticsRange.Year => (new DateOnly(today.Year, 1, 1),
                                     new DateOnly(today.Year, 12, 31)),
            _ => (DateOnly.MinValue, today)
        };
    }

    private void BuildSegments(IReadOnlyList<CategoryTotal> breakdown)
    {
        if (breakdown.Count == 0)
        {
            Segments = [];
            LegendRows = [];
            return;
        }

        decimal total = breakdown.Sum(t => t.Expense);
        var segments = new List<DonutSegment>();

        var head = breakdown.Take(MaxDonutSegments).ToList();
        var tail = breakdown.Skip(MaxDonutSegments).ToList();

        foreach (var item in head)
        {
            segments.Add(new DonutSegment
            {
                Label = item.Category,
                Amount = item.Expense,
                Fraction = total == 0 ? 0 : (double)(item.Expense / total),
                ColorKey = CategoryPalette.ThemeKeyOf(item.Category),
                Count = item.Count
            });
        }

        if (tail.Count > 0)
        {
            decimal rest = tail.Sum(t => t.Expense);

            segments.Add(new DonutSegment
            {
                Label = $"其他 {tail.Count} 项",
                Amount = rest,
                Fraction = total == 0 ? 0 : (double)(rest / total),
                // 合并段有自己的中性色，不参与分类哈希：它不是某一个分类
                ColorKey = CategoryPalette.ThemeKeyOfSlot(CategoryPalette.SlotCount),
                Count = tail.Sum(t => t.Count)
            });
        }

        Segments = segments;

        // 图例是圆环的前几段的名单，必须取同一个列表：分别构造会让环上某段的颜色
        // 与图例里同名分类的颜色对不上，而那种偏差肉眼很难发现
        LegendRows = [.. segments.Take(MaxLegendRows)];
    }

    private void BuildRanks(IReadOnlyList<CategoryTotal> breakdown)
    {
        if (breakdown.Count == 0)
        {
            RankRows = [];
            return;
        }

        decimal total = breakdown.Sum(t => t.Expense);
        decimal top = breakdown[0].Expense;

        RankRows = [.. breakdown.Select(t => new CategoryRankRow
        {
            Label = t.Category,
            Amount = t.Expense,
            Share = total == 0 ? 0 : (double)(t.Expense / total),
            BarFraction = top == 0 ? 0 : (double)(t.Expense / top),
            ColorKey = CategoryPalette.ThemeKeyOf(t.Category),
            Count = t.Count
        })];
    }

    private void BuildSummary()
    {
        TotalExpense = _rangeEntries.Where(e => e.Amount < 0).Sum(e => -e.Amount);
        TotalIncome = _rangeEntries.Where(e => e.Amount > 0).Sum(e => e.Amount);
        EntryCount = _rangeEntries.Count;

        TopCategoryText = RankRows.Count == 0
            ? ""
            : $"最高 {RankRows[0].Label} {RankRows[0].ShareText}";

        var (from, to) = RangeBounds();

        // 范围是"全部"时首日取数据里最早的一天，否则按今天减起始日算天数
        var earliest = _rangeEntries.Count == 0
            ? to
            : _rangeEntries.Min(e => e.Date);

        var first = Range == StatisticsRange.All ? earliest : from;
        int days = Math.Max(1, to.DayNumber - first.DayNumber + 1);

        AveragePerDayText = $"日均 {(TotalExpense / days).ToString("N2")}";

        OnPropertyChanged(nameof(TotalExpense));
        OnPropertyChanged(nameof(TotalIncome));
        OnPropertyChanged(nameof(EntryCount));
        OnPropertyChanged(nameof(TotalExpenseText));
        OnPropertyChanged(nameof(TotalIncomeText));
        OnPropertyChanged(nameof(EntryCountText));
        OnPropertyChanged(nameof(TopCategoryText));
        OnPropertyChanged(nameof(AveragePerDayText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoExpense));
        OnPropertyChanged(nameof(HasExpense));
        OnPropertyChanged(nameof(Segments));
        OnPropertyChanged(nameof(LegendRows));
        OnPropertyChanged(nameof(RankRows));
        OnPropertyChanged(nameof(VisibleRankRows));
        OnPropertyChanged(nameof(HasMoreRanks));
    }

    /// <summary>
    /// 近 12 个月的支出趋势。**与当前范围无关**：趋势本身就是"跨范围"的视图，
    /// 按当前范围过滤会让"本年"以外的选择只剩一根柱子。
    /// </summary>
    private void BuildTrend(IReadOnlyList<BillEntry> allEntries)
    {
        var thisMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);

        var totals = allEntries
            .Where(e => e.Amount < 0)
            .GroupBy(e => new DateOnly(e.Date.Year, e.Date.Month, 1))
            .ToDictionary(g => g.Key, g => g.Sum(e => -e.Amount));

        var months = new List<(DateOnly Month, decimal Amount)>();
        for (int i = TrendMonths - 1; i >= 0; i--)
        {
            var month = thisMonth.AddMonths(-i);
            totals.TryGetValue(month, out var amount);
            months.Add((month, amount));
        }

        decimal peak = months.Count == 0 ? 0 : months.Max(m => m.Amount);

        TrendBars = [.. months.Select(m => new MonthBar
        {
            Label = m.Month.ToString("M月"),
            Amount = m.Amount,
            HeightFraction = peak == 0 ? 0 : (double)(m.Amount / peak)
        })];

        PeakMonthText = peak == 0
            ? ""
            : $"最高 {months.First(m => m.Amount == peak).Month:M月}";

        OnPropertyChanged(nameof(TrendBars));
        OnPropertyChanged(nameof(PeakMonthText));
    }

    // ---- 导出 ----

    /// <summary>
    /// 把当前范围内的流水导出成 CSV。写 UTF-8 **带 BOM**：没有 BOM 时 Excel 会按本地代码页
    /// 解释，中文分类与描述全是乱码 —— 这是导出功能最常见的"看起来能用其实不能用"。
    /// </summary>
    private void ExportCsv()
    {
        try
        {
            var (from, to) = RangeBounds();
            var rows = _rangeEntries.OrderBy(e => e.Date).ToList();

            var folder = Path.Combine(Constants.AppDataFolder, "Export");
            Directory.CreateDirectory(folder);

            var name = $"papercoin-统计-{RangeText.Replace(" ", "")}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
            var path = Path.Combine(folder, name);

            var csv = new StringBuilder();
            csv.AppendLine("日期,描述,分类,金额");
            foreach (var e in rows)
                csv.AppendLine(string.Join(',',
                    e.Date.ToString("yyyy-MM-dd"), Escape(e.Description),
                    Escape(CategoryPalette.Normalize(e.Category)), e.Amount.ToString("0.00")));

            csv.AppendLine();
            csv.AppendLine($"支出合计,{TotalExpense:0.00}");
            csv.AppendLine($"收入合计,{TotalIncome:0.00}");
            csv.AppendLine($"笔数,{EntryCount}");

            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Notice = $"已导出 {rows.Count} 笔到 {path}";
            Log.Information("统计导出完成: {Path} ({Count} 笔)", path, rows.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 命令是同步的，异常外抛会成为未处理异常
            Notice = "导出失败，请检查磁盘权限或空间";
            Log.Warning(ex, "统计导出失败");
        }
    }

    /// <summary>CSV 字段转义：含逗号、引号或换行时用双引号包住，内部引号翻倍。</summary>
    private static string Escape(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;

        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
