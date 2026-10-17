package com.ksayus.papercoin.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.runtime.setValue
import androidx.activity.compose.BackHandler
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.ksayus.papercoin.PaperCoinApplication
import com.ksayus.papercoin.data.AppContainer
import com.ksayus.papercoin.ui.components.AppBackdrop
import com.ksayus.papercoin.ui.components.AppIcons
import com.ksayus.papercoin.ui.components.IconText
import com.ksayus.papercoin.ui.screens.CalendarScreen
import com.ksayus.papercoin.ui.screens.SettingsScreen
import com.ksayus.papercoin.ui.screens.StatisticsScreen
import com.ksayus.papercoin.ui.screens.SyncConflictDialog
import com.ksayus.papercoin.ui.screens.TodayScreen
import com.ksayus.papercoin.ui.theme.LocalAppColors
import com.ksayus.papercoin.ui.theme.PaperCoinTheme

private data class TabItem(val icon: String, val label: String)

private val tabs = listOf(
    TabItem(AppIcons.TODAY_BILL, "今日"),
    TabItem(AppIcons.CALENDAR, "日历"),
    TabItem(AppIcons.STATISTICS, "统计"),
    TabItem(AppIcons.SETTINGS, "设置"),
)

@Composable
fun PaperCoinApp() {
    val context = LocalContext.current
    val container = remember(context) {
        (context.applicationContext as PaperCoinApplication).container
    }

    PaperCoinTheme(container.theme) {
        val lifecycleOwner = LocalLifecycleOwner.current

        // LifecycleEventEffect 不会重放已经发生的 ON_START；首帧在 onStart 之后组合时就永远不会启动。
        // 观察者 + 当前状态补一次，才能保证冷启动和从后台回来都能启动监听。
        DisposableEffect(lifecycleOwner) {
            val observer = LifecycleEventObserver { _, event ->
                when (event) {
                    Lifecycle.Event.ON_START -> container.sync.start()
                    Lifecycle.Event.ON_STOP -> container.sync.stop()
                    else -> Unit
                }
            }

            lifecycleOwner.lifecycle.addObserver(observer)

            if (lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)) {
                container.sync.start()
            }

            onDispose {
                lifecycleOwner.lifecycle.removeObserver(observer)
                container.sync.stop()
            }
        }

        AppBackdrop(container.theme.style) {
            BoxWithConstraints(modifier = Modifier.fillMaxSize()) {
                val wide = maxWidth >= 600.dp
                var selected by rememberSaveable { mutableIntStateOf(0) }
                val stateHolder = rememberSaveableStateHolder()
                val scope = rememberCoroutineScope()

                BackHandler(enabled = selected != 0) {
                    selected = 0
                }

                LaunchedEffect(Unit) {
                    container.updates.autoCheckOnce(scope)
                }

                if (wide) {
                    Row(modifier = Modifier.fillMaxSize()) {
                        PaperNavRail(selected) { selected = it }

                        Scaffold(
                            modifier = Modifier.weight(1f),
                            containerColor = Color.Transparent,
                            contentWindowInsets = WindowInsets.safeDrawing,
                        ) { padding ->
                            Box(modifier = Modifier.fillMaxSize().padding(padding)) {
                                ScreenHost(selected, container, stateHolder)
                            }
                        }
                    }
                } else {
                    Scaffold(
                        modifier = Modifier.fillMaxSize(),
                        containerColor = Color.Transparent,
                        contentWindowInsets = WindowInsets.safeDrawing,
                        bottomBar = { PaperBottomBar(selected) { selected = it } },
                    ) { padding ->
                        Box(modifier = Modifier.fillMaxSize().padding(padding)) {
                            ScreenHost(selected, container, stateHolder)
                        }
                    }
                }
            }
        }

        // 自动同步可能在任意页面发现冲突；这个对话框挂在根节点，保证一定能被用户处理。
        SyncConflictDialog(container)
    }
}

@Composable
private fun ScreenHost(
    selected: Int,
    container: AppContainer,
    stateHolder: androidx.compose.runtime.saveable.SaveableStateHolder,
) {
    stateHolder.SaveableStateProvider(selected) {
        when (selected) {
            0 -> TodayScreen(container)
            1 -> CalendarScreen(container)
            2 -> StatisticsScreen(container)
            else -> SettingsScreen(container)
        }
    }
}

@Composable
private fun PaperBottomBar(selected: Int, onSelect: (Int) -> Unit) {
    val colors = LocalAppColors.current

    Column(modifier = Modifier.fillMaxWidth().background(colors.sideBar)) {
        Box(modifier = Modifier.fillMaxWidth().height(1.dp).background(colors.cardBorder))

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .navigationBarsPadding()
                .height(62.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            tabs.forEachIndexed { index, tab ->
                val active = index == selected
                val tint by animateColorAsState(
                    targetValue = if (active) colors.textTitle else colors.textSecondary,
                    animationSpec = tween(durationMillis = 180),
                    label = "tabTint",
                )

                Column(
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxHeight()
                        .clickable { onSelect(index) },
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    Box(
                        modifier = Modifier
                            .width(46.dp)
                            .height(26.dp)
                            .clip(RoundedCornerShape(13.dp))
                            .background(if (active) colors.sideBarBtnActive else Color.Transparent),
                        contentAlignment = Alignment.Center,
                    ) {
                        IconText(tab.icon, 18.sp, tint)
                    }

                    Text(text = tab.label, fontSize = 10.sp, color = tint)
                }
            }
        }
    }
}

/** 宽屏（平板 / 横屏）改用左侧导航栏，与桌面版的空间利用方式一致。 */
@Composable
private fun PaperNavRail(selected: Int, onSelect: (Int) -> Unit) {
    val colors = LocalAppColors.current

    Column(
        modifier = Modifier
            .width(88.dp)
            .fillMaxHeight()
            .background(colors.sideBar)
            .statusBarsPadding()
            .navigationBarsPadding()
            .padding(vertical = 16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        tabs.forEachIndexed { index, tab ->
            val active = index == selected
            val tint by animateColorAsState(
                targetValue = if (active) colors.textTitle else colors.textSecondary,
                animationSpec = tween(durationMillis = 180),
                label = "railTint",
            )

            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 8.dp)
                    .clip(RoundedCornerShape(10.dp))
                    .background(if (active) colors.sideBarBtnActive else Color.Transparent)
                    .clickable { onSelect(index) }
                    .padding(vertical = 10.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                IconText(tab.icon, 20.sp, tint)
                Text(text = tab.label, fontSize = 10.sp, color = tint)
            }
        }
    }
}
