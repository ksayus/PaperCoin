package com.ksayus.papercoin.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.clickable
import androidx.compose.foundation.border
import androidx.compose.ui.draw.clip
import com.ksayus.papercoin.data.AppContainer
import com.ksayus.papercoin.ui.components.AppCard
import com.ksayus.papercoin.ui.components.AppIcons
import com.ksayus.papercoin.ui.components.IconText
import com.ksayus.papercoin.ui.components.PillButton
import com.ksayus.papercoin.ui.components.SectionTitle
import com.ksayus.papercoin.ui.components.SettingHeader
import com.ksayus.papercoin.ui.components.SettingsSwitch
import com.ksayus.papercoin.ui.theme.Bold
import com.ksayus.papercoin.ui.theme.LocalAppColors
import com.ksayus.papercoin.ui.theme.SemiBold
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/** 局域网同步设置页。手机端用整屏 Dialog，避免再往底部导航里塞第五个 Tab。 */
@Composable
fun SyncDialog(container: AppContainer, onClose: () -> Unit) {
    val sync = container.sync
    val ui by sync.ui.collectAsState()
    val colors = LocalAppColors.current

    var codeDraft by remember(ui.code) { mutableStateOf(ui.code) }
    var nameDraft by remember(ui.deviceName) { mutableStateOf(ui.deviceName) }
    var codeVisible by remember { mutableStateOf(false) }
    var hostDraft by remember { mutableStateOf("") }
    var portDraft by remember { mutableStateOf("47832") }
    var localMessage by remember { mutableStateOf("") }

    val now = System.currentTimeMillis()
    val online = ui.discovered.filter { now - it.lastSeenAt < 15_000 }
    val unknown = online.filter { discovered ->
        ui.peers.none { it.deviceId == discovered.deviceId }
    }

    Dialog(
        onDismissRequest = onClose,
        properties = DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Surface(color = colors.mainContentArea, modifier = Modifier.fillMaxSize()) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .statusBarsPadding()
                    .navigationBarsPadding(),
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = "局域网同步",
                        fontSize = 16.sp,
                        fontWeight = Bold,
                        color = colors.textTitle,
                        modifier = Modifier.weight(1f),
                    )
                    PillButton("关闭", onClick = onClose)
                }

                Column(
                    modifier = Modifier
                        .weight(1f)
                        .verticalScroll(rememberScrollState())
                        .padding(horizontal = 16.dp)
                        .padding(bottom = 24.dp),
                ) {
                    AppCard(modifier = Modifier.fillMaxWidth()) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            SettingHeader(
                                icon = AppIcons.REFRESH,
                                title = "启用同步",
                                subtitle = when {
                                    !ui.enabled -> "打开后才会发现设备"
                                    ui.running -> "正在监听局域网"
                                    else -> "同步已暂停"
                                },
                                modifier = Modifier.weight(1f),
                            )
                            SettingsSwitch(
                                checked = ui.enabled,
                                onCheckedChange = { sync.setEnabled(it) },
                            )
                        }
                    }

                    Spacer(Modifier.height(8.dp))

                    AppCard(modifier = Modifier.fillMaxWidth()) {
                        SettingHeader(
                            icon = AppIcons.SOURCE,
                            title = "同步码",
                            subtitle = "两台设备设置相同的 6-12 位字母或数字；不会明文发送",
                        )
                        Spacer(Modifier.height(8.dp))
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            SyncField(
                                value = codeDraft,
                                onValueChange = {
                                    codeDraft = it.uppercase().take(12)
                                    localMessage = ""
                                },
                                modifier = Modifier.weight(1f),
                                placeholder = "例如 ABCD2345",
                                capitalization = KeyboardCapitalization.Characters,
                                visualTransformation = if (codeVisible) {
                                    VisualTransformation.None
                                } else {
                                    PasswordVisualTransformation()
                                },
                            )
                            Spacer(Modifier.width(6.dp))
                            Text(
                                text = if (codeVisible) "隐藏" else "显示",
                                fontSize = 10.sp,
                                color = colors.textSecondary,
                                modifier = Modifier.clickable { codeVisible = !codeVisible }.padding(6.dp),
                            )
                        }

                        Row(modifier = Modifier.padding(top = 8.dp)) {
                            PillButton("保存", onClick = {
                                localMessage = if (sync.setCode(codeDraft)) "同步码已保存" else "格式不正确"
                            })
                            Spacer(Modifier.width(6.dp))
                            PillButton("生成", onClick = {
                                codeDraft = sync.generateCode()
                                localMessage = "已生成，请同步到另一台设备"
                            })
                        }

                        if (localMessage.isNotEmpty()) {
                            Text(
                                text = localMessage,
                                fontSize = 10.sp,
                                color = colors.textSecondary,
                                modifier = Modifier.padding(top = 6.dp),
                            )
                        }
                    }

                    Spacer(Modifier.height(8.dp))

                    AppCard(modifier = Modifier.fillMaxWidth()) {
                        SettingHeader(
                            icon = AppIcons.INFO,
                            title = "本机名称",
                            subtitle = "对方在设备列表里看到的名字",
                        )
                        Spacer(Modifier.height(8.dp))
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            SyncField(
                                value = nameDraft,
                                onValueChange = { nameDraft = it.take(48) },
                                modifier = Modifier.weight(1f),
                                placeholder = "设备名称",
                            )
                            Spacer(Modifier.width(6.dp))
                            PillButton("保存", onClick = { sync.setDeviceName(nameDraft) })
                        }
                    }

                    Spacer(Modifier.height(8.dp))

                    AppCard(modifier = Modifier.fillMaxWidth()) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            SettingHeader(
                                icon = AppIcons.POWER,
                                title = "自动同步",
                                subtitle = "应用在前台时自动同步已配对设备",
                                modifier = Modifier.weight(1f),
                            )
                            SettingsSwitch(
                                checked = ui.autoSync,
                                onCheckedChange = { sync.setAutoSync(it) },
                            )
                        }
                    }

                    Spacer(Modifier.height(8.dp))

                    AppCard(modifier = Modifier.fillMaxWidth()) {
                        Text(text = "状态", fontSize = 11.sp, color = colors.textSecondary)
                        Text(
                            text = ui.status,
                            fontSize = 12.sp,
                            color = colors.textTitle,
                            modifier = Modifier.padding(top = 3.dp),
                        )
                        Text(
                            text = "上次同步：${formatSyncTime(ui.lastSyncAt)}",
                            fontSize = 10.sp,
                            color = colors.textSecondary,
                            modifier = Modifier.padding(top = 6.dp),
                        )
                    }

                    if (unknown.isNotEmpty()) {
                        Spacer(Modifier.height(14.dp))
                        SectionTitle("发现的新设备")
                        AppCard(modifier = Modifier.fillMaxWidth()) {
                            unknown.forEachIndexed { index, peer ->
                                if (index > 0) SyncDivider()
                                Row(
                                    modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Column(modifier = Modifier.weight(1f)) {
                                        Text(peer.deviceName, fontSize = 13.sp, color = colors.textTitle)
                                        Text(
                                            "${peer.host}:${peer.tcpPort}",
                                            fontSize = 10.sp,
                                            color = colors.textSecondary,
                                        )
                                    }
                                    PillButton("同步", onClick = { sync.syncPeer(peer.deviceId) })
                                }
                            }
                        }
                    }

                    Spacer(Modifier.height(14.dp))
                    SectionTitle("已配对设备")

                    AppCard(modifier = Modifier.fillMaxWidth()) {
                        if (ui.peers.isEmpty()) {
                            Text(
                                text = "还没有已配对设备。第一次手动同步成功后会自动记住对方。",
                                fontSize = 11.sp,
                                color = colors.textSecondary,
                            )
                        } else {
                            ui.peers.forEachIndexed { index, peer ->
                                if (index > 0) SyncDivider()

                                val discovered = online.firstOrNull { it.deviceId == peer.deviceId }

                                Row(
                                    modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Column(modifier = Modifier.weight(1f)) {
                                        Text(
                                            text = peer.deviceName.ifBlank { "未命名设备" },
                                            fontSize = 13.sp,
                                            color = colors.textTitle,
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis,
                                        )
                                        Text(
                                            text = "上次 ${formatSyncTime(peer.lastSyncAt)}" +
                                                if (discovered != null) " · 在线" else " · 未发现",
                                            fontSize = 10.sp,
                                            color = colors.textSecondary,
                                        )
                                    }

                                    if (discovered != null) {
                                        PillButton("同步", onClick = { sync.syncPeer(peer.deviceId) })
                                        Spacer(Modifier.width(4.dp))
                                    }

                                    PillButton("解除", onClick = { sync.forgetPeer(peer.deviceId) })
                                }
                            }
                        }
                    }

                    Spacer(Modifier.height(14.dp))
                    SectionTitle("手动连接")
                    AppCard(modifier = Modifier.fillMaxWidth()) {
                        SettingHeader(
                            icon = AppIcons.SOURCE,
                            title = "主机与端口",
                            subtitle = "广播被防火墙挡住时，直接填对方地址",
                        )
                        Spacer(Modifier.height(8.dp))
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            SyncField(
                                value = hostDraft,
                                onValueChange = { hostDraft = it },
                                modifier = Modifier.weight(1f),
                                placeholder = "192.168.1.23",
                                keyboardType = KeyboardType.Uri,
                            )
                            Spacer(Modifier.width(6.dp))
                            SyncField(
                                value = portDraft,
                                onValueChange = { portDraft = it.filter { ch -> ch.isDigit() }.take(5) },
                                modifier = Modifier.width(82.dp),
                                placeholder = "47832",
                                keyboardType = KeyboardType.Number,
                            )
                            Spacer(Modifier.width(6.dp))
                            PillButton("连接", onClick = {
                                val port = portDraft.toIntOrNull() ?: 0
                                sync.syncHost(hostDraft, port)
                            })
                        }
                    }

                    Spacer(Modifier.height(14.dp))
                    Text(
                        text = "只在可信局域网使用；同步流量未加密。Android 退到后台会暂停同步，回到前台自动恢复。",
                        fontSize = 10.sp,
                        color = colors.textSecondary,
                    )
                }
            }
        }
    }
}

