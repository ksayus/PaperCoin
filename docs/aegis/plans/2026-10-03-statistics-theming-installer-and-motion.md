# PaperCoin 1.3.0：统计可视化、动效统一、安装路径选择

## Goal

为 PaperCoin 增加消费可视化（圆环 + 分类排行 + 月度趋势 + 下钻 + CSV 导出）作为第四个导航页；
按 `TextAni` 那套动效风格统一全项目动画；安装程序支持首次安装选择目录、升级时自动定位已安装路径；
版本升到 1.3.0 并发版。

## Architecture

沿用现有的四层结构，不新增项目、不引入第三方依赖。

| 关注点 | 归属 | 理由 |
|---|---|---|
| 分类 → 颜色槽位（纯函数） | `PaperCoin.Core` | 无 WPF、无 IO 的确定性映射，是服务层与界面层的共同契约 |
| 分类聚合查询 | `PaperCoin.Service.BillRepository` | 仓库已经持有全量内存索引，聚合零额外 IO；放别处会多一个数据所有者 |
| 统计页状态与派生数据 | `PaperCoin.ViewModels.StatisticsViewModel` | 与现有三个 ViewModel 同构 |
| 图表绘制 | `PaperCoin.Controls`（3 个 UserControl） | 与 `SwitchToggle` 一致：自绘控件进 Controls |
| 颜色字面量 | `Themes/Dark.xaml` / `Bright.xaml` | 现有主题切换机制按 key 就地替换，新色板必须走同一通道才能跟着主题过渡 |

## Tech Stack

WPF / .NET 10，LAE 动画引擎，CommunityToolkit.Mvvm，Serilog。**不新增 NuGet 包**。

## Baseline / Authority Refs

- `README.md`：项目结构与构建方式、发版流程（版本号两处、UpgradeCode 不可换）
- `SESSION-NOTES.md`：MSI 覆盖规则（文件版本）、`RemoveExistingProducts` 在 6550、vdproj 的 BOM 坑、
  `dpplg.dll` 节点 GUID 表、`Dotnet build -m:1`、`devenv.com` 不在 PATH
- `PaperCoin.Setup/PaperCoin.Setup.vdproj`：现有 `Locator` 节点、`UserInterface` 三组对话框
- 用户提供的 `TextAni.cs`：动效风格基准（缓动、时长、弹性回弹、`TaskCompletionSource` 串行）

## Compatibility Boundary

**不变**：磁盘布局 `Bills/yyyy/M/d/bills.json`、`BillEntry` 的 JSON 字段、配置文件名与字段、
注册表约定 `HKCU\Software\Ksayus\PaperCoin\InstallDir`、`UpgradeCode {CDCA03BC-…}`、
LAE 为外部引擎（一个字不改）、窗口尺寸 420×560。

**变**：`Themes/*.xaml` 新增 key（Dark 与 Bright 必须同集合）、`MainWindow` 新增第四个导航项、
`MainWindow.xaml` 的窗口初始尺寸（为了让统计页容得下，见 T5）、vdproj 的 `ProductVersion` /
`ProductCode` / `PackageCode` 与正常序列的对话框组。

## Aegis Visibility

本轮同时新增一个界面所有者（统计页与三个自绘图表控件）、一条新的数据读取契约
（仓库的分类聚合）、一个分发面（安装程序路径选择）与版本发布。这些都需要在动手前定下
唯一所有者与验证方式，所以用一份计划固定边界。

## Requirement Ready Check

| 项 | 状态 |
|---|---|
| 已确认来源 | 用户本轮请求 + 8 项选择题的答复 |
| 范围 | 统计页可视化、动效统一、安装程序、版本与发版 |
| 场景 | 见每个任务的黑盒验收 |
| 验收证据 | `dotnet build -m:1` 0 错误；`tools/UiSmokeTest` 全绿；MSI 数据库复核；Release 资产存在 |

无待定项。

## Change Necessity

- **统计页**：现状没有任何消费汇总能力，配置或文档改不出来，必须新增代码。
  最小边界 = 1 个 ViewModel + 1 个 View + 3 个自绘控件 + 1 个仓库聚合方法 + 1 个纯函数色板。
