# 项目记忆 · 当前状态

> 本文件是本工程的**唯一事实来源**。每轮工作开始先读它，结束前更新它。
> 最后更新：2026-11-14 15:42

---

## 一、铁律（不可违反）

1. **禁止本地编译**。不运行 `dotnet build` / `dotnet publish` / `msbuild`。
   编译只在 GitHub Actions 上做。
2. **禁止使用 WSL**，除非 Windows 原生确实做不到（当前一切都可以用 Windows 完成）。
3. **空间红线 100 GB**：项目文件 + WSL 里创建的文件总和不得超过 100 GB。
   当前占用极小（约 0.4 MB），无需申请扩容。
4. **推送后必须检查 CI 结果**；失败则修复再推，直到绿。
5. 工作流必须**自动上传日志与产物到仓库**（artifact + dist 分支）。

---

## 二、仓库

| 项 | 值 |
|---|---|
| 仓库 | https://github.com/FengFaQ/MEIZU_tools |
| 协议 | SSH, key `C:\Users\Administrator\.ssh\162636`，口令 `162636` |
| SSH 通路 | `ssh.github.com:443`（22 端口不通），已写在 `~/.ssh/config` |
| 本地工作目录 | `C:\Users\Administrator\Documents\dsh\meiyou-toolbox` |
| 默认分支 | `main` |
| 状态 | 已初始化，待首次推送 |

### 推送命令（非交互，实测可用）

```powershell
$askDir="$env:TEMP\gkiauth"
New-Item -ItemType Directory -Force -Path $askDir | Out-Null
Set-Content "$askDir\askpass.cmd" '@echo 162636' -Encoding Ascii
$env:SSH_ASKPASS="$askDir\askpass.cmd"
$env:SSH_ASKPASS_REQUIRE='force'
$env:DISPLAY='dsh'
$env:Path="$askDir;$env:Path"
cd C:\Users\Administrator\Documents\dsh\meiyou-toolbox
git config core.sshCommand "ssh -F C:/Users/Administrator/.ssh/config"
git push origin main
```

### 网络限制（重要）

- **直连 `github.com` / `api.github.com` / `nuget.org` 均不通**（curl 返回 `http_code=000`）。
- **只有 SSH 经 `ssh.github.com:443` 可用**。
- 因此：**无法从本机查询 CI 结果**（API 不可达）。CI 结果只能由用户在网页上确认。
- 替代方案：让 workflow 把产物推到 `dist` 分支，用户可以 `git fetch` 取回。

---

## 三、技术栈（已确认版本）

| 用途 | 包 | 版本 |
|---|---|---|
| UI | Avalonia | **12.1.6**（注意：不是 11.x） |
| 组件库 | TKCode.Arc | 1.1.0 |
| 组件库 | TKCode.Arc.Blocks | 1.1.0（Sidebar 在此包） |
| 组件库 | TKCode.Arc.Fonts | 1.1.0 |
| 组件库 | TKCode.Arc.Icons.Lucide | 1.1.0 |
| MVVM | CommunityToolkit.Mvvm | 8.4.0 |
| DI | Microsoft.Extensions.DependencyInjection | 10.0.0 |
| TFM | **net10.0** | |

### TKCode.Arc 关键 API（已从源码核实）

```csharp
// Program.cs
AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseArcApp()        // 自定义扩展，见 AppSetup.cs
    .LogToTrace();

// AppSetup.cs
builder.WithInterFont()
       .WithArcFonts()
       .UseArc(arc => arc
           .UseArcBlocks()    // 含 Sidebar，且内部已注册 UseArcControls()
           .WithTheme(t => t.Appearance(ArcAppearance.System)
                            .Accent(ArcAccentPreset.Neutral)));
```

**要点**：
- Arc 在运行时注册样式与主题（`ArcRuntime.cs` 里 `Resources.MergedDictionaries.Add`），
  **不需要**在 `App.axaml` 里 `StyleInclude` FluentTheme。
- XAML 命名空间：`xmlns:arc="https://github.com/AIDotNet/TKCode.Arc"`、
  `xmlns:lucide="https://github.com/AIDotNet/TKCode.Arc/lucide"`
