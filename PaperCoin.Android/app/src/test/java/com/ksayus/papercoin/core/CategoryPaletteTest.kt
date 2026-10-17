package com.ksayus.papercoin.core

import org.junit.Assert.assertEquals
import org.junit.Test

class CategoryPaletteTest {
    @Test
    fun `slots match the desktop FNV-1a implementation`() {
        // 期望值由独立的 Python FNV-1a 实现对同一批字符串算出，用来锁住跨端配色一致。
        assertEquals(3, CategoryPalette.slotOf("餐饮"))
        assertEquals(9, CategoryPalette.slotOf("交通"))
        assertEquals(4, CategoryPalette.slotOf("购物"))
        assertEquals(8, CategoryPalette.slotOf("工资"))
        assertEquals(6, CategoryPalette.slotOf("娱乐"))
    }

    @Test
    fun `hash ignores case and surrounding spaces`() {
        assertEquals(CategoryPalette.slotOf("Food"), CategoryPalette.slotOf("food"))
        assertEquals(CategoryPalette.slotOf("Food"), CategoryPalette.slotOf("  Food  "))
    }

    @Test
    fun `blank category has its own neutral color`() {
        assertEquals(CategoryPalette.SLOT_COUNT, CategoryPalette.slotOf(null))
        assertEquals(CategoryPalette.SLOT_COUNT, CategoryPalette.slotOf("   "))
        assertEquals("CategoryColorOther", CategoryPalette.themeKeyOf(""))
        assertEquals("CategoryColorOther", CategoryPalette.themeKeyOfSlot(10))
    }

    @Test
    fun `color keys are one based and zero padded`() {
        assertEquals("CategoryColor01", CategoryPalette.themeKeyOfSlot(0))
        assertEquals("CategoryColor10", CategoryPalette.themeKeyOfSlot(9))
    }

    @Test
    fun `normalize maps blank names to the uncategorized label`() {
        assertEquals("未分类", CategoryPalette.normalize(null))
        assertEquals("未分类", CategoryPalette.normalize("  "))
        assertEquals("餐饮", CategoryPalette.normalize(" 餐饮 "))
    }
}
