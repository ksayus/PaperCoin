package com.ksayus.papercoin

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import androidx.activity.ComponentActivity
import androidx.compose.runtime.Composable
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performScrollToIndex
import androidx.test.core.app.ApplicationProvider
import com.ksayus.papercoin.data.AppContainer
import com.ksayus.papercoin.data.BillEntry
import com.ksayus.papercoin.sync.ConflictKind
import com.ksayus.papercoin.sync.SyncConflict
import com.ksayus.papercoin.ui.PaperCoinApp
import com.ksayus.papercoin.ui.components.AppBackdrop
import com.ksayus.papercoin.ui.screens.CalendarScreen
import com.ksayus.papercoin.ui.screens.SettingsScreen
import com.ksayus.papercoin.ui.screens.StatisticsScreen
import com.ksayus.papercoin.ui.screens.SyncConflictDialogContent
import com.ksayus.papercoin.ui.screens.TodayScreen
import com.ksayus.papercoin.ui.theme.PaperCoinTheme
import com.ksayus.papercoin.ui.theme.ThemeState
import kotlinx.coroutines.runBlocking
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode
import java.io.File
import java.io.FileOutputStream
import java.math.BigDecimal
import java.time.LocalDate

/**
 * 无模拟器环境下的界面渲染回归：用 Robolectric 的 NATIVE 图形把真实 Compose 页面画出来，
 * 存成 PNG 供人工核对布局。它不是断言测试，产物在 app/build/screenshots/。
 */
