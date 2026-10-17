package com.ksayus.papercoin.data

import com.ksayus.papercoin.core.AppLog
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerializationException
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import java.io.File
import java.io.IOException
import java.time.DateTimeException
import java.time.LocalDate
import java.util.Locale

/**
 * 账单持久化与查询。磁盘布局为 `Bills/yyyy/M/d/bills.json`，年月日已经是路径的一部分，
 * 所以没有独立的索引文件；查询直接枚举目录，用户手工增删目录也能被识别。
 *
 * 首次访问时构建一次"日期 → 流水"的内存索引并常驻，避免翻月翻年时反复遍历目录。
 * 所有磁盘操作都在单把互斥锁里串行执行 —— 两个写句柄落在同一个文件上会互相覆盖，
 * 删除与写入撞在一起时，写还可能把删掉的记录写回来。
 */
class BillRepository(private val billsRoot: File) {
    private val mutex = Mutex()

    /** null 表示尚未构建或已被 [invalidate] 作废 */
    private var index: MutableMap<LocalDate, MutableList<BillEntry>>? = null

    private val revisionFlow = MutableStateFlow(0)

    /** 索引版本号，单调递增。统计页用它判断"要不要重算"。 */
    val revision: StateFlow<Int> = revisionFlow.asStateFlow()

    val currentRevision: Int get() = revisionFlow.value

    // ---- 查询 ----

    suspend fun loadDay(date: LocalDate): List<BillEntry> = withContext(Dispatchers.IO) {
        mutex.withLock {
            ensureIndexLocked()
            index?.get(date)?.toList() ?: emptyList()
        }
    }

    suspend fun loadMonth(year: Int, month: Int): List<BillEntry> = withContext(Dispatchers.IO) {
        mutex.withLock {
            ensureIndexLocked()
            index.orEmpty().entries
                .filter { it.key.year == year && it.key.monthValue == month }
                .sortedBy { it.key }
                .flatMap { it.value.toList() }
        }
    }

    suspend fun loadYear(year: Int): List<BillEntry> = withContext(Dispatchers.IO) {
        mutex.withLock {
            ensureIndexLocked()
            index.orEmpty().entries
                .filter { it.key.year == year }
                .sortedBy { it.key }
                .flatMap { it.value.toList() }
        }
    }

    /** 一次性取回全部流水，日期升序。 */
    suspend fun loadAll(): List<BillEntry> = withContext(Dispatchers.IO) {
        mutex.withLock {
            ensureIndexLocked()
            allEntriesLocked()
        }
    }

    /** 当前全部账单的快照哈希，用于同步前的一致性校验。 */
    suspend fun snapshotHash(): String = withContext(Dispatchers.IO) {
        mutex.withLock {
            ensureIndexLocked()
            SyncHash.snapshotHash(allEntriesLocked())
        }
    }

    /**
     * 一次同步要落地的全部变更，必须在同一把写锁内完成：
     * 先校验"当前快照仍是同步开始时那份"，再逐条应用。这样同步过程中用户在界面上
     * 改的账不会被静默覆盖，而是让整次同步失败并重试。
     */
    suspend fun applySyncChanges(
        changes: List<BillChange>,
        expectedSnapshotHash: String? = null,
    ): Boolean = withContext(Dispatchers.IO) {
        mutex.withLock {
            try {
                ensureIndexLocked()

                if (expectedSnapshotHash != null) {
                    val current = SyncHash.snapshotHash(allEntriesLocked())
                    if (current != expectedSnapshotHash) return@withLock false
                }

                for (change in changes) {
                    when (change) {
                        is BillChange.Delete -> removeEntryFromDayLocked(change.date, change.id)
                        is BillChange.Upsert -> mergeEntryIntoDayLocked(change.entry)
                    }
                }

                if (changes.isNotEmpty()) bumpRevisionLocked()
                true
            } catch (ex: Exception) {
                if (!isStorageFailure(ex)) throw ex
                AppLog.warn("应用同步变更失败", ex)
                false
            }
        }
    }

    /** 已落盘的日期集合，供日历判断某天是否有记录。 */
    suspend fun getRecordedDates(): Set<LocalDate> = withContext(Dispatchers.IO) {
        mutex.withLock {
            ensureIndexLocked()
            index.orEmpty().keys.toSet()
        }
    }

    /**
     * 指定日期区间内按分类汇总的支出，金额降序，金额相同按分类名排。
     * 首尾两天都算在内；只统计支出，金额取绝对值。
     */
    suspend fun getCategoryExpense(from: LocalDate, to: LocalDate): List<CategoryTotal> =
        withContext(Dispatchers.IO) {
            val (start, end) = if (to < from) to to from else from to to
            mutex.withLock {
                ensureIndexLocked()
                val entries = index.orEmpty().entries
                    .filter { it.key >= start && it.key <= end }
                    .sortedBy { it.key }
                    .flatMap { it.value }
                computeCategoryTotals(entries)
            }
        }

