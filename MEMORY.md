# 项目记忆 · 当前状态

> 本文件是本工程的**唯一事实来源**。每轮工作开始先读它，结束前更新它。
> 最后更新：2026-11-14 16:42

---

## 零、当前状态（一眼看）

| 项 | 状态 |
|---|---|
| **CI** | ✅ 通过（最新 run 43576650048） |
| 产物 | ✅ 48.6 MB，`MeiyouToolbox.exe` 可运行 |
| 启动 | ✅ 正常，无崩溃 |
| 配色 | ✅ 已修复（白底黑字） |
| 内置截图 | ✅ 可用，输出到 `screenshots\` |
| 实时日志 | ✅ 可用，输出到 `logs\` |
| 第一轮需求 | ✅ 三区域界面 + 概览页三部分 + 四快捷按钮 |
| 下一步 | 刷机 / 备份 / 管理 三页的功能实现 |

---

## 一、铁律（不可违反）

1. **禁止本地编译**。不运行 `dotnet build` / `dotnet publish` / `msbuild`。
   编译只在 GitHub Actions 上做。
2. **禁止使用 WSL**，除非 Windows 原生确实做不到。
   （当前仅在需要读 TKCode.Arc 源码时用过；源码已提取关键 API 到 PITFALLS.md）
3. **空间红线 100 GB**。当前约 3 MB。
4. **推送后必须检查 CI 结果**；失败则修复再推，直到绿。
5. 工作流必须**自动上传日志与产物到仓库**。

---

## 二、仓库与工具

| 项 | 值 |
|---|---|
| 仓库 | https://github.com/FengFaQ/MEIZU_tools |
| 本地目录 | `C:\Users\Administrator\Documents\dsh\meiyou-toolbox` |
| 分支 | `main`（源码）/ `dist`（CI 产物，自动推送） |
| SSH 密钥 | `C:\Users\Administrator\.ssh\162636`，口令 `162636` |
| SSH 通路 | `ssh.github.com:443` |
| **gh CLI** | `C:\Users\Administrator\AppData\Local\Microsoft\WinGet\Links\gh.exe`（已认证） |

### 推送模板

```powershell
$askDir="$env:TEMP\gkiauth"
New-Item -ItemType Directory -Force -Path $askDir | Out-Null
Set-Content "$askDir\askpass.cmd" '@echo 162636' -Encoding Ascii
$env:SSH_ASKPASS="$askDir\askpass.cmd"; $env:SSH_ASKPASS_REQUIRE='force'; $env:DISPLAY='dsh'
$env:Path="$askDir;$env:Path"
cd C:\Users\Administrator\Documents\dsh\meiyou-toolbox
git config core.sshCommand "ssh -F C:/Users/Administrator/.ssh/config"
git add -A; git commit -m "..."; git push origin main
```

### 查 CI / 取产物 / 本地验证（完整闭环）

```powershell
$gh = "C:\Users\Administrator\AppData\Local\Microsoft\WinGet\Links\gh.exe"

# 1) 看最新 run
& $gh run list --repo FengFaQ/MEIZU_tools --limit 2 --json databaseId,status,conclusion

# 2) 失败时只看错误
& $gh run view <RUNID> --repo FengFaQ/MEIZU_tools --log-failed

# 3) 成功后取产物
& $gh run download <RUNID> --repo FengFaQ/MEIZU_tools --name MeiyouToolbox-win-x64 --dir <目录>

# 4) 本地运行 + 读程序自己生成的日志/截图
Start-Process <目录>\MeiyouToolbox.exe -WorkingDirectory <目录>
Get-Content <目录>\logs\app-*.log -Raw
Get-ChildItem <目录>\screenshots
```

> **重要**：验证界面不要用"截屏桌面"——会被其它窗口遮挡、还会截到浏览器。
> 用程序内置截图（`screenshots\window-*.png`），一定准确。

---

## 三、技术栈（已确认版本）

| 用途 | 包 | 版本 |
|---|---|---|
| UI | Avalonia | **12.1.6**（不是 11.x） |
| 组件库 | TKCode.Arc | 1.1.0 |
| 组件库 | TKCode.Arc.Blocks | 1.1.0（Sidebar 在此包） |
| 组件库 | TKCode.Arc.Fonts | 1.1.0 |
| 组件库 | TKCode.Arc.Icons.Lucide | 1.1.0 |
| MVVM | CommunityToolkit.Mvvm | 8.4.0 |
| DI | Microsoft.Extensions.DependencyInjection | 10.0.0 |
| TFM | **net10.0** | |

### 界面写法（务必遵守）

```xml
<!-- 命名空间 -->
xmlns:arc="https://github.com/AIDotNet/TKCode.Arc"
xmlns:lucide="https://github.com/AIDotNet/TKCode.Arc/lucide"

<!-- 卡片：用 Arc 官方 Card，不要自己画 Border -->
<arc:Card Title="标题" Description="说明"> ...内容... </arc:Card>

<!-- 颜色/字号：一律用 Arc Design Token -->
Foreground="{arc:ArcDesignTokenResource Foreground}"
Foreground="{arc:ArcDesignTokenResource TextSecondary}"
FontSize="{arc:ArcDesignTokenResource TypeSM}"     <!-- XS SM Base LG XL -->
Background="{arc:ArcDesignTokenResource Surface}"
Background="{arc:ArcDesignTokenResource SurfaceMuted}"
BorderBrush="{arc:ArcDesignTokenResource BorderSubtle}"

