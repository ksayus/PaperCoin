using System.Text.Json;
using PaperCoin.Core;
using PaperCoin.Core.Models;
using Serilog;

namespace PaperCoin.Service;

/// <summary>
/// 账单持久化与查询。磁盘布局为 <c>Bills/yyyy/M/d/bills.json</c>，年月日已经是路径的一部分，
/// 所以没有独立的索引文件，查询直接枚举目录，用户手工增删目录也能被正确识别。
/// 首次访问时构建一次“日期 → 流水”的内存索引并常驻，避免翻月翻年时反复遍历目录。
/// </summary>
public class BillRepository
{
    private const string IndexFileName = "bills.json";

    /// <summary>保护索引的构建与替换；磁盘遍历在锁外完成，不阻塞查询。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 串行化写入与删除。保存是即发即忘的，两个写句柄落在同一个文件上会让后一个抛
    /// <see cref="IOException"/>；删除与写入撞在一起时，写还可能把删掉的记录写回来。
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private readonly Dictionary<DateOnly, List<BillEntry>> _index = [];

    private bool _indexStale = true;

    /// <summary>索引版本号，见 <see cref="Revision"/>。写入只在 UI 线程发生，读用 volatile 保证可见性。</summary>
    private volatile int _revision;

    // ---- 查询 ----

    /// <summary>读取某一天的流水，按录入顺序。没有记录时返回空集合。</summary>
    public async Task<IReadOnlyList<BillEntry>> LoadDayAsync(DateOnly date, CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct);

