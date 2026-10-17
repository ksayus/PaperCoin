package com.ksayus.papercoin.data

import android.content.Context
import android.net.Uri
import androidx.documentfile.provider.DocumentFile
import com.ksayus.papercoin.core.AppLog
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.decodeFromString
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.time.LocalDate
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream

/**
 * 备份与导入。Android 的私有目录不能被文件管理器直接访问，所以桌面版那种
 * "把 Bills 文件夹拷进去"的做法不可行；这里提供两条等价路径：
 *
 * - 导出 / 导入 ZIP（整棵 Bills 目录 + 配置）
 * - 直接选一个桌面版拷贝过来的文件夹（SAF 树选择器）
 *
 * 导入按 Id 合并，绝不整片覆盖：同一天里备份中没有的记录会原样保留。
 */
class BackupManager(
    private val context: Context,
    private val repository: BillRepository,
    private val dataRoot: File,
) {
    suspend fun exportZip(uri: Uri): String = withContext(Dispatchers.IO) {
        try {
            val resolver = context.contentResolver
            var days = 0

            val stream = resolver.openOutputStream(uri)
                ?: return@withContext "无法写入所选位置"

            stream.use { raw ->
                ZipOutputStream(BufferedOutputStream(raw)).use { zip ->
                    val billsRoot = File(dataRoot, "Bills")
                    if (billsRoot.isDirectory) {
                        billsRoot.walkTopDown()
                            .filter { it.isFile && it.name == BillRepository.BILLS_JSON }
                            .forEach { file ->
                                val relative = file.relativeTo(dataRoot).path
                                    .replace(File.separatorChar, '/')
                                zip.putNextEntry(ZipEntry(relative))
                                file.inputStream().use { it.copyTo(zip) }
                                zip.closeEntry()
                                days++
                            }
                    }

                    val configFile = File(dataRoot, "Config/papercoin-settings.json")
                    if (configFile.isFile) {
                        zip.putNextEntry(ZipEntry("Config/papercoin-settings.json"))
                        configFile.inputStream().use { it.copyTo(zip) }
                        zip.closeEntry()
                    }
                }
            }

            "已导出 $days 天账单"
        } catch (ex: Exception) {
            AppLog.warn("导出备份失败", ex)
            "导出失败：${ex.message ?: "未知错误"}"
        }
    }

    suspend fun importZip(uri: Uri): String = withContext(Dispatchers.IO) {
        try {
            val collected = mutableListOf<BillEntry>()
            var days = 0
            var files = 0

            val stream = context.contentResolver.openInputStream(uri)
                ?: return@withContext "无法读取所选文件"

            stream.use { raw ->
                ZipInputStream(BufferedInputStream(raw)).use { zip ->
                    var entry = zip.nextEntry

                    while (entry != null && files < MAX_FILES) {
                        if (!entry.isDirectory && dateFromPath(entry.name) != null) {
                            files++
                            val list = parseEntries(readLimited(zip))
                            if (!list.isNullOrEmpty()) {
                                collected += list
                                days++
                            }
                        }

                        zip.closeEntry()
                        entry = zip.nextEntry
                    }
                }
            }

            merge(collected, days)
        } catch (ex: Exception) {
            AppLog.warn("导入备份失败", ex)
            "导入失败：${ex.message ?: "未知错误"}"
        }
    }

    suspend fun importFolder(uri: Uri): String = withContext(Dispatchers.IO) {
        try {
            val root = DocumentFile.fromTreeUri(context, uri)
                ?: return@withContext "无法打开所选文件夹"

            val collected = mutableListOf<BillEntry>()
            var days = 0
            var visited = 0

            fun visit(dir: DocumentFile, path: List<String>, depth: Int) {
                if (depth > 6 || visited > MAX_FILES) return

                for (child in dir.listFiles()) {
                    visited++

                    if (child.isDirectory) {
                        visit(child, path + (child.name ?: ""), depth + 1)
                    } else if (child.name == BillRepository.BILLS_JSON) {
                        val fullPath = (path + (child.name ?: "")).joinToString("/")
                        if (dateFromPath(fullPath) == null) continue

                        val list = context.contentResolver.openInputStream(child.uri)?.use { input ->
                            parseEntries(readLimited(input))
                        }

                        if (!list.isNullOrEmpty()) {
                            collected += list
                            days++
                        }
                    }
                }
            }

            visit(root, emptyList(), 0)
            merge(collected, days)
        } catch (ex: Exception) {
            AppLog.warn("导入文件夹失败", ex)
            "导入失败：${ex.message ?: "未知错误"}"
        }
    }

    private suspend fun merge(entries: List<BillEntry>, days: Int): String {
        if (entries.isEmpty()) return "没有找到可导入的账单"

        val merged = repository.mergeEntries(entries)
        return if (merged < 0) {
            "导入失败，请检查存储空间"
        } else {
            "已导入 $merged 笔（$days 天）"
        }
    }

    /** 只读取当前 ZIP 条目，并且限制单条大小，避免一个异常备份把内存吃光。 */
    private fun readLimited(input: InputStream): String {
        val output = ByteArrayOutputStream()
        val buffer = ByteArray(8192)
        var total = 0

        while (true) {
            val read = input.read(buffer)
            if (read <= 0) break

            total += read
            if (total > MAX_ENTRY_BYTES) throw IOException("备份文件过大")

            output.write(buffer, 0, read)
        }

        return output.toString(Charsets.UTF_8.name())
    }

    private fun parseEntries(text: String): List<BillEntry>? {
        if (text.isBlank()) return emptyList()

        return try {
            PaperCoinJson.decodeFromString<List<BillEntry>>(text)
        } catch (ex: Exception) {
            AppLog.warn("备份中的账单文件解析失败，已跳过", ex)
            null
        }
    }

    /**
     * 从 "Bills/2026/10/18/bills.json" 或 "2026/10/18/bills.json" 这样的路径里取日期。
     * 只用来确认这是账单文件；真正的日期仍以文件内容里每条记录的 date 为准。
     */
    private fun dateFromPath(path: String): LocalDate? {
        val match = PATH_PATTERN.find(path) ?: return null
        val (year, month, day) = match.destructured

        return try {
            LocalDate.of(year.toInt(), month.toInt(), day.toInt())
        } catch (_: Exception) {
            null
        }
    }

    private companion object {
        const val MAX_FILES = 20_000
        const val MAX_ENTRY_BYTES = 8 * 1024 * 1024

        val PATH_PATTERN = Regex(
            """(?:^|/)(?:Bills/)?(\d{4})/(\d{1,2})/(\d{1,2})/bills\.json$""",
            RegexOption.IGNORE_CASE,
        )
    }
}