- **动效**：现状动画散落在各 View 的代码后置，风格与 `TextAni` 不一致（部分用默认 300ms、
  缺少弹性回弹与串行收尾）。最小边界 = 只改既有动画参数与新增少量状态反馈，不动布局结构。
- **安装程序**：现状正常安装序列里根本没有"安装文件夹"对话框，这是配置缺失，必须改 vdproj。
  最小边界 = 恢复一个对话框节点并加条件，不动权限模型。
- **版本**：MSI 按文件版本决定是否覆盖，不升版本等于本次发版是空包，必须改。

## Ripple Signal Triage

触发信号：**持久化读路径**（仓库新增聚合查询）、**跨模块共享契约**（分类 → 颜色 key）、
**分发面**（安装程序）。

| 关注点 | 结论 |
|---|---|
| 唯一所有者 | 分类颜色槽位由 `Core.CategoryPalette` 独占；分类聚合由 `BillRepository` 独占；统计派生数据由 `StatisticsViewModel` 独占 |
| 下游消费者 | 统计页三个图表；今日/日历卡片的分类色条 |
| 契约风险 | 颜色 key 是主题字典与 `CategoryPalette` 的隐式约定 → 用测试 20 求差集兜住 |
| 回退 / 退休 | 不新增回退分支。`SaveAsync` / `UpsertEntryAsync` / `DeleteDayAsync` 的既有语义不动，只在统计读取侧加"索引过期"标记 |
| 兼容 | 旧数据文件照读，无迁移 |

## TDD Route

- Mode: `off`（本会话未开启自动 TDD，用户也未要求 strict）
- Decision: `light`
- Authority: 项目既有约定 —— `tools/UiSmokeTest` 是手工驱动的冒烟宿主，无测试框架、无断言库
- Test posture: 每个任务先做最小改动，再用"构造真实 View → 量它 → 驱动命令 → 断言"的方式补冒烟项；
  对每一条新增断言做**反证**（临时把实现改回错的，确认断言真的失败）
- Verification: `dotnet build PaperCoin.sln -m:1` + `dotnet run --project tools/UiSmokeTest`

## Plan Pressure Test

- **所有者/契约/退休**：通过。三个所有者边界清晰，无重复所有者，无遗留路径需要退休
- **上层架构**：通过。复用现有四层，不新建项目。唯一新抽象是"颜色槽位"这个纯函数
- **验证范围**：通过。界面改动由冒烟宿主覆盖，MSI 改动由数据库复核覆盖，两者都是既有手段
- **可执行性**：通过。任务粒度是"一个文件或一组同属文件 + 一条可执行验证"

## Execution Route

`inline`。理由：任务之间共享 `Themes/*.xaml`、`MainWindow.xaml`、`UiSmokeTest/Program.cs`
三个文件的写权，并行会互相踩；且每步都要编译验证，串行总成本更低。

`User confirmation required: no — 全部边界已在 8 项选择题中确认`

---

## 任务

### T1 分类色板：颜色槽位纯函数 + 主题色 key

**文件**
- 新增 `PaperCoin.Core/CategoryPalette.cs`
- 修改 `PaperCoin/Themes/Dark.xaml`、`PaperCoin/Themes/Bright.xaml`
- 修改 `PaperCoin/Icon/AppIcons.xaml`
- 修改 `tools/UiSmokeTest/Program.cs`（第 1 项 key 清单 + 新增第 20 项）

**改动**
1. `CategoryPalette`：`SlotCount = 10`，`ColorKey(string? category)` 用 FNV-1a 对
   `category.Trim().ToLowerInvariant()` 求稳定哈希（**不用 `string.GetHashCode`**：.NET Core 每进程随机化，
   同一分类重启后会换色），空/空白分类固定映射到 `CategoryColorOther`。
   `ThemeKey(slot)` 返回 `CategoryColor01`…`CategoryColor10`，`ThemeKeyOf(category)` 是前两者的组合。
2. 两个主题各加 11 个 `SolidColorBrush`。色相按色轮等距铺开，明度对齐现有 `ColorPositive`/`ColorNegative`
   （暗色底取高亮、亮色底取中深），保证环上相邻段可分辨。
