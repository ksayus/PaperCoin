package com.ksayus.papercoin.data

import java.time.LocalDate

/** 一次同步对本地仓库的最小变更。删除必须排在同一个 id 的 upsert 之前。 */
sealed interface BillChange {
    data class Upsert(val entry: BillEntry) : BillChange

    data class Delete(val id: String, val date: LocalDate) : BillChange
}