- 图标用法：`<lucide:LucideIcon Kind="PanelLeft" Size="16" />`
- `ArcWindow` 派生自 Window，自带标题栏；`Sidebar` 有 `IsCollapsed` / `ToggleCollapsed()`。

---

## 四、目录体系

```
C:\Users\Administrator\Documents\dsh\
├── meiyou-toolbox\                 ← 本项目（git 仓库）
│   ├── MEMORY.md                   ← 本文件（状态与铁律）
│   ├── PITFALLS.md                 ← 踩坑与要点清单
│   ├── README.md
│   ├── .github/workflows/build.yml
│   ├── docs/reference/             ← 参考实现分析（00~04）
│   └── src/MeiyouToolbox/          ← 应用源码
└── _ref_meiyou\                    ← 参考实现解压（不进 git，约 118MB）
    ├── bin\                        ← 煤油工具箱原始程序
    └── _utf8\                      ← GBK→UTF-8 转码副本（供阅读）
```

---

## 五、进度

### 已完成
- [x] 参考实现完整分析（4 份文档，约 4300 行，含 04 核心机制详解）
- [x] 仓库连通性验证（SSH over 443）
- [x] TKCode.Arc API 调研（Avalonia 12 / net10.0 / UseArcApp）
- [x] 项目骨架：csproj、Program、AppSetup、App、DI 注册
- [x] 服务层：ProcessRunner / ToolLocator / DeviceService / HostInfoService / ShellLauncher
- [x] ViewModel：Main / Overview / Settings
- [x] 视图：MainWindow / MainView / OverviewView / PlaceholderView
- [x] 样式：AppStyles.axaml / Colors.axaml
- [x] CI workflow（build + dist 分支上传）

### 待办（按优先级）
1. **首次推送并确认 CI 通过**（最高优先，未通过前不写新功能）
2. 概览页细节打磨（设备信息真实填充、任务进度接入）
3. 刷机页：解锁/上锁 BL、Root、刷 Recovery、9008 线刷
4. 备份页：全分区备份、任意分区回读、QCN
5. 管理页：分区表、槽位
6. 设置页：外观、工具路径、备份目录

---

## 六、界面规范（用户明确要求，不可改）

```
┌─────────────────────────────────────────────────────┐
│ [侧栏按钮] 文件 更新        [—] [□] [×]             │ 顶栏 44px
├────────┬────────────────────────────────────────────┤
│ 概览    │                                            │
│ 刷机    │         主窗口（页面内容）                  │
│ 备份    │                                            │
│ 管理    │                                            │
│  ····  │  ← 中间留白，与下方岔开                     │
│ 设置    │  ← 固定最下方                               │
└────────┴────────────────────────────────────────────┘
```

- 侧栏宽 192px，默认展开，按钮切换
- 侧栏 5 项：概览/刷机/备份/管理 在上，**设置在最下方**

### 概览页坐标系（用户定义，务必遵守）

原点 = 主窗口中心（两对角线交点）
**X 轴从右向左为正**，**Y 轴从下向上为正**。

| 象限 | 实际位置 | 内容 |
|---|---|---|
| 第一、四象限（贯穿 X 轴） | **左半通栏**（上下通高） | 手机状态 |
| 第二象限 | **右上** | 电脑状态 + 当前任务 |
| 第三象限 | **右下** | 快捷打开 |

**手机状态**三行结构：
1. 连接状态：已连接 / 未连接
2. 连接途径：Fastboot / Recovery / 9008(EDL) 等（未连接时**不显示**）
3. 14 项信息：品牌、型号、代号、系统、BL 状态、主板 ID、CPU 架构、闪存类型、
   显示信息、开机状态、电池信息、系统架构、编译版本、内核版本
   → 读不到的显示 `---`

**电脑状态**：电量、本软件版本号
**当前任务**：操作名 + 进度百分比

**快捷打开**四按钮：
| 按钮 | 动作 |
|---|---|
| 设备管理 | `C:\Windows\System32\mmc.exe` |
| 终端 | CMD，工作目录 = 工具目录，可直接用 adb |
| 任务管理器 | `C:\Windows\System32\Taskmgr.exe` |
| 目录管理 | 资源管理器打开程序目录 |