3. `AppIcons.xaml` 新增 `IconChart`（圆环+扇区矢量几何）。字体只有 19 个码位且无图表字形，
   按 `IconDeleteGeometry` 的既有做法给矢量几何。

**兼容**：新增 key，不改既有 key。旧主题字典少 key 时 `DynamicResource` 静默回退 —— 这正是测试 20 要拦的。

**验证**
- 第 1 项 key 清单补 11 个新 key
- 新增第 20 项：`CategoryPalette` 对同一输入恒返回同一 key（含大小写与首尾空格）、
  空分类落在 `Other`、10 个槽位都能被取到、每个 `ThemeKey` 在 Dark 与 Bright 里都存在
- **反证**：把哈希换成 `string.GetHashCode` 后再跑，断言必须报"同一分类两次调用返回不同 key"
  （跨进程不稳定，同一进程内其实稳定 —— 所以这一项改为断言"哈希实现不含 GetHashCode"的文字检查，
  见任务执行时的实际做法）

**产出**：分类有稳定颜色，且是主题可过渡的。

---

### T2 Service：仓库的分类聚合查询

**文件**：修改 `PaperCoin.Service/BillRepository.cs`

**改动**
1. 新增 `public sealed record CategoryTotal(string Category, decimal Expense, int Count)`（放同文件或单独文件）
2. 新增 `GetCategoryExpenseAsync(DateOnly from, DateOnly to, CancellationToken ct = default)`：
   从内存索引取 `[from, to]` 区间内的条目 → **只取支出（`Amount < 0`）** → 按 `Category` 分组
   （大小写不敏感、忽略首尾空格、空值归为 `"未分类"`）→ 金额取绝对值求和 → 按金额降序、金额相同按名称排。
3. 新增 `InvalidateIndex()`：把 `_indexStale` 置回 `true`。供统计页显式作废聚合。
   既有写路径（`UpsertEntryAsync` / `DeleteDayAsync` / `SaveAsync`）已经在维护 `_index`，不改。

**为什么支出过滤放在仓库而不是 ViewModel**：金额正负是**领域约定**（README 里写明"正数为收入、
负数为支出"），由领域层解释才只有一个所有者；放界面层会让同一个约定散在多处。

**兼容**：只新增公开成员，既有调用方零影响。

**验证**：第 21 项 —— 造三条数据（餐饮 −50、餐饮 −30、工资 +1000）放到同一个日期，
断言聚合只返回一条 `餐饮`，金额 80、笔数 2；再断言日期区间边界（含首尾两天）。

**反证**：去掉 `Amount < 0` 过滤，断言必须失败并报出"聚合里出现了收入分类 工资"。

**产出**：统计页拿到的是已经按领域口径算好的分类支出。

---

### T3 统计页 ViewModel

**文件**：新增 `PaperCoin/ViewModels/StatisticsViewModel.cs`

**改动**（全部派生属性，无持久化状态 —— 与 `CalenderBillViewModel` 的立场一致：统计口径不进配置）

1. 时间范围：`RangeKind`（`Month` / `Year` / `All`），默认 `Month`。
   `RangeText` 显示为 `2026-10` / `2026` / `全部`。
2. `LoadAsync()`：按范围调 `BillRepository` 的 `LoadMonthAsync` / `LoadYearAsync` / `LoadAllAsync`，
   在内存里筛一次区间，然后一次算出三份数据：
   - `Segments`：`DonutSegment`（`Label`、`Amount`、`Percent`、`ColorKey`）—— 圆环用
   - `RankRows`：`CategoryRankRow`（序号、名称、金额、笔数、占比、`ColorKey`）—— 排行用
   - `TrendBars`：`MonthBar`（`Label` = `MM月`、`Amount`、`HeightRatio`）—— 近 12 个月趋势用
3. 汇总：`TotalExpenseText`、`TotalIncomeText`、`EntryCountText`、`TopCategoryText`、
   `AveragePerDayText`。
4. 下钻：`Focus`（选中的分类，null 表示未下钻）、`FocusEntries`（该分类在当前范围内的流水，
   按日期升序）、`OpenFocusCommand` / `CloseFocusCommand`。
5. CSV 导出：`ExportCsvCommand`，写 `Constants.AppDataFolder/Export/papercoin-统计-<范围>-<时间戳>.csv`，
   **UTF-8 带 BOM**（否则 Excel 打开中文乱码），表头 `日期,描述,分类,金额`；
   成功后 `Notice` 给出路径，失败给出原因。
