# PaperCoin

Windows 桌面记账程序。WPF + .NET 10，单机运行，账单以按日期分层的 JSON 文件保存，不依赖数据库和网络。

主窗口是一个 480×640 的无边框窗口：左侧导航栏（今日 / 日历 / 统计 / 设置），右侧内容区。

## 功能

### 今日账单

- 记录当天流水：描述、金额、分类、日期。金额约定正数为收入、负数为支出。
- 列表按录入顺序展示，卡片带悬浮抬升、按下压缩、单击编辑。
- 新增与编辑走浮在列表之上的面板，不占布局行，列表不会因面板开合而重排。
- 分类是可编辑下拉框：候选来自账单里已经用过的分类，也可以直接输入新的；保存后新分类自动进入候选。
- 快捷键：`Esc` 取消，`Ctrl+Enter` 保存。
- 列表为空时显示引导提示。

### 日历账单

- 日 / 月 / 年三种粒度共用同一个当前定位，切换粒度不会丢掉已经翻到的位置。
- 上一页 / 下一页的步长跟随当前粒度，日视图走一天、月视图走一月、年视图走一年。
- 日视图右下角的 ＋ 可以像今日账单那样直接记一笔，草稿日期默认是当前定位。
- 月视图固定 6×7 网格；有记录的日期带底色，格子里显示当日合计。
- 年视图展示 12 个月的合计。
- 明细列表在三种视图下共用，内容分别是当天 / 当月 / 当年的流水。

### 统计

- 圆环图按分类看总支出去向，圆心显示总支出与笔数；每段颜色由分类名哈希决定，同一分类在任何范围、任何一次启动里都是同一种颜色。
- 分类排行：金额、笔数、占比，条形长度取相对第一名的比例。
- **分类明细**：点排行行、点图例色块，或用"分类明细"右侧的下拉选一个分类，列出它的**全部**流水（不受上面的时间范围限制），并给出笔数、合计与占全部支出的比例。想核对某个分类的完整流水时，按范围过滤反而碍事，所以这块刻意不跟着范围走。
- 近 12 个月支出趋势柱状图，柱高取相对最高月的比例。
- 时间范围：本月 / 本年 / 全部，趋势不受范围限制（趋势本身就是跨范围的视图）。
- 导出 CSV：按当前范围导出明细与合计，UTF-8 带 BOM，Excel 直接打开不乱码。
- 图表都有展开动效，数据换范围或换页时重播。

### 设置

- 外观有**两个独立维度**，各三个选项，组合出 6 套调色板（见下文「主题」）：
  - 明暗：跟随系统 / 暗色 / 亮色
  - 风格：经典 / Windows / 液态玻璃
- 切换时所有颜色以 300ms 过渡到位，而不是瞬间跳变，并写入配置。
- 开机自动启动：直接写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
- 自动更新：目前只保存开关与更新源，检查版本与下载逻辑尚未接入。
- 打开数据目录、打开 GitHub Releases 页面、恢复默认设置。

## 项目结构

| 项目 | 目标框架 | 职责 |
|------|----------|------|
| `PaperCoin` | `net10.0-windows` | WPF 界面层：视图、ViewModel、主题渲染、自绘控件（含圆环图）与转换器 |
| `PaperCoin.Core` | `net10.0` | 领域模型与路径常量，不依赖 WPF |
| `PaperCoin.Service` | `net10.0` | 配置读写、主题状态、账单持久化 |
| `PaperCoin.Setup` | — | Visual Studio 安装项目（`.vdproj`），生成 MSI 与 `setup.exe` |
| `tools/UiSmokeTest` | `net10.0-windows` | 手工驱动的 UI 冒烟测试，不在解决方案内 |

