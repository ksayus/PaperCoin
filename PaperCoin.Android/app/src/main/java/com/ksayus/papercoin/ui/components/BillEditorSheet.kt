package com.ksayus.papercoin.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.BottomSheetDefaults
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.IconButton
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.ksayus.papercoin.core.Formats
import com.ksayus.papercoin.ui.theme.Bold
import com.ksayus.papercoin.ui.theme.LocalAppColors
import com.ksayus.papercoin.vm.BillEditorState
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset

/**
 * 新增 / 编辑面板。桌面版是一个居中浮层；手机上是底部面板，
 * 字段顺序、校验文案与"取消 / 保存"语义保持一致。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun BillEditorSheet(editor: BillEditorState) {
    val colors = LocalAppColors.current
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)

    ModalBottomSheet(
        onDismissRequest = { editor.close() },
        sheetState = sheetState,
        containerColor = colors.mainContentArea,
        contentColor = colors.textTitle,
        scrimColor = Color.Black.copy(alpha = 0.45f),
        dragHandle = { BottomSheetDefaults.DragHandle(color = colors.cardBorder) },
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .imePadding()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 20.dp)
                .padding(bottom = 28.dp),
        ) {
            Text(text = editor.title, fontSize = 15.sp, fontWeight = Bold, color = colors.textTitle)

            Spacer(Modifier.height(14.dp))

            FieldLabel("描述")
            EditorTextField(
                value = editor.description,
                onValueChange = { editor.description = it },
            )

            FieldLabel("金额（支出填负数）")
            EditorTextField(
                value = editor.amountText,
                onValueChange = { editor.amountText = it },
                keyboardType = KeyboardType.Decimal,
            )

            FieldLabel("分类（可选已有的，也可直接输入新的）")
            CategoryField(editor)

            FieldLabel("日期（可改成以前的任意一天）")
            DateField(editor)

            if (editor.errorMessage.isNotEmpty()) {
                Text(
                    text = editor.errorMessage,
                    fontSize = 11.sp,
                    color = colors.negative,
                    modifier = Modifier.padding(top = 10.dp),
                )
            }

            Row(
                modifier = Modifier.fillMaxWidth().padding(top = 16.dp),
                horizontalArrangement = Arrangement.End,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                TextButton(onClick = { editor.close() }) {
                    Text("取消", fontSize = 12.sp, color = colors.textSecondary)
                }
                Spacer(Modifier.width(8.dp))
                Button(
                    onClick = { editor.save() },
                    enabled = !editor.hasError && !editor.isSaving,
                    shape = RoundedCornerShape(6.dp),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = colors.addNewBillBtn,
                        contentColor = Color.White,
                        disabledContainerColor = colors.cardActionBackground,
                        disabledContentColor = colors.textSecondary,
                    ),
                ) {
                    Text(
                        text = if (editor.isSaving) "保存中…" else "保存",
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold,
                    )
                }
            }
        }
    }
}

@Composable
private fun FieldLabel(text: String) {
    Text(
        text = text,
        fontSize = 11.sp,
        color = LocalAppColors.current.textSecondary,
        modifier = Modifier.padding(top = 12.dp, bottom = 4.dp),
    )
}

@Composable
private fun EditorTextField(
    value: String,
    onValueChange: (String) -> Unit,
    keyboardType: KeyboardType = KeyboardType.Text,
) {
    val colors = LocalAppColors.current

    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        modifier = Modifier.fillMaxWidth(),
        singleLine = true,
        shape = RoundedCornerShape(6.dp),
        textStyle = TextStyle(fontSize = 13.sp, color = colors.textTitle),
        keyboardOptions = KeyboardOptions(keyboardType = keyboardType),
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
private fun CategoryField(editor: BillEditorState) {
    val colors = LocalAppColors.current
    var expanded by remember { mutableStateOf(false) }

    Box {
        OutlinedTextField(
            value = editor.category,
            onValueChange = { editor.category = it },
            modifier = Modifier.fillMaxWidth(),
            singleLine = true,
            shape = RoundedCornerShape(6.dp),
            textStyle = TextStyle(fontSize = 13.sp, color = colors.textTitle),
            trailingIcon = {
                IconButton(onClick = { expanded = true }) {
                    Text("⌄", fontSize = 13.sp, color = colors.textSecondary)
                }
            },
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

        DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
            if (editor.categories.isEmpty()) {
                DropdownMenuItem(
                    text = { Text("还没有历史分类", fontSize = 11.sp, color = colors.textSecondary) },
                    onClick = { expanded = false },
                )
            } else {
                editor.categories.forEach { category ->
                    DropdownMenuItem(
                        text = { Text(category, fontSize = 12.sp, color = colors.textTitle) },
                        onClick = {
                            editor.category = category
                            expanded = false
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun DateField(editor: BillEditorState) {
    val colors = LocalAppColors.current
    var pickerOpen by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(6.dp)

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(colors.cardBackground)
            .border(1.dp, colors.cardBorder, shape)
            .clickable { pickerOpen = true }
            .padding(horizontal = 10.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = Formats.dateLong(editor.date),
            fontSize = 13.sp,
            color = colors.textTitle,
            modifier = Modifier.weight(1f),
        )
        IconText(AppIcons.CALENDAR, 16.sp, colors.textSecondary)
    }

    if (pickerOpen) {
        DatePickerSheet(
            initial = editor.date,
            onConfirm = { picked ->
                editor.date = picked
                pickerOpen = false
            },
            onDismiss = { pickerOpen = false },
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun DatePickerSheet(
    initial: LocalDate,
    onConfirm: (LocalDate) -> Unit,
    onDismiss: () -> Unit,
) {
    val state = rememberDatePickerState(
        initialSelectedDateMillis = initial
            .atStartOfDay(ZoneOffset.UTC)
            .toInstant()
            .toEpochMilli(),
    )

    DatePickerDialog(
        onDismissRequest = onDismiss,
        confirmButton = {
            TextButton(
                onClick = {
                    state.selectedDateMillis?.let { millis ->
                        onConfirm(Instant.ofEpochMilli(millis).atZone(ZoneOffset.UTC).toLocalDate())
                    }
                    onDismiss()
                },
            ) {
                Text("确定")
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("取消")
            }
        },
    ) {
        DatePicker(state = state)
    }
}
