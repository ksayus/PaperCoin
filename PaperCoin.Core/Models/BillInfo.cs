using CommunityToolkit.Mvvm.ComponentModel;

namespace PaperCoin.Core.Models;

public class BillDayFile
{
    public DateOnly Date { get; set; }
    public List<BillEntry> Items { get; set; } = new();
}

/// <summary>
/// 一条账单流水，也是列表卡片的绑定源；通知由 <c>[ObservableProperty]</c> 源生成器产生。
/// </summary>
public partial class BillEntry : ObservableObject
{
    /// <summary>业务主键；更新与删除一律按 Id 定位，不依赖对象引用相等。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>账单描述，列表卡片主标题</summary>
    [ObservableProperty]
    private string _description = "";

    /// <summary>金额，正数为收入，负数为支出；变更时连带通知 <see cref="AmountText"/>。</summary>
    [ObservableProperty]
    private decimal _amount;

    /// <summary>分类，允许为空</summary>
    [ObservableProperty]
    private string? _category;

    /// <summary>
    /// 发生日期。模型统一用 <see cref="DateOnly"/>；WPF DatePicker 需要
    /// <c>DateTime?</c>，转换只在 ViewModel 的草稿字段上进行。
    /// </summary>
    [ObservableProperty]
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>列表卡片副标题的日期文本</summary>
    public string DateText => Date.ToString("MM-dd");

    /// <summary>
    /// 分类对应的主题颜色键，供卡片上的色条使用。
    /// <para>
    /// 只给键名不给颜色：颜色字面量归主题字典所有，界面用转换器解析，
    /// 这样主题切换时色条会跟着一起过渡。归一化（大小写、空白、空分类）
    /// 由 <see cref="CategoryPalette"/> 独占，各处不重复实现。
    /// </para>
    /// </summary>
    public string CategoryColorKey => CategoryPalette.ThemeKeyOf(Category);

    /// <summary>
    /// 带符号金额文本。正负号手动补，不用 <c>N2</c> 格式串：其对负数的输出受区域设置影响。
    /// </summary>
    public string AmountText => Amount >= 0
        ? $"+{Amount:N2}"
        : $"-{Math.Abs(Amount):N2}";

    // 生成器声明的部分方法钩子，只能提供实现体，不能改签名。

    partial void OnAmountChanged(decimal value) => OnPropertyChanged(nameof(AmountText));

    partial void OnDateChanged(DateOnly value) => OnPropertyChanged(nameof(DateText));

    /// <summary>改了分类要连带通知色条，否则卡片会一直用旧颜色。</summary>
    partial void OnCategoryChanged(string? value) => OnPropertyChanged(nameof(CategoryColorKey));
}

public class TermsFile
{
    public List<Term> Terms { get; set; } = new();
}

/// <summary>单学期账单</summary>
public class Term
{
    public string Name { get; init; } = "";
    public DateOnly Start { get; init; }
    public DateOnly End { get; init; }

    public bool Contains(DateOnly date) => date >= Start && date <= End;
}

public class BillBool
{
    private readonly List<BillItem> _items = new();

    public IReadOnlyList<BillItem> Items => _items;

    public void Add(BillItem item) => _items.Add(item);
    public bool Remove(Guid id)
    {
        var item = _items.FirstOrDefault(x => x.Id == id);
        return item != null && _items.Remove(item);
    }

    public IEnumerable<BillItem> GetByDay(DateOnly date)
        => _items.Where(x => x.Date == date);

    public IEnumerable<BillItem> GetByMonth(int year, int month)
        => _items.Where(x => x.Date.Year == year && x.Date.Month == month);

    public IEnumerable<BillItem> GetByTerm(Term term)
        => _items.Where(x => term.Contains(x.Date));

    public IEnumerable<BillItem> GetByYear(int year)
        => _items.Where(x => x.Date.Year == year);

    public BillSummary Summary(IEnumerable<BillItem> items)
    {
        var list = items as IList<BillItem> ?? items.ToList();
        return new BillSummary
        {
            Total = list.Sum(x => x.Amount),
            Count = list.Count
        };
    }

    public BillSummary SummaryByDay(DateOnly date)
        => Summary(GetByDay(date));

    public BillSummary SummaryByMonth(int year, int month)
        => Summary(GetByMonth(year, month));

    public BillSummary SummaryByDay(Term term)
        => Summary(GetByTerm(term));

    public BillSummary SummaryByYear(int year)
        => Summary(GetByYear(year));
}

public class BillSummary
{
    public decimal Total { get; init; }
    public int Count { get; init; }
    public decimal Average => Count == 0 ? 0 : Total / Count;
}

public class BillItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateOnly Date { get; init; }
    public string Description { get; init; } = "";
    public decimal Amount { get; set; } = 0;
    public string? Category { get; init; }
}