@Composable
private fun SyncField(
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    placeholder: String = "",
    keyboardType: KeyboardType = KeyboardType.Text,
    capitalization: KeyboardCapitalization = KeyboardCapitalization.None,
    visualTransformation: VisualTransformation = VisualTransformation.None,
) {
    val colors = LocalAppColors.current

    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        modifier = modifier,
        singleLine = true,
        shape = RoundedCornerShape(6.dp),
        textStyle = TextStyle(fontSize = 13.sp, color = colors.textTitle),
        placeholder = {
            if (placeholder.isNotEmpty()) {
                Text(placeholder, fontSize = 12.sp, color = colors.textSecondary)
            }
        },
        visualTransformation = visualTransformation,
        keyboardOptions = KeyboardOptions(
            keyboardType = keyboardType,
            capitalization = capitalization,
        ),
        colors = OutlinedTextFieldDefaults.colors(
            focusedTextColor = colors.textTitle,
            unfocusedTextColor = colors.textTitle,
            focusedContainerColor = colors.cardBackground,
            unfocusedContainerColor = colors.cardBackground,
            focusedBorderColor = colors.addNewBillBtn,
            unfocusedBorderColor = colors.cardBorder,
            cursorColor = colors.textTitle,
        ),
    )
}

@Composable
private fun SyncDivider() {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(1.dp)
            .background(LocalAppColors.current.cardBorder),
    )
}

private fun formatSyncTime(millis: Long): String {
    if (millis <= 0L) return "从未同步"

    return Instant.ofEpochMilli(millis)
        .atZone(ZoneId.systemDefault())
        .format(DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm"))
}
