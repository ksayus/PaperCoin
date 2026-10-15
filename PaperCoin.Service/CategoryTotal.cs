namespace PaperCoin.Service;

/// <summary>
/// 某个分类在指定时间范围内的支出合计。
/// <para>
/// <see cref="Expense"/> 是**正数**：它表达"这个分类花了多少"，不是流水里的带符号金额。
/// 金额正负是领域约定（正数为收入、负数为支出），收入不进这个聚合，符号在这里没有意义，
/// 留着负号只会让每个消费方各自再取一次绝对值。
/// </para>
/// </summary>
/// <param name="Category">分类名，空白归并为 <c>未分类</c></param>
/// <param name="Expense">支出合计，恒为正数</param>
/// <param name="Count">该分类在这个范围内的流水笔数</param>
public sealed record CategoryTotal(string Category, decimal Expense, int Count);
