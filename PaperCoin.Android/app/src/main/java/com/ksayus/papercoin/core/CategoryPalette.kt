package com.ksayus.papercoin.core

import java.util.Locale

/**
 * 分类到颜色的稳定映射。只产出键名，不产出颜色值，颜色由主题层解析。
 *
 * 哈希算法必须与桌面版 `CategoryPalette.cs` 完全一致（FNV-1a over UTF-16 code units），
 * 否则同一个分类在两端的圆环与色条上会是两种颜色。
 */
object CategoryPalette {
    /** 与主题里的 CategoryColor01…CategoryColor10 对应 */
    const val SLOT_COUNT = 10

    /** 分类为空时的归并名 */
    const val UNCATEGORIZED = "未分类"

    /** 把分类名归一化成显示用的名字 */
    fun normalize(category: String?): String =
        if (category.isNullOrBlank()) UNCATEGORIZED else category.trim()

    /** 按分类名取颜色键；空白分类固定落在 CategoryColorOther */
    fun themeKeyOf(category: String?): String =
        if (category.isNullOrBlank()) themeKeyOfSlot(SLOT_COUNT) else themeKeyOfSlot(slotOf(category))

    /** 分类名落在哪个槽位，取值 [0, SLOT_COUNT)，与大小写和首尾空格无关 */
    fun slotOf(category: String?): Int {
        if (category.isNullOrBlank()) return SLOT_COUNT

        var hash: UInt = 2166136261u
        for (ch in category.trim()) {
            // 大写折成小写，让 "Food" 与 "food" 落到同一个槽位
            val lower = ch.lowercaseChar().code
            hash = (hash xor (lower and 0xFF).toUInt()) * 16777619u
            hash = (hash xor ((lower shr 8) and 0xFF).toUInt()) * 16777619u
        }

        return (hash % SLOT_COUNT.toUInt()).toInt()
    }

    /** 槽位号取颜色键；槽位等于 SLOT_COUNT 时返回未分类专用键 */
    fun themeKeyOfSlot(slot: Int): String =
        if (slot >= SLOT_COUNT) "CategoryColorOther"
        else String.format(Locale.ROOT, "CategoryColor%02d", slot + 1)
}
