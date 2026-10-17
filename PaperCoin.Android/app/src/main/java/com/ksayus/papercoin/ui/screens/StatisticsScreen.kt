package com.ksayus.papercoin.ui.screens

import android.content.Context
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.ksayus.papercoin.data.AppContainer
import com.ksayus.papercoin.data.CsvExporter
import com.ksayus.papercoin.data.DonutSegment
import com.ksayus.papercoin.data.StatisticsRange
import com.ksayus.papercoin.ui.components.AppCard
import com.ksayus.papercoin.ui.components.AppIcons
import com.ksayus.papercoin.ui.components.DonutChart
import com.ksayus.papercoin.ui.components.IconText
import com.ksayus.papercoin.ui.components.NoticeText
import com.ksayus.papercoin.ui.components.PillButton
import com.ksayus.papercoin.ui.components.RankRow
import com.ksayus.papercoin.ui.components.SectionTitle
import com.ksayus.papercoin.ui.components.SegmentedControl
import com.ksayus.papercoin.ui.components.TrendBars
import com.ksayus.papercoin.ui.theme.Bold
import com.ksayus.papercoin.ui.theme.LocalAppColors
import com.ksayus.papercoin.ui.theme.SemiBold
import com.ksayus.papercoin.vm.CsvExport
import com.ksayus.papercoin.vm.StatisticsViewModel

