# 魅友工具箱 (MEIZU_tools)

基于 **Avalonia + TKCode.Arc + CommunityToolkit.Mvvm** 重写的魅族手机刷机工具箱，
逻辑参考经典实现《煤油工具箱 1.2.1-beta9》，但换成现代 UI 与更健壮的服务层。

> 编译在 GitHub Actions 上完成，本地不编译。产物见 Actions 的 Artifacts，
> 或 `dist` 分支。

---

## 技术栈

| 用途 | 选型 |
|---|---|
| UI 框架 | Avalonia 12.1.6 |
| 组件库 | TKCode.Arc 1.1.0（含 Blocks / Fonts / Icons.Lucide） |
| MVVM | CommunityToolkit.Mvvm 8.4.0（源生成 `[ObservableProperty]` / `[RelayCommand]`） |
| 依赖注入 | Microsoft.Extensions.DependencyInjection 10.0.0 |
| 目标框架 | `net10.0` |

---

## 界面结构

```
┌─────────────────────────────────────────────────────────┐
│ [侧栏按钮] 文件  更新            [—] [□] [×]            │ 顶栏
├──────────┬──────────────────────────────────────────────┤
│ 概览      │                                              │
│ 刷机      │              主窗口（页面内容）                │
│ 备份      │                                              │
│ 管理      │                                              │
│          │                                              │
│ ┄┄┄┄┄┄┄ │                                              │
│ 设置      │                                              │
└──────────┴──────────────────────────────────────────────┘
```

- 顶栏左侧是侧栏展开按钮与「文件 / 更新」，右侧是最小化 / 最大化 / 关闭
- 侧栏 5 项：概览、刷机、备份、管理 在上，**设置固定在最下方**并与上方拉开距离
- 侧栏默认展开，点击顶栏按钮切换

### 概览页坐标系

以主窗口中心为原点、X 轴向右、Y 轴向上的平面直角坐标系：

| 象限 | 位置 | 内容 |
|---|---|---|
| 第一、四象限（贯穿 X 轴） | 左半通栏 | 手机状态 |
| 第二象限 | 右上 | 电脑状态 + 当前任务 |
| 第三象限 | 右下 | 快捷打开 |

**手机状态**：连接状态（已连接/未连接）→ 连接途径（Fastboot / Recovery / 9008(EDL)…，未连接时不显示）
→ 14 项设备信息（品牌、型号、代号、系统、BL 状态、主板 ID、CPU 架构、闪存类型、显示信息、
开机状态、电池信息、系统架构、编译版本、内核版本）。读不到的项统一显示 `---`。

**电脑状态**：电量、软件版本。**当前任务**：操作名 + 进度百分比。

**快捷打开**：设备管理（`mmc.exe`）、终端（在工具目录打开 CMD，可直接用 adb/fastboot）、
任务管理器（`Taskmgr.exe`）、目录管理（资源管理器定位到程序目录）。

---

## 目录结构

```
src/MeiyouToolbox/
├── Program.cs              入口
├── AppSetup.cs             Avalonia + Arc 装配（字体、主题）
├── App.axaml(.cs)          应用与 DI 容器
├── Models/                 DeviceInfo、TaskProgress
├── Services/
│   ├── ProcessRunner.cs    外部命令执行（带超时与取消）
│   ├── ToolLocator.cs      定位 adb / fastboot
│   ├── DeviceService.cs    设备探测（adb / fastboot / 9008）
│   ├── HostInfoService.cs  电脑电量与版本
│   ├── ShellLauncher.cs    快捷打开
│   └── AppServices.cs      DI 注册
├── ViewModels/             Main / Overview / Settings
├── Views/                  MainWindow / MainView / OverviewView / PlaceholderView
└── Styles/                 调色板与控件样式
```

---

## 相对参考实现的改进

参考实现（煤油工具箱）的若干实证缺陷，本项目在设计上规避：

| 原实现问题 | 本项目做法 |
|---|---|
| `goto` 到不存在标签直接杀死进程 | 服务层方法调用，异常有兜底 |
| `portstatus` 写死导致保护失效 | 探测结果显式建模为 `DeviceInfo`，不再靠全局变量 |
| 全脚本不加引号，路径不能含空格 | 用 `ProcessStartInfo.ArgumentList`，天然安全 |
| 无超时，命令卡死则整工具卡死 | `ProcessRunner` 统一超时 + 取消 |
| 型号不匹配仅警告仍可刷入 | 后续刷机功能将前置机型指纹校验 |
| QCN 原始输出含 IMEI 且默认入日志 | 后续实现默认脱敏 |

---

## 开发

```bash
# 本地仅做静态检查（不编译）
dotnet restore src/MeiyouToolbox/MeiyouToolbox.csproj
```

推送即触发 CI 编译与产物上传。
