package com.ksayus.papercoin.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SemanticVersionTest {
    @Test
    fun `parses tags with v prefix and missing segments`() {
        assertEquals(SemanticVersion(1, 3, 6), SemanticVersion.parse("v1.3.6"))
        assertEquals(SemanticVersion(1, 3, 0), SemanticVersion.parse("1.3"))
        assertEquals(SemanticVersion(2, 0, 0), SemanticVersion.parse("2"))
    }

    @Test
    fun `ignores pre-release and build suffixes`() {
        assertEquals(SemanticVersion(1, 3, 6), SemanticVersion.parse("1.3.6-beta.1"))
        assertEquals(SemanticVersion(1, 3, 6), SemanticVersion.parse("v1.3.6+20261018"))
        assertEquals(SemanticVersion(1, 3, 7), SemanticVersion.parse("1.3.7-debug"))
    }

    @Test
    fun `rejects non version tags`() {
        assertNull(SemanticVersion.parse(null))
        assertNull(SemanticVersion.parse(""))
        assertNull(SemanticVersion.parse("latest"))
        assertNull(SemanticVersion.parse("v1.x.0"))
    }

    @Test
    fun `compares numerically instead of lexically`() {
        assertTrue(SemanticVersion.parse("1.3.10")!! > SemanticVersion.parse("1.3.9")!!)
        assertEquals(0, SemanticVersion.parse("1.3")!!.compareTo(SemanticVersion.parse("1.3.0")!!))
    }
}
