package com.ksayus.papercoin.ui.screens

import android.content.Intent
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.ksayus.papercoin.data.AppContainer
import com.ksayus.papercoin.ui.components.AppCard
import com.ksayus.papercoin.ui.components.AppIcons
import com.ksayus.papercoin.ui.components.NoticeText
import com.ksayus.papercoin.ui.components.PillButton
import com.ksayus.papercoin.ui.components.SectionTitle
import com.ksayus.papercoin.ui.components.SegmentedControl
import com.ksayus.papercoin.ui.components.SettingHeader
import com.ksayus.papercoin.ui.components.SettingsSwitch
import com.ksayus.papercoin.ui.theme.LocalAppColors
import com.ksayus.papercoin.ui.theme.ThemeState
import com.ksayus.papercoin.vm.SettingsViewModel
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

private const val RELEASES_URL = "https://github.com/ksayus/PaperCoin/releases"

@Composable
fun SettingsScreen(container: AppContainer) {
    val vm: SettingsViewModel = viewModel(factory = settingsFactory(container))
    val colors = LocalAppColors.current
    val theme = container.theme
    val context = LocalContext.current

    var confirmReset by remember { mutableStateOf(false) }
    var sourceMenuOpen by remember { mutableStateOf(false) }
    var showSync by remember { mutableStateOf(false) }

    val syncUi by container.sync.ui.collectAsState()
    val syncSummary = when {
        !syncUi.enabled -> "已关闭"
        syncUi.code.isBlank() -> "请设置同步码"
        else -> "已开启 · ${syncUi.peers.size} 台已配对"
    }

    val systemDark = isSystemInDarkTheme()
    val themeText = when (theme.mode) {
        ThemeState.MODE_SYSTEM ->
            "跟随系统（当前：${if (systemDark) "暗色" else "亮色"}）"
        ThemeState.MODE_BRIGHT -> "亮色"
        else -> "暗色"
    }

    val exportZip = rememberLauncherForActivityResult(
        ActivityResultContracts.CreateDocument("application/zip"),
    ) { uri -> if (uri != null) vm.exportBackup(uri) }

    val importZip = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocument(),
    ) { uri -> if (uri != null) vm.importZip(uri) }

    val importFolder = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenDocumentTree(),
    ) { uri -> if (uri != null) vm.importFolder(uri) }

    fun openUrl(url: String) {
        try {
            context.startActivity(
                Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            )
        } catch (ex: Exception) {
            vm.notice = "无法打开浏览器"
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
    ) {
        SectionTitle("外观")

        AppCard(modifier = Modifier.fillMaxWidth()) {
            SettingHeader(AppIcons.THEME, "主题", themeText)
            Spacer(Modifier.height(10.dp))
            SegmentedControl(
                options = listOf(
                    ThemeState.MODE_SYSTEM to "跟随系统",
                    ThemeState.MODE_DARK to "暗色",
                    ThemeState.MODE_BRIGHT to "亮色",
                ),
                selected = theme.mode,
                onSelect = vm::setMode,
                modifier = Modifier.padding(start = 28.dp),
            )
        }

        Spacer(Modifier.height(8.dp))

        AppCard(modifier = Modifier.fillMaxWidth()) {
            SettingHeader(AppIcons.THEME, "风格", theme.styleText())
            Spacer(Modifier.height(10.dp))
            SegmentedControl(
                options = listOf(
                    ThemeState.STYLE_CLASSIC to "经典",
                    ThemeState.STYLE_WINDOWS to "Windows",
                    ThemeState.STYLE_LIQUID to "液态玻璃",
                ),
                selected = theme.style,
                onSelect = vm::setStyle,
                modifier = Modifier.padding(start = 28.dp),
            )
        }

        Spacer(Modifier.height(14.dp))
        SectionTitle("启动与更新")

        AppCard(modifier = Modifier.fillMaxWidth()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SettingHeader(
                    icon = AppIcons.POWER,
                    title = "自动检查更新",
                    subtitle = "启动时在后台查一次新版本；查到后只在设置页显示，不打断使用",
                    modifier = Modifier.weight(1f),
                )
                Spacer(Modifier.width(12.dp))
                SettingsSwitch(
                    checked = vm.autoUpdate,
                    onCheckedChange = { vm.toggleAutoUpdate() },
                )
            }
        }

        Spacer(Modifier.height(8.dp))

        AppCard(modifier = Modifier.fillMaxWidth()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SettingHeader(
                    icon = AppIcons.REFRESH,
                    title = "检查更新",
                    subtitle = vm.updateStatusText,
                    modifier = Modifier.weight(1f),
                )
                if (vm.isIdle) {
                    Spacer(Modifier.width(8.dp))
                    PillButton("检查", onClick = { vm.check(true) })
                }
            }

            if (vm.hasUpdate) {
                Spacer(Modifier.height(8.dp))
                Row(
                    modifier = Modifier.padding(start = 28.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = vm.updateHeadline,
                        fontSize = 10.sp,
                        color = colors.textSecondary,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f),
                    )
                    if (vm.canInstall) {
                        PillButton("下载并安装", onClick = { vm.downloadAndInstall() })
                    }
                    if (vm.mustDownloadManually) {
                        PillButton("去发布页", onClick = { openUrl(RELEASES_URL) })
                    }
                }
            }

            if (vm.isDownloading) {
                Spacer(Modifier.height(8.dp))
                Column(modifier = Modifier.padding(start = 28.dp)) {
                    Text(
                        text = vm.downloadText,
                        fontSize = 10.sp,
                        color = colors.textSecondary,
                    )
                    LinearProgressIndicator(
                        progress = { vm.downloadPercent },
                        modifier = Modifier.fillMaxWidth().height(3.dp).padding(top = 4.dp),
                        color = colors.sideBarBtnActive,
                        trackColor = colors.cardActionBackground,
                    )
                }
            }

            if (vm.hasReleaseNotes) {
                Spacer(Modifier.height(8.dp))
                Text(
                    text = vm.releaseNotes,
                    fontSize = 10.sp,
                    color = colors.textSecondary,
                    modifier = Modifier.padding(start = 28.dp),
                )
            }
        }

        Spacer(Modifier.height(8.dp))

        AppCard(modifier = Modifier.fillMaxWidth()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SettingHeader(
                    icon = AppIcons.SOURCE,
                    title = "更新源",
                    subtitle = "决定从哪里查询新版本",
                    modifier = Modifier.weight(1f),
                )
                Box {
                    PillButton(vm.updateSource, onClick = { sourceMenuOpen = true })
                    DropdownMenu(
                        expanded = sourceMenuOpen,
                        onDismissRequest = { sourceMenuOpen = false },
                    ) {
                        vm.sourceOptions.forEach { source ->
                            DropdownMenuItem(
                                text = { Text(source, fontSize = 12.sp, color = colors.textTitle) },
                                onClick = {
                                    vm.applyUpdateSource(source)
                                    sourceMenuOpen = false
                                },
                            )
                        }
                    }
                }
            }
        }

        Spacer(Modifier.height(14.dp))
        SectionTitle("局域网同步")

        AppCard(modifier = Modifier.fillMaxWidth()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SettingHeader(
                    icon = AppIcons.REFRESH,
                    title = "局域网同步",
                    subtitle = syncSummary,
                    modifier = Modifier.weight(1f),
                )
                PillButton("管理", onClick = { showSync = true })
            }
        }

        Spacer(Modifier.height(14.dp))
        SectionTitle("数据与关于")

        AppCard(modifier = Modifier.fillMaxWidth()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SettingHeader(
                    icon = AppIcons.FOLDER,
                    title = "导出备份",
                    subtitle = "把 Bills 与配置打成 ZIP，存到你选择的位置",
                    modifier = Modifier.weight(1f),
                )
                PillButton("导出", onClick = {
                    val name = "PaperCoin-backup-" +
                        LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss")) +
                        ".zip"
                    exportZip.launch(name)
                })
            }

            Spacer(Modifier.height(10.dp))
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(1.dp)
                    .background(colors.cardBorder),
            )
            Spacer(Modifier.height(10.dp))

            SettingHeader(
                icon = AppIcons.FOLDER,
                title = "导入备份",
                subtitle = "支持 ZIP 与桌面版 Bills 文件夹；按 Id 合并，不覆盖未选中的日期",
            )
            Row(modifier = Modifier.padding(start = 28.dp, top = 8.dp)) {
                PillButton(
                    "导入 ZIP",
                    onClick = {
                        importZip.launch(
                            arrayOf("application/zip", "application/octet-stream", "*/*"),
                        )
                    },
                )
                Spacer(Modifier.width(6.dp))
                PillButton("导入文件夹", onClick = { importFolder.launch(null) })
            }
        }

        Spacer(Modifier.height(8.dp))

        AppCard(modifier = Modifier.fillMaxWidth()) {
            SettingHeader(
                icon = AppIcons.FOLDER,
                title = "数据目录",
                subtitle = vm.dataFolderText,
            )
        }

        Spacer(Modifier.height(8.dp))

        AppCard(modifier = Modifier.fillMaxWidth()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                SettingHeader(
                    icon = AppIcons.INFO,
                    title = "版本",
                    subtitle = vm.versionText,
                    modifier = Modifier.weight(1f),
                )
                PillButton("发布页", onClick = { openUrl(RELEASES_URL) })
                Spacer(Modifier.width(6.dp))
                PillButton("恢复默认", onClick = { confirmReset = true })
            }
        }

        NoticeText(vm.notice, Modifier.padding(top = 8.dp))
    }

    if (showSync) {
        SyncDialog(container, onClose = { showSync = false })
    }

    if (confirmReset) {
        AlertDialog(
            onDismissRequest = { confirmReset = false },
            containerColor = colors.mainContentArea,
            titleContentColor = colors.textTitle,
            textContentColor = colors.textSecondary,
            title = { Text("恢复默认设置") },
            text = { Text("主题、更新开关与更新源会恢复默认；账单数据不会被删除。") },
            confirmButton = {
                TextButton(
                    onClick = {
                        vm.resetToDefaults()
                        confirmReset = false
                    },
                ) {
                    Text("恢复", color = colors.negative)
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmReset = false }) {
                    Text("取消", color = colors.textSecondary)
                }
            },
        )
    }
}

private fun settingsFactory(container: AppContainer) = viewModelFactory {
    initializer {
        SettingsViewModel(
            config = container.config,
            theme = container.theme,
            updates = container.updates,
            backup = container.backup,
            dataRoot = container.dataRoot,
        )
    }
}
