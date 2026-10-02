# PaperCoin

Windows 桌面记账程序。WPF + .NET 10，单机运行，账单以按日期分层的 JSON 文件保存，不依赖数据库和网络。

主窗口是一个 420×560 的无边框窗口：左侧导航栏，右侧内容区，外观支持暗色 / 亮色 / 跟随系统。

## 功能

### 今日账单

- 记录当天流水：描述、金额、分类、日期。金额约定正数为收入、负数为支出。
- 列表按录入顺序展示，卡片带悬浮抬升、按下压缩、单击编辑。
- 新增与编辑走浮在列表之上的面板，不占布局行，列表不会因面板开合而重排。
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

- 主题：跟随系统 / 暗色 / 亮色，切换立即生效并写入配置。
- 开机自动启动：直接写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
- 自动更新：目前只保存开关与更新源，检查版本与下载逻辑尚未接入。
- 打开数据目录、打开 GitHub Releases 页面、恢复默认设置。

## 项目结构

| 项目 | 目标框架 | 职责 |
|------|----------|------|
| `PaperCoin` | `net10.0-windows` | WPF 界面层：视图、ViewModel、主题渲染、自绘控件与转换器 |
| `PaperCoin.Core` | `net10.0` | 领域模型与路径常量，不依赖 WPF |
| `PaperCoin.Service` | `net10.0` | 配置读写、主题状态、账单持久化 |
| `tools/UiSmokeTest` | `net10.0-windows` | 手工驱动的 UI 冒烟测试，不在解决方案内 |

```
PaperCoin/
├── PaperCoin.slnx                  解决方案（LAE / Core / Service / 界面）
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
├── tools/UiSmokeTest/              UI 冒烟测试
└── LivelyAnimationEngine/          LAE 动画引擎（未纳入版本管理，见下文）
```

主要依赖：CommunityToolkit.Mvvm 8.4、Microsoft.Extensions.Configuration 与 DependencyInjection 10.0、Serilog 4.4。动画统一交给 LAE（Lively Animation Engine），以项目引用的方式使用。

## 构建与运行

需要 .NET 10 SDK 和 Windows。

```powershell
dotnet build PaperCoin.slnx
dotnet run --project PaperCoin/PaperCoin.csproj
```

### 关于 LAE

`LivelyAnimationEngine/` 被 `.gitignore` 排除，入库的只有引用它的项目文件。克隆之后需要先把 LAE 放到仓库根目录的同一层级：

```
<仓库根>/
├── LivelyAnimationEngine/
│   └── LAE/LivelyAnimationEngine.csproj
├── PaperCoin.slnx
└── PaperCoin/
```

否则构建会报找不到 `..\LivelyAnimationEngine\LAE\LivelyAnimationEngine.csproj`。若暂时不需要动效相关的页面，也可以从 `PaperCoin.slnx` 和 `PaperCoin.csproj` 里摘掉对它的引用。

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