<!-- 按钮 -->
<arc:Button Variant="Text" Content="文件" />          <!-- Primary Secondary Ghost Danger Text -->
<arc:Button Variant="Secondary" Command="{Binding XxxCommand}"> ... </arc:Button>

<!-- 图标（Kind 必须是字面量，且要在 csproj 声明） -->
<lucide:LucideIcon Kind="PanelLeft" Size="16" />
```

**已验证可用的 token**：`Foreground` `TextSecondary` `TextMuted` `Surface` `SurfaceRaised`
`SurfaceMuted` `Border` `BorderSubtle` `BorderStrong` `TypeXS` `TypeSM` `TypeBase` `TypeLG` `TypeXL`

---

## 四、目录结构

```
C:\Users\Administrator\Documents\dsh\
├── meiyou-toolbox\                 ← 本项目（git 仓库）
│   ├── MEMORY.md                   ← 本文件
│   ├── PITFALLS.md                 ← 踩坑清单（写功能前必读）
│   ├── README.md
│   ├── .github/workflows/build.yml
│   ├── docs/reference/             ← 参考实现分析 00~05
│   └── src/MeiyouToolbox/
│       ├── App.axaml(.cs)          ← 应用 + DI + 全局异常兜底
│       ├── AppSetup.cs             ← UseArcApp：字体/Arc/主题
│       ├── Program.cs
│       ├── app.manifest            ← DPI 感知
│       ├── Models/                 ← DeviceInfo / BackgroundTaskProgress
│       ├── Services/               ← ProcessRunner ToolLocator DeviceService
│       │                             HostInfoService ShellLauncher AppLogger
│       │                             ScreenshotService AppServices(DI)
│       ├── ViewModels/             ← Main / Overview / Settings / Placeholder
│       ├── Views/                  ← MainWindow MainView OverviewView
│       │                             SettingsView PlaceholderView
│       └── Converters/             ← StatusDotConverter NavVariantConverter
├── _ref_meiyou\                    ← 参考实现解压（不进 git，118 MB）
└── _lucide_check\                  ← Lucide 图标权威清单（2155 个）
```

### 发布后的程序目录结构（用户要求：简洁）

```
<程序目录>\
├── MeiyouToolbox.exe
├── <依赖 DLL 若干>
├── logs\          运行日志 app-yyyyMMdd.log（自动创建）
└── screenshots\   内置截图 window-*.png（自动创建）
```

> 预留：`tools\` 放 adb/fastboot；`assets\` 放图标资源。尚未实现。

---

## 五、界面规范（用户明确要求，不可改）

```
┌─────────────────────────────────────────────────────┐
│ [侧栏] 文件 更新 截图      [—] [□] [×]              │ 顶栏 44px
├────────┬────────────────────────────────────────────┤
│ 概览    │  手机状态（第一、四象限，左半通栏）          │
│ 刷机    │                                            │
│ 备份    │                          ┌───────────────┐ │
│ 管理    │                          │ 电脑状态+任务  │ │ 第二象限
│  ····  │                          └───────────────┘ │
│ 设置    │                          ┌───────────────┐ │
└────────┴──────────────────────────│ 快捷打开       │─┘ 第三象限
                                     └───────────────┘
```

- 侧栏宽 192px，默认展开，顶栏按钮切换
- 侧栏 5 项：概览/刷机/备份/管理 在上，**设置固定在最下方**
- 外观固定 **Light**（`ArcAppearance.Light`），窗口 `Background="White"`

### 概览页三个部分

**① 手机状态**（左半通栏）
- 第一行：已连接 / 未连接
- 第二行：连接途径（Fastboot / Recovery / 9008(EDL)…），未连接时**不显示**
- 14 项信息：品牌、型号、代号、系统、BL 状态、主板 ID、CPU 架构、闪存类型、
  显示信息、开机状态、电池信息、系统架构、编译版本、内核版本 → 读不到显示 `---`

**② 电脑状态 + 当前任务**（右上）
- 电量、软件版本、当前任务名 + 进度百分比

**③ 快捷打开**（右下，四按钮）
| 按钮 | 动作 |
|---|---|
| 设备管理 | `C:\Windows\System32\mmc.exe` |
| 终端 | CMD，工作目录 = 工具目录，PATH 已注入 |
| 任务管理器 | `C:\Windows\System32\Taskmgr.exe` |
| 目录管理 | 资源管理器打开程序目录 |

---

## 六、进度

### 已完成
- [x] 参考实现完整分析（5 份文档，约 5800 行）
- [x] 项目骨架 + CI（build / artifact / ci-report / dist 分支）
- [x] 三区域界面与 5 页导航
- [x] 概览页三部分（真实设备探测 adb/fastboot/9008）
- [x] 四个快捷按钮
- [x] 设置页（目录一览 + 打开目录）
- [x] 内置截图 + 实时日志
- [x] 配色修复、启动崩溃修复、窗口尺寸修复

### 待办
1. **刷机页**：解锁/上锁 BL、Magisk Root、刷 Recovery、9008 线刷
2. **备份页**：全分区备份、任意分区回读、QCN
3. **管理页**：分区表、槽位
4. `tools\` 目录与 adb/fastboot 自动准备
5. 主题切换（Light/Dark）、侧栏折叠动画
