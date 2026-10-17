package com.ksayus.papercoin.vm

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.ksayus.papercoin.core.Formats
import com.ksayus.papercoin.data.BillEntry
import com.ksayus.papercoin.data.BillRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import java.time.LocalDate
import java.util.UUID

/**
 * 新增 / 编辑一笔流水的草稿状态。今日页与日历页各持有一个实例，
 * 面板外观由 Compose 的 BillEditorSheet 提供，这里不碰任何 UI 元素。
 */
class BillEditorState(
    private val repository: BillRepository,
    private val scope: CoroutineScope,
) {
    var isOpen by mutableStateOf(false)
        private set

    var title by mutableStateOf("新增账单")
        private set

    var isSaving by mutableStateOf(false)
        private set

    private var _description by mutableStateOf("")
    private var _amountText by mutableStateOf("")
    private var touched = false
    private var editing: BillEntry? = null

    var description: String
        get() = _description
        set(value) {
            _description = value
            touched = true
            validate()
        }

    /** 金额以字符串承载：用户输入 "-" 或 "12." 这类中间态时不会触发绑定转换失败。 */
    var amountText: String
        get() = _amountText
        set(value) {
            _amountText = value
            touched = true
            validate()
        }

    var category by mutableStateOf("")

    var date by mutableStateOf(LocalDate.now())

    var categories by mutableStateOf<List<String>>(emptyList())
        private set

    var errorMessage by mutableStateOf("")
        private set

    var hasError by mutableStateOf(true)
        private set

    /** 落盘成功后调用，宿主在这里刷新自己的列表 */
    var saved: ((BillEntry) -> Unit)? = null

    /** 落盘失败时调用，参数是给用户看的提示文本 */
    var saveFailed: ((String) -> Unit)? = null

    fun openForCreate(date: LocalDate) {
        editing = null
        touched = false

        title = "新增账单"
        _description = ""
        _amountText = ""
        category = ""
        this.date = date
        errorMessage = ""

        validate()
        refreshCategories()

        isOpen = true
    }

    fun openForEdit(entry: BillEntry) {
        editing = entry
        touched = true

        title = "编辑账单"
        _description = entry.description
        _amountText = Formats.compactAmount(entry.amount)
        category = entry.category.orEmpty()
        date = entry.date
        errorMessage = ""

        validate()
        refreshCategories()

        isOpen = true
    }

    fun close() {
        editing = null
        errorMessage = ""
        isOpen = false
    }

    fun save() {
        if (hasError || isSaving) return

        val amount = amountText.trim().toBigDecimalOrNull() ?: return
        val previous = editing

        val target = BillEntry(
            id = previous?.id ?: UUID.randomUUID().toString(),
            description = description.trim(),
            amount = amount,
            category = category.trim().ifBlank { null },
            date = date,
        )

        isSaving = true

        scope.launch {
            val ok = try {
                repository.upsertEntry(target, previous?.date)
            } catch (ex: Exception) {
                false
            }

            isSaving = false

            if (!ok) {
                saveFailed?.invoke("保存失败，请检查磁盘权限或空间")
                return@launch
            }

            editing = null
            errorMessage = ""
            isOpen = false

            categories = try {
                repository.getCategories()
            } catch (ex: Exception) {
                categories
            }

            saved?.invoke(target)
        }
    }

    private fun refreshCategories() {
        scope.launch {
            categories = try {
                repository.getCategories()
            } catch (ex: Exception) {
                categories
            }
        }
    }

    private fun validate() {
        val amount = amountText.trim().toBigDecimalOrNull()

        val error = when {
            description.isBlank() -> "请填写账单描述"
            amount == null -> "金额需要是数字, 例如 -12.50"
            amount.signum() == 0 -> "金额不能为 0"
            else -> ""
        }

        hasError = error.isNotEmpty()
        errorMessage = if (touched) error else ""
    }
}