```
PaperCoin/
├── PaperCoin.sln                   解决方案（LAE / Core / Service / 安装项目 / 界面）
├── PaperCoin/                      WPF 界面层
│   ├── App.xaml(.cs)               启动流程：单实例、日志、依赖注入容器、主题装载
│   ├── MainWindow.xaml(.cs)        自绘标题栏、侧边栏导航与指示器动效
│   ├── ThemeRenderer.cs            运行时原地替换颜色字典
│   ├── Controls/                   SwitchToggle、DonutChart 等自绘控件
│   ├── Converters/                 值转换器
│   ├── Icon/                       图标资源与 Iconfont.ttf
│   ├── Themes/                     Dark / Bright / Controls / Generic
│   ├── ViewModels/                 四个页面的 ViewModel，以及两页共用的编辑面板状态
│   └── Views/                      四个页面、两页共用的浮层编辑面板、列表入场动效、Motion 动效常量
├── PaperCoin.Core/                 领域模型与路径常量
├── PaperCoin.Service/              配置、主题状态、账单持久化
├── PaperCoin.Setup/                安装项目（生成物不入库）
├── tools/UiSmokeTest/              UI 冒烟测试
└── LivelyAnimationEngine/          LAE 动画引擎（未纳入版本管理，见下文）
```

主要依赖：CommunityToolkit.Mvvm 8.4、Microsoft.Extensions.Configuration 与 DependencyInjection 10.0、Serilog 4.4。动画统一交给 LAE（Lively Animation Engine），以项目引用的方式使用。

## 构建与运行

需要 .NET 10 SDK 和 Windows。

```powershell
dotnet build PaperCoin.sln
dotnet run --project PaperCoin/PaperCoin.csproj
```

### 打包安装程序

`.vdproj` 是 Visual Studio 专有的项目类型，MSBuild 不认识它（`dotnet build` 只会给一条 `MSB4078` 警告并跳过）。所以走两段：先发布应用，再用 `devenv` 生成安装包。

```powershell
dotnet publish PaperCoin/PaperCoin.csproj -c Release

$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
& "$vs\Common7\IDE\devenv.com" PaperCoin.sln /Build Release
```

`devenv.com` 不在 PATH 上，上面的 `vswhere` 用来定位它；如果在 VS 开发者命令提示符里，直接 `devenv.com PaperCoin.sln /Build Release` 即可。

产物在 `PaperCoin.Setup/Release/`，该目录已被 `.gitignore` 排除。

安装项目引用的是 **PublishItems**（发布产物）而不是主输出：VS 安装项目对 .NET Core 项目的主输出只能取到 `PaperCoin.dll` 与 `runtimeconfig.json`，会漏掉 exe 和全部依赖 DLL。因此**必须先 `dotnet publish`**，否则安装包内容不完整。

### 版本号

产品版本只有一个来源：根目录 `Directory.Build.props` 的 `<Version>`。三个自有程序集都从这里取，`Constants.AppVersion` 也改成读程序集版本，不再写字面量。

发版时改**两处**，必须一起改：

1. `Directory.Build.props` 的 `<Version>`
2. `PaperCoin.Setup/PaperCoin.Setup.vdproj` 的 `ProductVersion`，并换一个新的 `ProductCode`

顺手把 `PackageCode` 也换掉更稳。这两个码是两回事：`ProductCode` 标识**产品**，升级时必须换，`RemovePreviousVersions` 靠它认出"同一产品的新版本"；`PackageCode` 标识**这个包**，同一版本重新出包时换了可以绕开 Windows 的包缓存。`UpgradeCode` **绝不能换**——换了旧版就不被认为同一产品，升级会变成并存。

第二条不是形式主义。MSI 覆盖文件时比较的是**文件版本**：新版本不大于已安装版本就直接跳过。曾经 `PaperCoin.Core` 与 `PaperCoin.Service` 没设版本（都是 `1.0.0.0`），升级安装后安装目录里留住了旧的 `Service.dll`，而新的 `PaperCoin.dll` 调用它里面不存在的方法——点 + 号就以 `MissingMethodException` 崩掉。无版本的附属文件（`.deps.json`、`.pdb`、`.ico`）反而正常更新，因为它们按日期与大小判断。