@Composable
fun StatisticsScreen(container: AppContainer) {
    val vm: StatisticsViewModel = viewModel(factory = statisticsFactory(container))
    val colors = LocalAppColors.current
    val context = LocalContext.current

    var pendingCsv by remember { mutableStateOf<CsvExport?>(null) }

    val exportLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.CreateDocument("text/csv"),
    ) { uri ->
        val csv = pendingCsv
        pendingCsv = null

        if (uri != null && csv != null) {
            try {
                val stream = context.contentResolver.openOutputStream(uri)
                    ?: throw java.io.IOException("无法写入所选位置")

                stream.use { output ->
                    // UTF-8 BOM：没有它 Excel 会按本地代码页解释，中文全是乱码
                    output.write(byteArrayOf(0xEF.toByte(), 0xBB.toByte(), 0xBF.toByte()))
                    output.write(csv.content.toByteArray(Charsets.UTF_8))
                }
                vm.notice = "已导出 ${csv.fileName}"
            } catch (ex: Exception) {
                vm.notice = "导出失败，请检查磁盘权限或空间"
            }
        }
    }

    LaunchedEffect(Unit) {
        vm.ensureFresh()
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(horizontal = 16.dp, vertical = 12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            SegmentedControl(
                options = listOf(
                    StatisticsRange.MONTH.name to "本月",
                    StatisticsRange.YEAR.name to "本年",
                    StatisticsRange.ALL.name to "全部",
                ),
                selected = vm.range.name,
                onSelect = { vm.showRange(StatisticsRange.valueOf(it)) },
            )

            Spacer(Modifier.weight(1f))

            Box(
                modifier = Modifier
                    .size(34.dp)
                    .clip(RoundedCornerShape(17.dp))
                    .background(colors.cardActionBackground)
                    .clickable { vm.refresh() },
                contentAlignment = Alignment.Center,
            ) {
                IconText(AppIcons.REFRESH, 15.sp, colors.textTitle)
            }
        }

        Row(
            modifier = Modifier.padding(top = 8.dp, bottom = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = vm.snapshot.rangeText,
                fontSize = 11.sp,
                color = colors.textSecondary,
                modifier = Modifier.weight(1f),
            )
            PillButton("导出", onClick = {
                val csv = vm.exportCsv()
                pendingCsv = csv
                exportLauncher.launch(csv.fileName)
            })
        }

        NoticeText(vm.notice, Modifier.padding(bottom = 6.dp))

        LazyColumn(
            modifier = Modifier.weight(1f).testTag("statistics-list"),
            contentPadding = PaddingValues(bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            item {
                SectionTitle("分类占比")
                AppCard(modifier = Modifier.fillMaxWidth()) {
                    if (vm.snapshot.hasExpense) {
                        Box(
                            modifier = Modifier.fillMaxWidth().height(150.dp),
                            contentAlignment = Alignment.Center,
                        ) {
                            DonutChart(
                                segments = vm.snapshot.segments,
                                modifier = Modifier.fillMaxSize(),
                                onSegmentClick = { segment ->
                                    // 合并段不是真实分类，点它只会得到一份空明细
                                    if (!segment.label.startsWith("其他 ")) {
                                        vm.selectCategory(segment.label)
                                    }
                                },
                            )
                            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                Text("总支出", fontSize = 10.sp, color = colors.textSecondary)
                                Text(
                                    text = vm.snapshot.totalExpenseText,
                                    fontSize = 19.sp,
                                    fontWeight = Bold,
                                    color = colors.negative,
                                )
                                Text(
                                    text = vm.snapshot.entryCountText,
                                    fontSize = 10.sp,
                                    color = colors.textSecondary,
                                )
                            }
                        }

                        Column(modifier = Modifier.padding(top = 10.dp)) {
                            vm.snapshot.legendRows.forEach { segment ->
                                LegendRow(segment) { vm.selectCategory(segment.label) }
                            }
                        }
                    } else {
                        Box(
                            modifier = Modifier.fillMaxWidth().height(150.dp),
                            contentAlignment = Alignment.Center,
                        ) {
                            Text(
                                text = "这个范围内没有支出",
                                fontSize = 11.sp,
                                color = colors.textSecondary,
                            )
                        }
                    }
                }
            }

            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        text = "分类明细",
                        fontSize = 11.sp,
                        color = colors.textSecondary,
                        modifier = Modifier.weight(1f),
                    )
                    CategoryPicker(vm)
                    if (vm.hasCategorySelection) {
                        Spacer(Modifier.width(6.dp))
                        PillButton(
                            text = "清除",
                            onClick = vm::clearCategory,
                            fontSize = 10.sp,
                            padding = PaddingValues(horizontal = 8.dp, vertical = 2.dp),
                        )
                    }
                }
            }

            item {
                AppCard(modifier = Modifier.fillMaxWidth()) {
                    if (!vm.hasCategorySelection) {
                        Text(
                            text = "点上面的排行行或图例，或从右侧下拉里选一个分类，查看它的全部流水",
                            fontSize = 11.sp,
                            color = colors.textSecondary,
                        )
                    } else {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(
                                text = vm.detailTitle,
                                fontSize = 13.sp,
                                fontWeight = SemiBold,
                                color = colors.textTitle,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f),
                            )
                            Text("全部时间", fontSize = 10.sp, color = colors.textSecondary)
                        }

                        Text(
                            text = vm.detailSummary,
                            fontSize = 10.sp,
                            color = colors.textSecondary,
                            modifier = Modifier.padding(top = 2.dp, bottom = 8.dp),
                        )

                        Column(
                            modifier = Modifier
                                .heightIn(max = 220.dp)
                                .verticalScroll(rememberScrollState()),
                        ) {
                            vm.detailEntries.forEach { entry ->
                                Row(
                                    modifier = Modifier.fillMaxWidth().padding(bottom = 7.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Text(
                                        text = entry.dateText,
                                        fontSize = 10.sp,
                                        color = colors.textSecondary,
                                        modifier = Modifier.width(40.dp),
                                    )
                                    Text(
                                        text = entry.description,
                                        fontSize = 12.sp,
                                        color = colors.textTitle,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                        modifier = Modifier.weight(1f).padding(start = 6.dp),
                                    )
                                    Text(
                                        text = entry.amountText,
                                        fontSize = 12.sp,
                                        color = colors.textTitle,
                                        modifier = Modifier.padding(start = 10.dp),
                                    )
                                }
                            }
                        }
                    }
                }
            }

            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        text = "分类排行",
                        fontSize = 11.sp,
                        color = colors.textSecondary,
                        modifier = Modifier.weight(1f),
                    )
                    if (vm.hasMoreRanks) {
                        PillButton(
                            text = if (vm.isRankExpanded) "收起" else "查看全部",
                            onClick = { if (vm.isRankExpanded) vm.showTopRanks() else vm.showAllRanks() },
                            fontSize = 10.sp,
                            padding = PaddingValues(horizontal = 8.dp, vertical = 2.dp),
                        )
                    }
                }
            }

            item {
                AppCard(modifier = Modifier.fillMaxWidth()) {
                    if (vm.snapshot.hasExpense) {
                        Column(verticalArrangement = Arrangement.spacedBy(7.dp)) {
                            vm.visibleRankRows.forEach { row ->
                                RankRow(row = row, onClick = { vm.selectCategory(row.label) })
                            }
                        }
                    } else {
                        Text(
                            text = "这个范围内还没有支出记录",
                            fontSize = 11.sp,
                            color = colors.textSecondary,
                        )
                    }
                }
            }

            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        text = "近 12 个月支出",
                        fontSize = 11.sp,
                        color = colors.textSecondary,
                        modifier = Modifier.weight(1f),
                    )
                    Text(
                        text = vm.snapshot.peakMonthText,
                        fontSize = 10.sp,
                        color = colors.textSecondary,
                    )
                }
            }

            item {
                AppCard(modifier = Modifier.fillMaxWidth()) {
                    TrendBars(vm.snapshot.trendBars, Modifier.fillMaxWidth())
                }
            }

            item {
                SectionTitle("收支概览")
                AppCard(modifier = Modifier.fillMaxWidth()) {
                    Row {
                        OverviewCell(
                            label = "支出",
                            value = vm.snapshot.totalExpenseText,
                            valueColor = colors.negative,
                            modifier = Modifier.weight(1f),
                        )
                        OverviewCell(
                            label = "收入",
                            value = vm.snapshot.totalIncomeText,
                            valueColor = colors.positive,
                            modifier = Modifier.weight(1f),
                        )
                    }
                    Spacer(Modifier.height(10.dp))
                    Row {
                        OverviewCell(
                            label = "笔数",
                            value = vm.snapshot.entryCountText,
                            valueColor = colors.textTitle,
                            modifier = Modifier.weight(1f),
                        )
                        OverviewCell(
                            label = vm.snapshot.topCategoryText.ifEmpty { "最高 —" },
                            value = vm.snapshot.averagePerDayText,
                            valueColor = colors.textTitle,
                            modifier = Modifier.weight(1f),
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun LegendRow(segment: DonutSegment, onClick: () -> Unit) {
    val colors = LocalAppColors.current

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(5.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 4.dp, vertical = 3.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            modifier = Modifier
                .width(3.dp)
                .height(14.dp)
                .clip(RoundedCornerShape(1.5.dp))
                .background(colors.categoryColor(segment.colorKey)),
        )
        Spacer(Modifier.width(7.dp))
        Text(
            text = segment.label,
            fontSize = 11.sp,
            color = colors.textTitle,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        Text(
            text = segment.amountText,
            fontSize = 11.sp,
            color = colors.textTitle,
            modifier = Modifier.padding(start = 8.dp),
        )
        Text(
            text = com.ksayus.papercoin.core.Formats.percent(segment.fraction),
            fontSize = 10.sp,
            color = colors.textSecondary,
            textAlign = TextAlign.End,
            modifier = Modifier.width(44.dp),
        )
    }
}

@Composable
private fun CategoryPicker(vm: StatisticsViewModel) {
    val colors = LocalAppColors.current
    var expanded by remember { mutableStateOf(false) }

    Box {
        PillButton(
            text = vm.selectedCategory ?: "选择分类",
            onClick = { expanded = true },
            fontSize = 10.sp,
            padding = PaddingValues(horizontal = 10.dp, vertical = 3.dp),
        )

        DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
            if (vm.categories.isEmpty()) {
                DropdownMenuItem(
                    text = { Text("暂无分类", fontSize = 11.sp, color = colors.textSecondary) },
                    onClick = { expanded = false },
                )
            } else {
                vm.categories.forEach { category ->
                    DropdownMenuItem(
                        text = { Text(category, fontSize = 12.sp, color = colors.textTitle) },
                        onClick = {
                            vm.selectCategory(category)
                            expanded = false
                        },
                    )
                }
            }
        }
    }
}

@Composable
private fun OverviewCell(
    label: String,
    value: String,
    valueColor: Color,
    modifier: Modifier = Modifier,
) {
    val colors = LocalAppColors.current

    Column(modifier = modifier) {
        Text(
            text = label,
            fontSize = 10.sp,
            color = colors.textSecondary,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Text(
            text = value,
            fontSize = 14.sp,
            fontWeight = SemiBold,
            color = valueColor,
            modifier = Modifier.padding(top = 2.dp),
        )
    }
}

private fun statisticsFactory(container: AppContainer) = viewModelFactory {
    initializer { StatisticsViewModel(container.repository) }
}