6. `RefreshToken`：从仓库读一个单调递增的版本号，变化时才重算 —— 避免每次切回页面都重算。

**验证**：第 22 项 —— 造跨两个月的数据，驱动 `RangeKind` 切换，断言三份数据的条数与合计
在 `Month` / `Year` 下分别正确；驱动 `ExportCsvCommand` 后断言文件存在、首三字节是 `EF BB BF`、
行数等于范围内条数。

**反证**：把 BOM 去掉，断言必须报"CSV 缺少 UTF-8 BOM"。

**产出**：统计页的界面只需要绑定，不再做任何计算。

---

### T4 图表控件（纯 WPF 自绘 + LAE 动画）

**文件**
- 新增 `PaperCoin/Controls/DonutChart.cs`
- 新增 `PaperCoin/Controls/MonthTrendChart.cs`
- 新增 `PaperCoin/Controls/CategoryBarRow.xaml(.cs)`（或并入统计页模板）

**改动**
1. `DonutChart`：`ItemsSource`（`IEnumerable<DonutSegment>`）+ `Thickness` + `RevealAll()`。
   每段用 `Path` + `ArcSegment` 画在 `[startAngle, endAngle]` 上，`Stroke` 绑 `DynamicResource`
   `{Binding ColorKey}`，`StrokeThickness = Thickness`，`StrokeStartLineCap/EndLineCap = Flat`。
   起点固定在 12 点方向（−90°）。
2. **动画**：不逐帧重算几何，改为给每段的 `Path` 挂一个 `RotateTransform` + 透明度做"扫出"，
   并且每段的扇区角度提前算好、按顺序用 `Delay` 错开淡入 —— 保证动画是 LAE 驱动的常量属性写入，
   没有每帧几何重建。`RevealAll()` 里一次性 `Play()` 整组，`OnComplete` 回调给测试计数。
3. `MonthTrendChart`：12 根 `Rectangle`，高度 = `HeightRatio × 可用高度`，从 0 用 `Easing.OutBack(1.6)`
   弹起，`Delay = index × 40ms`。每根顶部标金额、底部标月份。
4. 两个控件都在 `SizeChanged` 时重算几何并**重播一次**（避免窗口拉伸时留下按旧尺寸算的弧）。
5. 计数入口：`DonutChart.SegmentsPlayed`、`MonthTrendChart.BarsPlayed`，供冒烟测试断言动画真的接上了。

**验证**：第 23 项 —— 造 3 段（占 50% / 30% / 20%）挂到离屏窗口，`RevealAll()` 后断言
`SegmentsPlayed == 3`，且三段弧的几何总角度 ≈ 360°（允许 0.5° 误差）、半径与 `Thickness` 一致。

**反证**：把角度分配改成平均分，断言必须报"扇区角度与占比不符"。

**产出**：可交互、动画化的圆环与柱状图，零第三方依赖。

---

### T5 统计页视图 + 导航接入

**文件**
- 新增 `PaperCoin/Views/Statistics.xaml(.cs)`
- 修改 `PaperCoin/MainWindow.xaml(.cs)`
- 修改 `PaperCoin/App.xaml.cs`（注册 `StatisticsViewModel`）

**改动**
1. `Statistics.xaml`：顶部时间范围分段（复用 `ModeRadio` 样式）→ 下方 `TabControl` 风格三段视图
   （分类占比 / 分类排行 / 月度趋势）→ 不足时底部汇总行。
   每段视图切换时按 `Settle()` 的做法（从下方 6px 滑入 + 淡入）落定。
2. 下钻面板：复用 `BillEditorOverlay` 的遮罩 + 滑入做法（`Panel.ZIndex`、`Esc` 关闭）。
3. `MainWindow`：`NavItem` 加 `Statistics`，`NavPanel` 加按钮（用 `IconChart`），
   `NavigateTo` / `UpdatePageDataContext` 加分支，`App` 注册 `AddTransient<StatisticsViewModel>`。