    /**
     * 账单里出现过的全部分类，按用过的次数从多到少排，次数相同按名称排。
     * 汇总自内存索引，不额外读盘；同名分类只保留首次出现时的写法。
     */
    suspend fun getCategories(): List<String> = withContext(Dispatchers.IO) {
        mutex.withLock {
            ensureIndexLocked()

            val counts = LinkedHashMap<String, Pair<String, Int>>()

            for (list in index.orEmpty().values) {
                for (entry in list) {
                    val name = entry.category?.trim().orEmpty()
                    if (name.isEmpty()) continue

                    val key = name.lowercase(Locale.ROOT)
                    val current = counts[key]
                    counts[key] = if (current == null) name to 1 else current.first to current.second + 1
                }
            }

            counts.values
                .sortedWith(
                    compareByDescending<Pair<String, Int>> { it.second }
                        .thenBy { it.first.lowercase(Locale.ROOT) },
                )
                .map { it.first }
        }
    }

    // ---- 写入 ----

    /**
     * 保存流水，按每条记录自身的日期分组写入对应日期的文件。
     * 单日文件是该日期的全部内容，所以逐日覆盖写是安全且幂等的。
     * 空集合不会删掉任何日期文件；要清空某天必须显式调用 [deleteDay]。
     */
    suspend fun save(entries: Collection<BillEntry>): Boolean = withContext(Dispatchers.IO) {
        mutex.withLock {
            val byDate = entries.groupBy { it.date }
            if (byDate.isEmpty()) return@withLock true

            try {
                ensureIndexLocked()
                val current = index ?: return@withLock false

                for ((date, list) in byDate) {
                    writeDayLocked(date, list)
                    current[date] = list.toMutableList()
                }

                bumpRevisionLocked()
                true
            } catch (ex: Exception) {
                if (!isStorageFailure(ex)) throw ex
                AppLog.warn("保存账单失败", ex)
                false
            }
        }
    }

    /**
     * 删除某一天的全部流水：删掉那一天的 bills.json，并收掉因此变空的目录。
     * 返回磁盘是否真的清干净了。
     */
    suspend fun deleteDay(date: LocalDate): Boolean = withContext(Dispatchers.IO) {
        mutex.withLock {
            try {
                ensureIndexLocked()
                val dir = dayDirectory(date)
                val file = File(dir, BILLS_JSON)

                if (file.isFile && !file.delete()) {
                    throw IOException("无法删除账单文件: ${file.path}")
                }
                pruneEmptyFolders(dir)

                index?.remove(date)
                bumpRevisionLocked()
                true
            } catch (ex: Exception) {
                if (!isStorageFailure(ex)) throw ex
                AppLog.warn("删除账单文件失败: $date", ex)
                false
            }
        }
    }

    /**
     * 删除某一天里的一条流水，保留同一天的其它记录。日历页在月 / 年视图下删除时必须走这里：
     * 列表混了多天，整片回写会把列表里没有的日期当成"这一天清空了"。
     */
    suspend fun deleteEntry(date: LocalDate, id: String): Boolean = withContext(Dispatchers.IO) {
        mutex.withLock {
            try {
                ensureIndexLocked()
                removeEntryFromDayLocked(date, id)
                bumpRevisionLocked()
                true
            } catch (ex: Exception) {
                if (!isStorageFailure(ex)) throw ex
                AppLog.warn("删除流水失败: $date/$id", ex)
                false
            }
        }
    }

    /**
     * 新增或编辑一条流水：写入它自身的日期，并保留该日期已有的其它记录。
     * 编辑时若改了日期，再从原日期摘掉这一条。两次磁盘操作在同一次写锁内完成。
     */
    suspend fun upsertEntry(entry: BillEntry, previousDate: LocalDate? = null): Boolean =
        withContext(Dispatchers.IO) {
            mutex.withLock {
                try {
                    ensureIndexLocked()

                    if (previousDate != null && previousDate != entry.date) {
                        removeEntryFromDayLocked(previousDate, entry.id)
                    }

                    mergeEntryIntoDayLocked(entry)
                    bumpRevisionLocked()
                    true
                } catch (ex: Exception) {
                    if (!isStorageFailure(ex)) throw ex
                    AppLog.warn("保存账单失败: ${entry.date}", ex)
                    false
                }
            }
        }

    /**
     * 把一批流水按 Id 合并进已有的日期文件。用于导入桌面版数据：
     * 同一天的旧记录一条不丢，同 Id 的以导入内容为准。
     *
     * @return 合并的条数；失败时返回 -1。
     */
    suspend fun mergeEntries(entries: Collection<BillEntry>): Int = withContext(Dispatchers.IO) {
        mutex.withLock {
            if (entries.isEmpty()) return@withLock 0

            try {
                ensureIndexLocked()
                val current = index ?: return@withLock -1

                for ((date, incoming) in entries.groupBy { it.date }) {
                    val list = readDayFileLocked(File(dayDirectory(date), BILLS_JSON))

                    for (entry in incoming) {
                        val existing = list.indexOfFirst { it.id == entry.id }
                        if (existing >= 0) list[existing] = entry else list.add(entry)
                    }

                    writeDayLocked(date, list)
                    current[date] = list
                }

                bumpRevisionLocked()
                entries.size
            } catch (ex: Exception) {
                if (!isStorageFailure(ex)) throw ex
                AppLog.warn("导入账单失败", ex)
                -1
            }
        }
    }