冒烟测试第 16 项比对这三个载体（程序集文件版本、`Directory.Build.props`、安装项目 `ProductVersion`），把"忘了给某个项目设版本"挡在构建期。

### 快捷方式图标

安装出来的桌面快捷方式与"应用和功能"里的图标曾经是空的。原因不在 `.ico` 文件本身，而在 MSI 的 `Icon` 表：

MSI 规定**与快捷方式关联的图标必须是 PE 文件，且文件名扩展名要和快捷方式目标的扩展名一致**（[Icon Table](https://learn.microsoft.com/en-us/windows/win32/msi/icon-table)）。VS 安装项目把 `papercoin-icon.ico` 的字节**原样**塞进 `Icon` 表，只按目标扩展名把条目命名成 `_XXXX.exe`；安装时 Windows Installer 据此在 `%APPDATA%\Microsoft\Installer\{ProductCode}\` 写出一个 `.exe`，里面却是 ICO 字节。Shell 按 PE 去加载它，`LoadLibrary` 失败（错误 193），于是没有图标。

这一点在安装项目那一侧没有可用出口：`Icon` 指向 `.exe` 形式的输出组或文件节点会让 `devenv /Build` 直接报 `Unable to build shortcut named 'PaperCoin'`；只接受真实 `.ico` 文件，而只要给 `.ico`，产出的必然是那个坏条目。

**所以改成运行期修正**，实现在 `PaperCoin.Service/ShortcutRepair.cs`，每次启动由 `App.OnStartup` 调用：

- 图标内嵌在 `PaperCoin.Service` 程序集里（`PaperCoin.Service.csproj` 的 `EmbeddedResource`）。不声明成 `Content`/随文件部署，是为了避免和安装项目里那个独立 File 节点在 MSI 的 `File` 表里撞成两份同名文件
- 程序目录里有 `papercoin-icon.ico` 就直接用它（安装包会放一份），没有就从内嵌资源落盘到 `%LOCALAPPDATA%\PaperCoin\`
- 图标已经对了就什么都不做，所以不会每次启动都去动用户的桌面

**必须记住的坑**：对 `Load` 进来的 `IShellLink` 调 `SetIconLocation` 再 `Save`，**写盘后快捷方式的目标会落成图标文件的路径**，快捷方式直接点不开程序（新建的链不会，从磁盘加载的会）。所以 `Repair` 改成"先只读判断是否需要修，需要就用一个全新的链对象把目标、起始目录、图标一次写全"。冒烟测试第 19 项盯着"目标必须是程序本身"，就是防这个。

> 这属于绕开工具链缺陷的补丁，不是理想形态。要彻底干净，需要构建后改写 MSI 的 `Icon` 表（本机没有 `msidb.exe`），或换 WiX 之类的工具链。

**"应用和功能"里那个图标仍然修不了**：它由 MSI 的 `ARPPRODUCTICON` 决定，同样指向那个坏缓存文件，而应用侧改不到那里。要连它一起修，只能走上一条路（构建后改写 MSI）。

### 安装路径

首次安装可以选择装到哪个目录，升级时自动沿用已安装的位置、不再让用户改：

- 正常安装序列里有"安装文件夹"对话框（MSI 里的 `FolderForm`，默认 `[ProgramFilesFolder]\PaperCoin`）。
- 升级时 `AppSearch`（序列 100）先搜注册表 `Software\Ksayus\PaperCoin` 的 `InstallDir`，搜到就把 `TARGETDIR` 定在那儿；`DIRCA_TARGETDIR`（序列 750，条件是 `TARGETDIR=""`）只在搜不到时才填默认值。
- `FolderForm` 只在"全新安装"时出现：VSI 生成的那个显示动作 `VSDCA_FolderForm_AllUsers` 自带条件 `Installed=""`，升级时它不跑。

**这需要提权安装（`InstallAllUsers=True`、`ALLUSERS=1`）。** 这一条不是选出来的，是工具链逼出来的：VS 安装项目里"安装文件夹"对话框的显示动作被**硬编码**成只对 `ALLUSERS=1` 生效——整份 `Microsoft.VisualStudio.InstallerProjects` 扩展（`dpplg.dll` 的字符串表）里只有 `FolderForm_AllUsers` 这一个 FolderForm 动作，没有每用户版本。所以"免提权 + 首次可选任意路径"在 VSI 里做不出来，只能在两者之间选一个。

**数据目录因此搬到了 `%APPDATA%\PaperCoin`**（见下节）。装在 `Program Files` 里的程序目录普通权限写不进去，数据再跟着程序目录走就会写失败；而且路径可改之后，"数据在程序目录下"这件事本身就成了负担——用户换个安装位置，账就"看不见"了。

### 安装位置的记录

写入这一侧的代码在 `PaperCoin.Service/InstallLocation.cs`，程序每次启动都会把 `AppContext.BaseDirectory` 写进注册表。

**键路径与值名是它和安装包的约定**：安装包那侧写在 `PaperCoin.Setup.vdproj` 的 `"Locator"` 节点里（节点类型 `{CF66B7F7-B7DA-4B08-A67A-233430638C9C}`，`Root` 用 VS 自己的枚举值——`3:1` 是 HKLM、`3:2` 是 HKCU）。改一处必须同步改另一处。

1.3.0 起写 **HKLM**（每机器安装，安装时提权），安装包里配了**两个**定位器：

| 顺序 | Root | 作用 |
|---|---|---|
| 1 | HKLM | 1.3.0 及以后的位置 |
| 2 | HKCU | 1.2.x 留下的位置，从旧版升级时靠它认出旧路径 |

`InstallLocation.Read()` 同样 HKLM 优先、HKCU 兜底，两边的优先级顺序必须一致。

> VS 安装项目的这个格式没有公开 schema。上面那些节点 GUID 与字段名是从 `Microsoft.VisualStudio.InstallerProjects` 扩展的 `dpplg.dll` 里读出来的（每个 GUID 后面就跟着它的显示名），并用"构建后查 MSI 的 `RegLocator` / `AppSearch` 表"验证过。

### 数据目录迁移

1.2.x 的数据根是**程序目录**下的 `PaperCoin\`；1.3.0 起是 `%APPDATA%\PaperCoin`。不搬的话，升级上来的用户会看到一份空账——数据其实还在，只是没人去那儿找了。

`PaperCoin.Service/AppDataMigration.cs` 在 `App.OnStartup` 里、**任何服务读写数据目录之前**跑一次：

- 目标目录已经有账单就不动（已经搬过，或者本来就是全新安装）。
- 旧目录找两处：当前程序目录下的 `PaperCoin\`，以及注册表里记着的上一版安装位置下的 `PaperCoin\`。升级换过安装位置时，只有后者能找到旧数据。
- 是**复制**不是移动：旧目录原样留着，搬错了还能回头。
- 失败（旧目录不存在、权限不足）只记一条日志，不影响启动。

### 主题

外观是**两个独立维度**的组合，不是一条三选一的列表：

| 维度 | 选项 | 决定什么 |
|---|---|---|
| 明暗 | 跟随系统 / 暗色 / 亮色 | 用哪一套明度 |
| 风格 | 经典 / Windows / 液态玻璃 | 材质与配色的性格 |

组合出 6 套调色板，对应 `PaperCoin/Themes/` 下 6 本字典：

```
Dark          Bright          经典（沿用原有命名，不带风格后缀）
DarkWindows   BrightWindows   Fluent：纯色中性灰阶 + 系统蓝强调
DarkLiquid    BrightLiquid    半透明材质 + 发丝高光
```

经典那两本**不带风格后缀**是有意的：`Dark` / `Bright` 是 1.2.x 就写进配置的值，改名会让老用户的主题失效。

**6 本的 key 必须完全一致**，共 31 个。切换是按 key 就地替换条目（见 `ThemeRenderer`），少一个 key 时 `DynamicResource` 找不到就静默回退——表现是"某个控件颜色不对"且没有任何报错。冒烟测试第 1 项逐本比对，并按合成后的实际颜色验算正文与次要文字的对比度（WCAG AA：4.5:1 / 3:1）。

### 液态玻璃的背部高斯模糊

模糊**不是 WPF 算的**（`BlurEffect` 只能模糊元素自己的内容，模糊不了窗口背后的桌面），而是交给 DWM 的系统背板：它拿得到屏幕内容，把窗口背后按高斯核模糊后填进窗口背景，所以窗口拖到不同内容上时玻璃颜色会跟着变。实现在 `PaperCoin/BackdropBlur.cs`，用的是 `DWMWA_SYSTEMBACKDROP_TYPE = Acrylic`。

**硬约束：系统背板只对非分层窗口生效**，而 WPF 的分层窗口由 `AllowsTransparency=True` 打开，它同时带来圆角裁剪与 `DropShadowEffect`。两种能力互斥。所以分两条路：

| 风格 | 窗口 | 圆角 | 阴影 | 玻璃 |
|---|---|---|---|---|
| 经典 / Windows | 分层（`AllowsTransparency=True`） | WPF 裁 | WPF `DropShadowEffect` | 表面本就不透明，不需要 |
| 液态玻璃 | 非分层 | DWM | 系统给 | DWM Acrylic 真模糊 |

`AllowsTransparency` 在窗口句柄建好后改不了，所以判定必须发生在窗口构造之前 —— `ThemeManager.SavedStyleNeedsLayeredWindow()` 直接读配置文件，`App.OnStartup` 把结果存进 `App.UsesLayeredWindow`，`MainWindow` 只能读。本机不支持系统背板（非 Windows 11）时液态玻璃会退回不透明材质，那时照旧走分层窗口，不白丢圆角与阴影。

窗口背景在两种情况下都用 `Transparent`，含义不同：分层窗口下是真透明；非分层窗口下是"这个像素不着色"，让背板露出来。**这里若给不透明色（哪怕黑色）会把背板整个盖住，模糊白设。**

### 玻璃的明度层（ColorGlassScrim）

模糊本身不改变明度。窗口压在浅色内容上时模糊结果很亮，玻璃面板上的白字会读不清；压在深色内容上又反过来。所以玻璃风格在模糊层之上、面板之下垫了一层 `ColorGlassScrim`，把模糊结果的明度钉在固定一侧，对比度就不再看窗口背后是什么。其它风格这个键是 `Transparent`。

**明度不能交给 DWM 色调**：`DWMWA_BACKDROP_TINT_COLOR` 在部分 Windows 11 版本上被拒（本机 build 26300 实测 `DwmSetWindowAttribute` 返回失败）。依赖它就会出现"某些机器上浅色内容里白字读不清"。

> **已知取舍**：玻璃风格的正文对比度门槛是 **4.0:1**，而不是 WCAG AA 的 4.5:1。这是材质的物理限制而非疏漏 —— 模糊结果可能是纯白，而"玻璃"又必须让模糊透出来；要让白字在纯白模糊上稳过 4.5:1，面板 Alpha 得高到几乎不透明，玻璃就不成立了。实测最坏情形 4.2:1，仍清晰可读。其余四种调色板照旧按 4.5:1 卡（实测 12.8–17.4）。

**风格解析在 `ThemeManager`**：`Mode`（明暗）+ `Style`（风格）各自持久化到配置的 `Theme` 与 `ThemeStyle`，`Palette` 是按两者算出的字典名，`ResolvedThemeChanged` 带的就是它。

### 图标字体

`PaperCoin/Icon/Iconfont.ttf` 是自建的图标字体，码位与语义的对应关系集中写在 `Icon/AppIcons.xaml` 里，各 View 通过 `DynamicResource` 引用。

改字体时注意两件事：

- **码位可以新增，不能重排。** 各 View 按码位引用字形，把已有图标的码位挪走会让界面上的图标集体错位，而且不会报错。冒烟测试第 2 项会检查 `AppIcons.xaml` 里的每个码位都真的存在于字体中。
- **核对字形要用 GDI+，不要用 WPF。** 现存核对方式是用 `PrivateFontCollection` 按文件路径加载 TTF 逐个画出来看；无窗口的测试宿主里 WPF 解析不出 pack 资源字体，所有图标都会画成缺字方框，看起来像"字体坏了"，其实只是宿主限制。

当前字体 22 个码位，其中 4 个是后补的：房子、刷新（带 ¥）、统计/分析、拼图。统计项用的就是"统计/分析"（U+E6C3），早先字体里没有图表类字形，曾用矢量几何顶替。

### 关于 LAE

`LivelyAnimationEngine/` 被 `.gitignore` 排除，入库的只有引用它的项目文件。克隆之后需要先把 LAE 放到仓库根目录的同一层级：

```
<仓库根>/
├── LivelyAnimationEngine/
│   └── LAE/LivelyAnimationEngine.csproj
├── PaperCoin.sln
└── PaperCoin/
```

否则构建会报找不到 `..\LivelyAnimationEngine\LAE\LivelyAnimationEngine.csproj`。若暂时不需要动效相关的页面，也可以从 `PaperCoin.sln` 和 `PaperCoin.csproj` 里摘掉对它的引用。

## 数据存储

运行时数据放在 `%APPDATA%\PaperCoin\`（`Constants.AppDataFolder`）。1.2.x 时它在程序目录下，1.3.0 起搬到了用户配置目录，迁移逻辑见上文「数据目录迁移」。

| 路径 | 内容 |
|------|------|
| `Config/papercoin-settings.json` | 应用配置（主题、自动启动、自动更新） |
| `Logs/papercoin-<日期>.log` | 按天滚动的日志，保留 7 份 |
| `Bills/<年>/<月>/<日>/bills.json` | 当天全部流水。新增或编辑按记录自身的日期定位，与那一天的已有记录合并，不覆盖 |

目录结构本身就是索引：查询直接枚举 `Bills/` 下的年 / 月 / 日目录，没有独立的索引文件。手工增删目录同样能被正确识别。

也可以在设置页点"数据目录"直接打开账单文件夹。

> 开发时数据同样落在 `%APPDATA%\PaperCoin\`，与安装版共用（`AppDataFolder` 不再跟工作目录走）。需要一份干净数据时，把这个目录改名即可。

## 测试

```powershell
cd tools/UiSmokeTest
dotnet run
```

25 项检查，覆盖主题资源键对齐、图标码位是否存在于字体、侧边栏指示器随窗口尺寸重算、四个页面的布局与交互、保存链路的串行化与增量同步、动效是否真的向 LAE 注册、分类色板的稳定性与可解析性、仓库按分类汇总支出的口径、统计页三份派生数据（圆环 / 排行 / 趋势）与下钻、圆环弧角与占比是否一致、导出 CSV 是否带 UTF-8 BOM 等。

这里不引入测试框架：需要验证的是真实布局、真实绑定和真实交互，测试框架帮不上忙。程序直接构造 View、量它的尺寸、驱动它的命令与事件，把编译期看不出来的问题（缺少的 `StaticResource`、失效的 `RelativeSource`、模板名字域解析失败、布局塌陷、绑定路径写错）逼出来。

## 许可

`LivelyAnimationEngine/` 是独立的第三方动画引擎，MIT 许可，版权与条款见其目录下的 `LICENSE.txt`。本仓库其余部分未声明开源许可。