4. **窗口尺寸**：统计页需要的最小高度比 560 大。把 `MainWindow` 的初始 `Height` 提到 640、
   `Width` 提到 480，并保留 `MaxHeight = SystemParameters.PrimaryScreenHeight`。
   420×560 是 README 里写明的现状，改完同步改 README。

**验证**
- 第 24 项：`NavigateForTest("Statistics")` 后断言页面非空、`AssertNoCollapse` 通过、
  三个分段按钮都能驱动视图切换、侧边栏指示器对准第四个按钮
- 既有第 3 项（指示器随尺寸重算）在四个按钮下仍然通过 —— 它是按按钮布局算的，不是硬编码

**反证**：把统计页从 `NavigateTo` 的 switch 里去掉，断言必须报"Statistics 页面未挂载"。

**产出**：第四个导航页可用。

---

### T6 卡片分类色块 + 动效统一化

**文件**：`PaperCoin/Views/TodayBill.xaml(.cs)`、`CalenderBill.xaml(.cs)`、`Settings.xaml(.cs)`、
`PaperCoin/Views/BillEditorOverlay.xaml(.cs)`、`PaperCoin/Themes/Controls.xaml`、`PaperCoin/Controls/SwitchToggle.xaml.cs`

**改动**
1. **分类色块**：今日与日历的流水卡片，在描述前加一个 3×14 的圆角色条，
   `Background` 用 `{Binding CategoryColorKey}`（`BillEntry` 加一个只读派生属性
   `CategoryColorKey => CategoryPalette.ThemeKeyOf(Category)`，并在 `OnCategoryChanged` 里通知）。
2. **动效统一**：按 `TextAni` 的节奏把全项目动画收敛成一套具名常量（放 `PaperCoin/Views/Motion.cs`）：
   - 交互动效 `Fast = 110ms` / `Normal = 200ms` / `Playful = 380ms`
   - 缓动：进入用 `OutCubic`，往复用 `InOutCubic`，落定用 `OutBack(2.0)` 收尾
   - 「按下去 → 弹回来」统一走 `TextAni` 里那种**两段串行**（去程 + 回程）而不是单段 overshoot
3. **具体补动效**（只加在有状态变化的地方，不做无意义装饰）：
   - 汇总金额变化时脉冲一次（`TodayBill` 的 `TotalTextBlock`，仅当数值真的变了）
   - 日历日/月/年三粒度切换时，粒度按钮做一次选中回弹
   - 设置页 `SwitchToggle` 的滑块过冲（现有实现是线性到位）
   - 编辑面板保存成功关闭时，列表新卡片做一次更强的入场
4. **不动**：`ThemeRenderer` 的过渡机制、`CardListEntrance` 的错开算法、`MainWindow` 的指示器定位。

**验证**：既有第 10 / 12 / 13 项必须仍然通过；新增第 25 项断言统一常量被真正使用
（统计 `Motion.Playful` 与 `UiSmokeTest` 里量到的实际时长一致），并对"金额变化脉冲"断言播放计数。

**反证**：把脉冲的触发条件改成无条件播放，断言必须报"未变化时不应播放"。

**产出**：全项目动效风格一致，且新增了状态反馈。

---

### T7 安装程序：首次可选路径 + 升级自动定位

**文件**：`PaperCoin.Setup/PaperCoin.Setup.vdproj`（**必须保持 UTF-8 BOM**）

**改动**
1. 在**正常安装序列**（`Name = "8:#1900"` 且 `Sequence = "3:1"` 的那一组，即无 `VsdAdmin` 前缀的）
   的 `Welcome` 与 `Confirm` 之间插入 `VsdFolderDlg.wid` 节点，`Sequence = 200`，
   `DisplayName = "8:安装文件夹"`，并给它的 `InstallDir` 属性设置条件 `Installed = ""`。
2. `Locator` 节点不动（升级时它把 `TARGETDIR` 填成已安装路径，序列 100 早于对话框的 200）。
3. `DIRCA_TARGETDIR`（序列 750，条件 `TARGETDIR=""`）不动 —— 它是"搜不到才用默认值"的那一层。
4. 前置校验：编辑前备份 vdproj；编辑后核对前三字节是 `EF BB BF`。