    /**
     * 作废内存索引，下一次查询按磁盘重建。
     * 正常写路径已经在维护索引，不需要调用；只有"磁盘被本进程以外的东西改过"时才需要。
     */
    suspend fun invalidate() = withContext(Dispatchers.IO) {
        mutex.withLock {
            index = null
            bumpRevisionLocked()
        }
    }

    // ---- 单日文件 ----

    private fun mergeEntryIntoDayLocked(entry: BillEntry) {
        val file = File(dayDirectory(entry.date), BILLS_JSON)
        val list = readDayFileLocked(file)

        val existing = list.indexOfFirst { it.id == entry.id }
        if (existing >= 0) list[existing] = entry else list.add(entry)

        writeDayLocked(entry.date, list)
        index?.set(entry.date, list)
    }

    private fun removeEntryFromDayLocked(date: LocalDate, id: String) {
        val dir = dayDirectory(date)
        val file = File(dir, BILLS_JSON)
        if (!file.isFile) return

        val list = readDayFileLocked(file)
        if (!list.removeAll { it.id == id }) return

        if (list.isEmpty()) {
            if (!file.delete()) throw IOException("无法删除账单文件: ${file.path}")
            pruneEmptyFolders(dir)
            index?.remove(date)
            return
        }

        writeDayLocked(date, list)
        index?.set(date, list)
    }

    private fun writeDayLocked(date: LocalDate, entries: List<BillEntry>) {
        val dir = dayDirectory(date)
        if (!dir.isDirectory && !dir.mkdirs()) {
            throw IOException("无法创建目录: ${dir.path}")
        }

        File(dir, BILLS_JSON).writeText(PaperCoinJson.encodeToString(entries))
    }

    /**
     * 读取单个日文件。文件损坏（手工编辑出错、写入被中断）时记日志并跳过，而不是抛出，
     * 避免一个坏文件让整个月的账都看不见。
     */
    private fun readDayFileLocked(file: File): MutableList<BillEntry> {
        if (!file.isFile) return mutableListOf()

        return try {
            val text = file.readText()
            if (text.isBlank()) {
                mutableListOf()
            } else {
                PaperCoinJson.decodeFromString<List<BillEntry>>(text).toMutableList()
            }
        } catch (ex: Exception) {
            if (ex !is IOException && ex !is SecurityException && ex !is SerializationException) throw ex
            AppLog.warn("账单文件读取失败，已跳过: ${file.path}", ex)
            mutableListOf()
        }
    }

    // ---- 索引 ----

    private fun allEntriesLocked(): List<BillEntry> =
        index.orEmpty().entries
            .sortedBy { it.key }
            .flatMap { it.value.toList() }

    private fun ensureIndexLocked() {
        if (index != null) return
        val built = LinkedHashMap<LocalDate, MutableList<BillEntry>>()

        if (billsRoot.isDirectory) {
            for (yearDir in billsRoot.listFiles()?.filter { it.isDirectory }.orEmpty()) {
                val year = yearDir.name.toIntOrNull() ?: continue

                for (monthDir in yearDir.listFiles()?.filter { it.isDirectory }.orEmpty()) {
                    val month = monthDir.name.toIntOrNull() ?: continue

                    for (dayDir in monthDir.listFiles()?.filter { it.isDirectory }.orEmpty()) {
                        val day = dayDir.name.toIntOrNull() ?: continue

                        val date = try {
                            LocalDate.of(year, month, day)
                        } catch (_: DateTimeException) {
                            continue
                        }

                        val list = readDayFileLocked(File(dayDir, BILLS_JSON))
                        if (list.isNotEmpty()) built[date] = list
                    }
                }
            }
        }

        index = built
    }

    /**
     * 自下而上收掉因此变空的日 / 月 / 年目录。非空时停止，所以不能递归删除；
     * 循环上限 3 层，保证不会动到 Bills 根本身。
     */
    private fun pruneEmptyFolders(dayDir: File) {
        var current: File? = dayDir

        repeat(3) {
            val dir = current ?: return
            if (!dir.isDirectory) return
            if (dir.list()?.isNotEmpty() == true) return
            if (!dir.delete()) return

            current = dir.parentFile
        }
    }

    private fun dayDirectory(date: LocalDate): File =
        File(
            File(File(billsRoot, date.year.toString()), date.monthValue.toString()),
            date.dayOfMonth.toString(),
        )

    private fun bumpRevisionLocked() {
        revisionFlow.value = revisionFlow.value + 1
    }

    private fun isStorageFailure(ex: Exception): Boolean =
        ex is IOException || ex is SecurityException

    companion object {
        const val BILLS_JSON = "bills.json"
    }
}
