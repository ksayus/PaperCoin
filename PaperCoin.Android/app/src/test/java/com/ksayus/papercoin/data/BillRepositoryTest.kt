package com.ksayus.papercoin.data

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File
import java.math.BigDecimal
import java.time.LocalDate

class BillRepositoryTest {
    @get:Rule
    val temp = TemporaryFolder()

    private val day1 = LocalDate.of(2026, 10, 18)
    private val day2 = LocalDate.of(2026, 10, 19)

    private fun repository() = BillRepository(File(temp.root, "Bills"))

    private fun entry(
        description: String,
        amount: String,
        category: String?,
        date: LocalDate = day1,
        id: String = java.util.UUID.randomUUID().toString(),
    ) = BillEntry(
        id = id,
        description = description,
        amount = BigDecimal(amount),
        category = category,
        date = date,
    )

    private fun assertAmount(expected: String, actual: BigDecimal) {
        assertEquals("金额不一致: $actual", 0, BigDecimal(expected).compareTo(actual))
    }

    @Test
    fun `save writes the same nested layout as the desktop app`() = runBlocking {
        val repo = repository()
        val item = entry("午饭", "-12.5", "餐饮")

        assertTrue(repo.save(listOf(item)))

        val file = File(temp.root, "Bills/2026/10/18/bills.json")
        assertTrue(file.isFile)

        val json = file.readText()
        assertTrue(json.contains("\"date\": \"2026-10-18\""))
        assertTrue(json.contains("\"amount\": -12.5"))

        val loaded = repo.loadDay(day1)
        assertEquals(1, loaded.size)
        assertEquals(item.id, loaded[0].id)
        assertAmount("-12.5", loaded[0].amount)
    }

    @Test
    fun `upsert keeps the other entries of the same day`() = runBlocking {
        val repo = repository()
        val a = entry("A", "-10", "餐饮")
        val b = entry("B", "-20", "交通")
        repo.save(listOf(a, b))

        val edited = a.copy(description = "A2", amount = BigDecimal("-15"))
        assertTrue(repo.upsertEntry(edited))

        val loaded = repo.loadDay(day1)
        assertEquals(2, loaded.size)
        assertEquals("A2", loaded.first { it.id == a.id }.description)
        assertAmount("-15", loaded.first { it.id == a.id }.amount)
        assertAmount("-20", loaded.first { it.id == b.id }.amount)
    }

    @Test
    fun `editing the date moves only that entry`() = runBlocking {
        val repo = repository()
        val a = entry("A", "-10", "餐饮")
        val b = entry("B", "-20", "交通")
        repo.save(listOf(a, b))

        val moved = a.copy(date = day2)
        assertTrue(repo.upsertEntry(moved, previousDate = day1))

        val first = repo.loadDay(day1)
        val second = repo.loadDay(day2)

        assertEquals(1, first.size)
        assertEquals(b.id, first[0].id)
        assertEquals(1, second.size)
        assertEquals(a.id, second[0].id)
    }

    @Test
    fun `deleting one entry never wipes the other days`() = runBlocking {
        val repo = repository()
        val a = entry("A", "-10", "餐饮")
        val b = entry("B", "-20", "交通")
        val c = entry("C", "-30", "购物", date = day2)
        repo.save(listOf(a, b))
        repo.save(listOf(c))

        assertTrue(repo.deleteEntry(day1, a.id))

        val first = repo.loadDay(day1)
        val second = repo.loadDay(day2)

        assertEquals(1, first.size)
        assertEquals(b.id, first[0].id)
        assertEquals(1, second.size)
        assertEquals(c.id, second[0].id)
    }

    @Test
    fun `deleting the last entry of a day removes the file and empty folders`() = runBlocking {
        val repo = repository()
        val only = entry("A", "-10", "餐饮")
        repo.save(listOf(only))

        assertTrue(repo.deleteEntry(day1, only.id))

        assertFalse(File(temp.root, "Bills/2026/10/18/bills.json").exists())
        assertFalse(File(temp.root, "Bills/2026/10/18").exists())
        assertFalse(File(temp.root, "Bills/2026/10").exists())
        assertTrue(repo.loadDay(day1).isEmpty())
    }

    @Test
    fun `deleteDay clears the day but keeps other dates`() = runBlocking {
        val repo = repository()
        repo.save(listOf(entry("A", "-10", "餐饮")))
        repo.save(listOf(entry("B", "-20", "交通", date = day2)))

        assertTrue(repo.deleteDay(day1))

        assertTrue(repo.loadDay(day1).isEmpty())
        assertEquals(1, repo.loadDay(day2).size)
    }

    @Test
    fun `a corrupt day file is skipped instead of breaking the whole month`() = runBlocking {
        val repo = repository()
        repo.save(listOf(entry("A", "-10", "餐饮")))

        val brokenDir = File(temp.root, "Bills/2026/10/19")
        brokenDir.mkdirs()
        File(brokenDir, "bills.json").writeText("{ this is not json")

        val month = repo.loadMonth(2026, 10)
        assertEquals(1, month.size)
        assertEquals("A", month[0].description)
    }

    @Test
    fun `categories are ordered by usage and keep the first spelling`() = runBlocking {
        val repo = repository()
        repo.save(
            listOf(
                entry("a", "-1", "Food"),
                entry("b", "-1", "food"),
                entry("c", "-1", "交通"),
                entry("d", "-1", "Food"),
                entry("e", "-1", null),
            ),
        )

        assertEquals(listOf("Food", "交通"), repo.getCategories())
    }

    @Test
    fun `category expense only counts negative amounts and groups by name`() = runBlocking {
        val repo = repository()
        repo.save(
            listOf(
                entry("a", "-10", "Food"),
                entry("b", "-5", "food"),
                entry("c", "100", "Food"),
                entry("d", "-5", "交通"),
            ),
        )

        val totals = repo.getCategoryExpense(day1, day1)

        assertEquals(2, totals.size)
        assertEquals("Food", totals[0].category)
        assertAmount("15", totals[0].expense)
        assertEquals(2, totals[0].count)
        assertAmount("5", totals[1].expense)
    }

    @Test
    fun `mergeEntries adds new entries and replaces the same id`() = runBlocking {
        val repo = repository()
        val a = entry("A", "-10", "餐饮")
        val b = entry("B", "-20", "交通")
        repo.save(listOf(a, b))

        val c = entry("C", "-30", "购物")
        val changedB = b.copy(description = "B2")

        assertEquals(2, repo.mergeEntries(listOf(changedB, c)))

        val loaded = repo.loadDay(day1)
        assertEquals(3, loaded.size)
        assertEquals("B2", loaded.first { it.id == b.id }.description)
    }

    @Test
    fun `revision moves forward on every write path`() = runBlocking {
        val repo = repository()
        val start = repo.currentRevision

        repo.save(listOf(entry("A", "-10", "餐饮")))
        assertEquals(start + 1, repo.currentRevision)

        repo.upsertEntry(entry("B", "-20", "交通"))
        assertEquals(start + 2, repo.currentRevision)

        repo.deleteEntry(day1, repo.loadDay(day1).first().id)
        assertEquals(start + 3, repo.currentRevision)
    }

    @Test
    fun `empty save does not delete the day file`() = runBlocking {
        val repo = repository()
        repo.save(listOf(entry("A", "-10", "餐饮")))

        assertTrue(repo.save(emptyList()))
        assertEquals(1, repo.loadDay(day1).size)
    }
}
