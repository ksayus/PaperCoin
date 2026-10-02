# PaperCoin

Windows 桌面记账程序。WPF + .NET 10，单机运行，账单以按日期分层的 JSON 文件保存，不依赖数据库和网络。

主窗口是一个 420×560 的无边框窗口：左侧导航栏，右侧内容区，外观支持暗色 / 亮色 / 跟随系统。

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

### 设置

- 主题：跟随系统 / 暗色 / 亮色。切换时所有颜色以 300ms 过渡到位，而不是瞬间跳变，并写入配置。
- 开机自动启动：直接写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
- 自动更新：目前只保存开关与更新源，检查版本与下载逻辑尚未接入。
- 打开数据目录、打开 GitHub Releases 页面、恢复默认设置。

## 项目结构

| 项目 | 目标框架 | 职责 |
|------|----------|------|
| `PaperCoin` | `net10.0-windows` | WPF 界面层：视图、ViewModel、主题渲染、自绘控件与转换器 |
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
│   ├── Controls/                   SwitchToggle 等自绘控件
│   ├── Converters/                 值转换器
│   ├── Icon/                       图标资源与 Iconfont.ttf
│   ├── Themes/                     Dark / Bright / Controls / Generic
│   ├── ViewModels/                 三个页面的 ViewModel，以及两页共用的编辑面板状态
│   └── Views/                      三个页面、两页共用的浮层编辑面板、列表入场动效
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

安装位置由安装包自己保证一致，用户没有地方可以改：

- 正常安装序列里的"安装文件夹"对话框已移除。MSI 里 `FolderForm` 与 `VSDCA_FolderForm_AllUsers` 都不存在，也没有任何 `ControlEvent` 会写 `TARGETDIR`。
- 升级时 `AppSearch`（序列 100）先搜注册表 `HKCU\Software\Ksayus\PaperCoin` 的 `InstallDir`，搜到就把 `TARGETDIR` 定在那儿；`DIRCA_TARGETDIR`（序列 750，条件是 `TARGETDIR=""`）只在搜不到时才填默认值。
- 默认值是 `[LocalAppDataFolder]PaperCoin`，即 `%LOCALAPPDATA%\PaperCoin`。本程序是每用户安装（`InstallAllUsers=False`、不提权），所以默认值必须是用户可写的位置——装到 `Program Files` 会因权限失败。

写入这一侧的代码在 `PaperCoin.Service/InstallLocation.cs`，程序每次启动都会把 `AppContext.BaseDirectory` 写进去。**键路径与值名是它和安装包的约定**：安装包那侧写在 `PaperCoin.Setup.vdproj` 的 `"Locator"` 节点里（节点类型 `{CF66B7F7-B7DA-4B08-A67A-233430638C9C}`，`Root` 用 VS 自己的枚举值——`3:1` 是 HKLM、`3:2` 是 HKCU）。改一处必须同步改另一处。

> VS 安装项目的这个格式没有公开 schema。上面那些节点 GUID 与字段名是从 `Microsoft.VisualStudio.InstallerProjects` 扩展的 `dpplg.dll` 里读出来的（每个 GUID 后面就跟着它的显示名），并用"构建后查 MSI 的 `RegLocator` / `AppSearch` 表"验证过。

数据目录跟着程序目录走（`Environment.CurrentDirectory\PaperCoin`），所以安装位置一变，账单就会"看不见"。这也是必须锁住路径的原因。注意默认路径下它是 `%LOCALAPPDATA%\PaperCoin\PaperCoin\Bills`，多了一层同名目录——这是数据目录命名与安装目录重名的老问题，与本次改动无关。

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

运行时数据放在**进程当前工作目录**下的 `PaperCoin/` 目录里：

| 路径 | 内容 |
|------|------|
| `Config/papercoin-settings.json` | 应用配置（主题、自动启动、自动更新） |
| `Logs/papercoin-<日期>.log` | 按天滚动的日志，保留 7 份 |
| `Bills/<年>/<月>/<日>/bills.json` | 当天全部流水。新增或编辑按记录自身的日期定位，与那一天的已有记录合并，不覆盖 |

目录结构本身就是索引：查询直接枚举 `Bills/` 下的年 / 月 / 日目录，没有独立的索引文件。手工增删目录同样能被正确识别。

从 Visual Studio 或 `dotnet run` 启动时，工作目录是输出目录 `PaperCoin/bin/<配置>/net10.0-windows`，数据也落在那里。也可以在设置页点“数据目录”直接打开账单文件夹。

## 测试

```powershell
cd tools/UiSmokeTest
dotnet run
```

14 项检查，覆盖主题资源键对齐、图标码位是否存在于字体、侧边栏指示器随窗口尺寸重算、三个页面的布局与交互、保存链路的串行化与增量同步、动效是否真的向 LAE 注册等。

这里不引入测试框架：需要验证的是真实布局、真实绑定和真实交互，测试框架帮不上忙。程序直接构造 View、量它的尺寸、驱动它的命令与事件，把编译期看不出来的问题（缺少的 `StaticResource`、失效的 `RelativeSource`、模板名字域解析失败、布局塌陷、绑定路径写错）逼出来。

## 许可

`LivelyAnimationEngine/` 是独立的第三方动画引擎，MIT 许可，版权与条款见其目录下的 `LICENSE.txt`。本仓库其余部分未声明开源许可。