        lock (_index)
        {
            return _index.TryGetValue(date, out var list)
                ? list.ToList()          // 复制一份，不把内部集合暴露出去
                : [];
        }
    }

    /// <summary>读取整月流水</summary>
    public async Task<IReadOnlyList<BillEntry>> LoadMonthAsync(int year, int month, CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct);

        lock (_index)
        {
            return _index
                .Where(kv => kv.Key.Year == year && kv.Key.Month == month)
                .OrderBy(kv => kv.Key)
                .SelectMany(kv => kv.Value)
                .ToList();
        }
    }

    /// <summary>读取整年流水</summary>
    public async Task<IReadOnlyList<BillEntry>> LoadYearAsync(int year, CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct);

        lock (_index)
        {
            return _index
                .Where(kv => kv.Key.Year == year)
                .OrderBy(kv => kv.Key)
                .SelectMany(kv => kv.Value)
                .ToList();
        }
    }

    /// <summary>
    /// 一次性取回全部流水，日期升序。规模不大（单机记账，一年通常几千条以内），
    /// 用于“全部时间”视图或导出。
    /// </summary>
    public async Task<IReadOnlyList<BillEntry>> LoadAllAsync(CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct);

        lock (_index)
        {
            return _index
                .OrderBy(kv => kv.Key)
                .SelectMany(kv => kv.Value)
                .ToList();
        }
    }

    /// <summary>已落盘的日期集合，供日历判断某天是否有记录</summary>
    public async Task<IReadOnlyCollection<DateOnly>> GetRecordedDatesAsync(CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct);

        lock (_index)
        {
            return _index.Keys.ToList();
        }
    }

    /// <summary>
    /// 指定日期区间内**按分类汇总的支出**，金额降序，金额相同按分类名排。
    /// <para>
    /// 只统计支出（金额为负），返回值里的金额取绝对值，语义见 <see cref="CategoryTotal"/>。
    /// 收支口径归领域层解释：正负约定写在 README 里，放界面层会让同一条约定散在多处。
    /// </para>
    /// <para>首尾两天都算在内。分类名做大小写不敏感归并，空白归入 <c>未分类</c>。</para>
    /// </summary>
    public async Task<IReadOnlyList<CategoryTotal>> GetCategoryExpenseAsync(
        DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from) (from, to) = (to, from);

        await EnsureIndexAsync(ct);

        lock (_index)
        {
            return _index
                .Where(kv => kv.Key >= from && kv.Key <= to)
                .SelectMany(kv => kv.Value)
                .Where(e => e.Amount < 0)
                .GroupBy(e => Core.CategoryPalette.Normalize(e.Category), StringComparer.OrdinalIgnoreCase)
                .Select(g => new CategoryTotal(g.Key, g.Sum(e => -e.Amount), g.Count()))
                .OrderByDescending(t => t.Expense)
                .ThenBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// 作废内存索引，下一次查询按磁盘重建。
    /// 正常写路径（<see cref="SaveAsync"/> / <see cref="UpsertEntryAsync"/> / <see cref="DeleteDayAsync"/>）
    /// 已经在维护索引，不需要调用；只有"磁盘被本进程以外的东西改过"时才需要。
    /// </summary>
    public void InvalidateIndex()
    {
        _indexStale = true;
        _revision++;
    }

    /// <summary>
    /// 索引版本号，单调递增。统计这类派生数据用它判断"要不要重算"：
    /// 每次切回页面都重算全量聚合是白做的，而每次都不算是错的。
    /// <para>
    /// 磁盘内容被改动的每条路径都会推它：三条写路径直接推，<see cref="InvalidateIndex"/> 推。
    /// 只推"索引重建"是不够的 —— 重建只发生一次，之后的编辑不再改这个号，
    /// 以它为失效依据会让统计页永远停在第一次的样子。
    /// </para>
    /// </summary>
    public int Revision => _revision;

    /// <summary>
    /// 账单里出现过的全部分类，按用过的次数从多到少排，次数相同按名称排。
    /// 汇总自内存索引，不额外读盘；同名分类只保留首次出现时的写法。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct);

        lock (_index)
        {
            return _index.Values
                .SelectMany(list => list)
                .Select(e => e.Category)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c!.Trim())
                .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .ToList();
        }
    }

    // ---- 写入 ----

    /// <summary>
    /// 保存流水，按每条记录自身的 <see cref="BillEntry.Date"/> 分组写入对应日期的文件。
    /// 单日文件是该日期的全部内容，所以逐日覆盖写是安全且幂等的。
    /// </summary>
    public async Task SaveAsync(IEnumerable<BillEntry> entries, CancellationToken ct = default)
    {
        // 分组是纯内存操作，放在锁外
        var byDate = entries
            .GroupBy(e => e.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        await _writeGate.WaitAsync(ct);
        try
        {
            foreach (var (date, list) in byDate)
                await WriteDayAsync(date, list, ct);

            // 只影响已存在的日期分组，索引结构不用重建，直接同步内容
            lock (_index)
            {
                foreach (var (date, list) in byDate)
                    _index[date] = list;
            }

            _revision++;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 删除某一天的全部流水：删掉那一天的 bills.json，并收掉因此变空的目录。
    /// 空集合表达不了“这一天应该是空的”，所以这个语义只能由调用方显式调用本方法；
    /// 调用方必须 await，因为下次启动读到的是磁盘。
    /// </summary>
    /// <returns>磁盘是否真的清干净了。失败时调用方应当告知用户。</returns>
    public async Task<bool> DeleteDayAsync(DateOnly date, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            var dir = DayDirectory(date);
            var file = Path.Combine(dir, IndexFileName);

            if (File.Exists(file)) File.Delete(file);

            PruneEmptyFolders(dir);

            // 磁盘已经是空的，索引跟着改掉即可
            lock (_index) _index.Remove(date);

            _revision++;

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 索引不动：磁盘上还有数据，内存与磁盘保持一致
            Log.Warning(ex, "删除账单文件失败: {Date}", date);
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 删除某一天里的**一条**流水，保留同一天的其它记录。它是日历页在月 / 年视图下删除的唯一正确做法：
    /// 那里列表混了多天，看不到的那几天不能跟着被覆盖掉（<see cref="SaveAsync"/> 是按记录自身日期分组的，
    /// 用当前列表整片回写会把其它日期的内容当成"这一天就这些"）。
    /// </summary>
    /// <param name="date">这条流水所在的日期</param>
    /// <param name="id">要删的记录 Id</param>
    /// <returns>是否落盘成功。失败时调用方应当告知用户。</returns>
    public async Task<bool> DeleteEntryAsync(DateOnly date, Guid id, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await RemoveEntryFromDayAsync(date, id, ct);

            // 索引已在 RemoveEntryFromDayAsync 里同步；这里只推进版本，
            // 让统计页那类按 Revision 判断"要不要重算"的消费方能察觉
            _revision++;

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "删除流水失败: {Date}/{Id}", date, id);
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 新增或编辑一条流水：写入它自身的日期，并保留该日期已有的其它记录。
    /// 编辑时若改了日期（<paramref name="previousDate"/> 与 <see cref="BillEntry.Date"/> 不同），
    /// 再从原日期摘掉这一条。两次磁盘操作在同一次写锁内完成。
    /// </summary>
    /// <returns>是否落盘成功。失败时调用方应当告知用户。</returns>
    public async Task<bool> UpsertEntryAsync(
        BillEntry entry, DateOnly? previousDate = null, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            if (previousDate is { } old && old != entry.Date)
                await RemoveEntryFromDayAsync(old, entry.Id, ct);

            await MergeEntryIntoDayAsync(entry, ct);

            _revision++;

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "保存账单失败: {Date}", entry.Date);
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>把一条流水写进它自身的日期文件：同 Id 覆盖，新 Id 追加，其余记录不动。</summary>
    private async Task MergeEntryIntoDayAsync(BillEntry entry, CancellationToken ct)
    {
        var file = Path.Combine(DayDirectory(entry.Date), IndexFileName);
        var list = File.Exists(file) ? await ReadDayFileAsync(file, ct) : [];

        int existing = list.FindIndex(e => e.Id == entry.Id);
        if (existing >= 0) list[existing] = entry;
        else list.Add(entry);

        await WriteDayAsync(entry.Date, list, ct);

        lock (_index) _index[entry.Date] = list;
    }

    /// <summary>从某天的文件里摘掉一条流水；该日期因此变空时连同目录一起收掉。</summary>
    private async Task RemoveEntryFromDayAsync(DateOnly date, Guid id, CancellationToken ct)
    {
        var file = Path.Combine(DayDirectory(date), IndexFileName);
        if (!File.Exists(file)) return;

        var list = await ReadDayFileAsync(file, ct);
        if (list.RemoveAll(e => e.Id == id) == 0) return;

        if (list.Count == 0)
        {
            File.Delete(file);
            PruneEmptyFolders(DayDirectory(date));

            lock (_index) _index.Remove(date);
            return;
        }

        await WriteDayAsync(date, list, ct);

        lock (_index) _index[date] = list;
    }

    /// <summary>
    /// 自下而上收掉因此变空的日 / 月 / 年目录。非空或被占用时停止，所以不能传
    /// <c>recursive: true</c>；循环上限 3 层，保证不会动到 <see cref="Constants.BillFolder"/> 本身。
    /// </summary>
    private static void PruneEmptyFolders(string dayDir)
    {
        var current = dayDir;

        for (int depth = 0; depth < 3 && !string.IsNullOrEmpty(current); depth++)
        {
            if (!Directory.Exists(current)) return;

            try
            {
                Directory.Delete(current, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 非空或被占用：到此为止，更上层肯定也非空
                return;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>某一天的目录 <c>Bills/yyyy/M/d</c>。读、写、删三条路径共用这一处实现。</summary>
    private static string DayDirectory(DateOnly date)
        => Path.Combine(
            Constants.BillFolder,
            date.Year.ToString(),
            date.Month.ToString(),
            date.Day.ToString());

    private static Task WriteDayAsync(DateOnly date, IReadOnlyList<BillEntry> entries, CancellationToken ct)
    {
        var dir = DayDirectory(date);

        Directory.CreateDirectory(dir);

        return BillJsonManager.SaveToFileAsync(Path.Combine(dir, IndexFileName), entries, ct);
    }

    // ---- 索引 ----

    private async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (!_indexStale) return;

        await _gate.WaitAsync(ct);
        try
        {
            // 等锁期间可能已有其它调用把索引建好了
            if (!_indexStale) return;

            var built = await BuildIndexAsync(ct);

            lock (_index)
            {
                _index.Clear();
                foreach (var (date, list) in built)
                    _index[date] = list;
            }

            _indexStale = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 遍历 Bills 目录构建 <c>日期 → 流水</c> 映射。目录名解析不出合法日期的直接跳过，
    /// 用户放在这里的备份文件夹或空目录不该让日历打不开。
    /// </summary>
    private static async Task<Dictionary<DateOnly, List<BillEntry>>> BuildIndexAsync(CancellationToken ct)
    {
        var index = new Dictionary<DateOnly, List<BillEntry>>();

        if (!Directory.Exists(Constants.BillFolder))
            return index;

        foreach (var yearDir in Directory.EnumerateDirectories(Constants.BillFolder))
        {
            if (!int.TryParse(Path.GetFileName(yearDir), out var year)) continue;

            foreach (var monthDir in Directory.EnumerateDirectories(yearDir))
            {
                if (!int.TryParse(Path.GetFileName(monthDir), out var month)) continue;

                foreach (var dayDir in Directory.EnumerateDirectories(monthDir))
                {
                    if (!int.TryParse(Path.GetFileName(dayDir), out var day)) continue;
                    if (!DateOnly.TryParse($"{year}/{month}/{day}", out var date)) continue;

                    var file = Path.Combine(dayDir, IndexFileName);
                    if (!File.Exists(file)) continue;

                    var list = await ReadDayFileAsync(file, ct);
                    if (list.Count > 0)
                        index[date] = list;
                }
            }
        }

        return index;
    }

    /// <summary>
    /// 读取单个日文件。文件损坏（手工编辑出错、写入被中断）时记日志并跳过，而不是抛出，
    /// 避免一个坏文件让整个月的账都看不见。
    /// </summary>
    private static async Task<List<BillEntry>> ReadDayFileAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            if (string.IsNullOrWhiteSpace(json)) return [];

            return JsonSerializer.Deserialize<List<BillEntry>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "账单文件读取失败, 已跳过: {Path}", path);
            return [];
        }
    }

    /// <summary>
    /// 与 <see cref="BillJsonManager"/> 保持一致的序列化配置，两处必须同步，
    /// 否则会出现写得进去却读不出来。这里不复用它的 <c>FromJSON</c>，因为本类要跳过坏文件。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