**为什么用 `Installed = ""` 而不是靠 `AppSearch` 自然生效**：`AppSearch` 只把路径**填进**对话框，
用户仍能改；而数据目录跟着程序目录走（`Environment.CurrentDirectory\PaperCoin`），改了就"看不见"账单。
所以升级路径要**不给改**。

**验证**
- `dotnet publish` → `devenv.com PaperCoin.sln /Build Release` 出包 0 错误
- 查 MSI 数据库：`InstallUISequence` 里 `FolderForm` 存在且 `Condition` 为 `Installed = ""`；
  `ControlEvent` 里 `InstallDir` 的 `SetTargetPath` 指向 `TARGETDIR`；
  `RegLocator` / `AppSearch` 与 1.2.2 一致；`RemoveExistingProducts` 仍是 6550
- **实际安装测试**：`msiexec /i PaperCoin.msi /qn TARGETDIR=<临时目录>` 装一份，
  断言文件落在那；再断言程序启动时把该目录写回注册表 `InstallDir`；测完 `/x` 卸载并清理

**反证**：把 `Condition` 去掉，断言必须报"升级路径对话框没有条件，用户可改路径"。

**产出**：首次装能选目录，升级自动锁定。

---

### T8 版本号、冒烟测试扩展与全量验证

**文件**：`Directory.Build.props`、`PaperCoin.Setup/PaperCoin.Setup.vdproj`、`README.md`、
`tools/UiSmokeTest/Program.cs`

**改动**
1. `Directory.Build.props`：`<Version>1.2.2</Version>` → `1.3.0`
2. vdproj：`ProductVersion` → `8:1.3.0`、换新 `ProductCode` 与新 `PackageCode`，
   `UpgradeCode` 保持不变
3. README：更新功能清单（统计页）、窗口尺寸、安装路径一节（首次可选 / 升级锁定）、
   项目结构（新增文件）、测试项数量、版本号
4. 冒烟测试补第 20–25 项，并做逐项反证

**验证**
- `dotnet build PaperCoin.sln -m:1` 0 错误
- `dotnet run --project tools/UiSmokeTest` 全绿（19 + 6 = 25 项）
- 第 16 项（三个程序集版本 / props / vdproj ProductVersion 一致性）在 1.3.0 下通过
- vdproj BOM 仍在

**产出**：一条命令能证明这次改动是自洽的。

---

### T9 提交、标签与 Release

**改动**
1. 分主题提交（不把全部改动塞进一个 commit）：
   统计可视化 / 动效统一 / 安装程序 / 版本与文档
2. 打标签 `v1.3.0`（用户指定 `vx.x.x` 格式；仓库现有的 `1.0.0` / `1.2.2` 是无 `v` 前缀的历史标签，不去改）
3. 推送 `master` 与标签到 `origin`
4. 写 Release 说明（标题 `PaperCoin-v1.3.0`，正文分「更新功能 / 优化 / Bug 修复」三段），
   安装包留在 `PaperCoin.Setup/Release/`，由用户在网页上创建 Release 并上传

**验证**：`git log --oneline origin/master..master` 为空；`git tag -l v1.3.0` 有输出；
Release 说明文件存在且三段齐全。

**产出**：可发布的 1.3.0。

---

## Risks

| 风险 | 处置 |
|---|---|
| `ThemeRenderer` 按 key 就地替换，新主题字典少 key 会静默回退 | 测试 20 求差集 |
| 720 张分类卡片的色条让 `ItemsControl` 每帧重算 | 色条是静态 `Border`，不参与任何动画，与现有阴影 Border 同策略 |
| 第四段导航让指示器定位算错 | 指示器按按钮的实际布局算（`TranslatePoint`），不硬编码；测试 3 在四按钮下复跑 |
| vdproj 编辑掉 BOM → VS 打不开 | 编辑后立即核对前三字节 |
| 装到 `Program Files` 因权限失败 | 保持每用户安装；对话框里给默认值 `%LOCALAPPDATA%\PaperCoin`，README 写明约束 |
| `dotnet build` 多进程还原静默失败 | 一律加 `-m:1` |

## Retirement

无路径需要退休。`TodayBillViewModel.TotalText` 等既有汇总属性保留（今日页仍用），
统计页不复用它们 —— 两者口径不同（今日页含收入，统计页只看支出），合并会混淆语义。