@RunWith(RobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
@Config(sdk = [35], qualifiers = "w411dp-h891dp-xxhdpi")
class ScreenshotTest {
    @get:Rule
    val compose = createAndroidComposeRule<ComponentActivity>()

    private fun boot(
        mode: String = ThemeState.MODE_DARK,
        style: String = ThemeState.STYLE_CLASSIC,
        awaitText: String? = null,
        content: @Composable (AppContainer) -> Unit,
    ): AppContainer {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val container = AppContainer(context)

        container.theme.applyMode(mode)
        container.theme.applyStyle(style)

        runBlocking { seed(container) }

        compose.setContent {
            PaperCoinTheme(container.theme) {
                AppBackdrop(container.theme.style) {
                    content(container)
                }
            }
        }

        if (awaitText != null) {
            compose.waitUntil(timeoutMillis = 10_000) {
                compose.onAllNodesWithText(awaitText, substring = true)
                    .fetchSemanticsNodes()
                    .isNotEmpty()
            }
        }

        compose.waitForIdle()

        return container
    }

    private fun seed(container: AppContainer) = runBlocking {
        val today = LocalDate.now()

        container.repository.save(
            listOf(
                BillEntry(
                    description = "午饭",
                    amount = BigDecimal("-32.50"),
                    category = "餐饮",
                    date = today,
                ),
                BillEntry(
                    description = "地铁",
                    amount = BigDecimal("-6.00"),
                    category = "交通",
                    date = today,
                ),
                BillEntry(
                    description = "咖啡",
                    amount = BigDecimal("-28.00"),
                    category = "餐饮",
                    date = today.minusDays(1),
                ),
                BillEntry(
                    description = "超市",
                    amount = BigDecimal("-128.80"),
                    category = "购物",
                    date = today.minusDays(3),
                ),
                BillEntry(
                    description = "工资",
                    amount = BigDecimal("12000.00"),
                    category = "工资",
                    date = today.minusDays(5),
                ),
                BillEntry(
                    description = "电影",
                    amount = BigDecimal("-45.00"),
                    category = "娱乐",
                    date = today.minusMonths(1).withDayOfMonth(12),
                ),
                BillEntry(
                    description = "话费",
                    amount = BigDecimal("-59.00"),
                    category = "日常",
                    date = today.minusMonths(2).withDayOfMonth(8),
                ),
                BillEntry(
                    description = "打车",
                    amount = BigDecimal("-23.50"),
                    category = "交通",
                    date = today.minusMonths(3).withDayOfMonth(20),
                ),
            ),
        )
    }

    private fun capture(name: String) {
        captureView(name, compose.activity.window.decorView)
    }

    private fun captureView(name: String, view: android.view.View) {
        if (view.width == 0 || view.height == 0) {
            view.measure(
                android.view.View.MeasureSpec.makeMeasureSpec(1128, android.view.View.MeasureSpec.EXACTLY),
                android.view.View.MeasureSpec.makeMeasureSpec(2445, android.view.View.MeasureSpec.EXACTLY),
            )
            view.layout(0, 0, view.measuredWidth, view.measuredHeight)
        }

        val bitmap = Bitmap.createBitmap(view.width, view.height, Bitmap.Config.ARGB_8888)
        view.draw(Canvas(bitmap))

        val dir = File("build/screenshots").apply { mkdirs() }
        FileOutputStream(File(dir, "$name.png")).use { output ->
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, output)
        }
    }

    @Test
    fun todayDark() {
        boot(awaitText = "午饭") { container -> TodayScreen(container) }
        capture("today-dark")
    }

    @Test
    fun appShellDark() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val container = (context as PaperCoinApplication).container

        runBlocking { seed(container) }

        compose.setContent { PaperCoinApp() }
        compose.waitUntil(timeoutMillis = 10_000) {
            compose.onAllNodesWithText("午饭", substring = true).fetchSemanticsNodes().isNotEmpty()
        }
        compose.waitForIdle()

        capture("app-shell-dark")
    }

    @Test
    fun calendarMonthDark() {
        boot(awaitText = "午饭") { container -> CalendarScreen(container) }

        compose.onNodeWithText("月").performClick()
        compose.waitUntil(timeoutMillis = 10_000) {
            compose.onAllNodesWithText("-28", substring = true).fetchSemanticsNodes().isNotEmpty()
        }
        compose.waitForIdle()

        capture("calendar-month-dark")
    }

    @Test
    fun calendarYearDark() {
        boot(awaitText = "午饭") { container -> CalendarScreen(container) }

        compose.onNodeWithText("年").performClick()
        compose.waitUntil(timeoutMillis = 10_000) {
            compose.onAllNodesWithText("12 月").fetchSemanticsNodes().isNotEmpty()
        }
        compose.waitForIdle()

        capture("calendar-year-dark")
    }

    @Test
    fun statisticsDark() {
        boot(awaitText = "餐饮") { container -> StatisticsScreen(container) }
        capture("statistics-dark")
    }

    @Test
    fun statisticsBottomDark() {
        boot(awaitText = "餐饮") { container -> StatisticsScreen(container) }

        compose.onNodeWithTag("statistics-list").performScrollToIndex(7)
        compose.waitForIdle()

        capture("statistics-bottom-dark")
    }

    @Test
    fun editorSheetDark() {
        boot(awaitText = "午饭") { container -> TodayScreen(container) }

        compose.onNodeWithTag("add-bill").performClick()
        compose.waitUntil(timeoutMillis = 10_000) {
            compose.onAllNodesWithText("新增账单").fetchSemanticsNodes().isNotEmpty()
        }
        compose.waitForIdle()

        val dialogView = org.robolectric.shadows.ShadowDialog.getLatestDialog()?.window?.decorView
            ?: compose.activity.window.decorView

        captureView("editor-dark", dialogView)
    }

    @Test
    fun settingsDark() {
        boot { container -> SettingsScreen(container) }
        capture("settings-dark")
    }

    @Test
    fun syncDialogDark() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val container = AppContainer(context)

        container.sync.setCode("ABCD2345")
        container.sync.setDeviceName("测试手机")
        container.sync.setEnabled(true)
        container.syncStore.recordSync(
            deviceId = "peer-1",
            deviceName = "DESKTOP-ABC",
            baseline = mapOf("id" to "hash"),
            atMillis = 1792250400000L,
        )

        compose.setContent {
            PaperCoinTheme(container.theme) {
                AppBackdrop(container.theme.style) {
                    SettingsScreen(container)
                }
            }
        }

        compose.onNodeWithText("管理").performClick()
        compose.waitUntil(timeoutMillis = 10_000) {
            compose.onAllNodesWithText("同步码").fetchSemanticsNodes().isNotEmpty()
        }
        compose.waitForIdle()

        val dialogView = org.robolectric.shadows.ShadowDialog.getLatestDialog()?.window?.decorView
            ?: compose.activity.window.decorView

        captureView("sync-dialog-dark", dialogView)
    }

    @Test
    fun syncConflictDark() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        val container = AppContainer(context)

        val local = BillEntry(
            id = "1",
            description = "午饭",
            amount = BigDecimal("-32.50"),
            category = "餐饮",
            date = LocalDate.now(),
        )
        val remote = local.copy(description = "午饭（对方）", amount = BigDecimal("-45.00"))
        val remoteOnly = BillEntry(
            id = "2",
            description = "地铁",
            amount = BigDecimal("-6.00"),
            category = "交通",
            date = LocalDate.now(),
        )

        val conflicts = listOf(
            SyncConflict("1", local, remote, ConflictKind.BOTH_CHANGED),
            SyncConflict("2", null, remoteOnly, ConflictKind.LOCAL_DELETED_REMOTE_CHANGED),
        )

        compose.setContent {
            PaperCoinTheme(container.theme) {
                SyncConflictDialogContent(
                    conflicts = conflicts,
                    peerName = "DESKTOP-ABC",
                    status = "发现 2 处冲突，请选择保留哪个版本",
                    busy = false,
                    onResolve = {},
                    onCancel = {},
                )
            }
        }
        compose.waitForIdle()

        val dialogView = org.robolectric.shadows.ShadowDialog.getLatestDialog()?.window?.decorView
            ?: compose.activity.window.decorView

        captureView("sync-conflict-dark", dialogView)
    }

    @Test
    fun todayBrightWindows() {
        boot(mode = ThemeState.MODE_BRIGHT, style = ThemeState.STYLE_WINDOWS, awaitText = "午饭") { container ->
            TodayScreen(container)
        }
        capture("today-bright-windows")
    }

    @Test
    fun todayLiquid() {
        boot(mode = ThemeState.MODE_DARK, style = ThemeState.STYLE_LIQUID, awaitText = "午饭") { container ->
            TodayScreen(container)
        }
        capture("today-liquid")
    }

    @Test
    fun todayBrightLiquid() {
        boot(mode = ThemeState.MODE_BRIGHT, style = ThemeState.STYLE_LIQUID, awaitText = "午饭") { container ->
            TodayScreen(container)
        }
        capture("today-bright-liquid")
    }
}
