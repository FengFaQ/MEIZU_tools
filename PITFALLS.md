# 踩坑与要点清单

> 只记录**已经踩过或已验证**的坑。每条都要能节省未来的时间。

---

## 零之前：界面层踩坑（2026-11-14 晚，用户实测反馈后修复）

### A. 白字白底 —— 不要自造颜色，全用 Arc Design Token

**症状**：卡片、快捷按钮的文字看不见（鼠标悬停才隐约可见）。
**根因**：我自制了 `AppSurface` / `AppForeground` 画刷，而 TKCode.Arc 在运行时
把 Design Token 注册进 `Application.Resources`。系统处于**暗色模式**时
Arc 令牌给出浅色文字，而我硬编码了白色背景 → 白字白底。
**正确做法**：
1. 外观固定 `ArcAppearance.Light`（`AppSetup.cs` 的 `.WithTheme(...)`）
2. 窗口 `Background="White"`
3. **所有颜色/字号一律用令牌**：
   `Foreground="{arc:ArcDesignTokenResource Foreground}"`
   `Foreground="{arc:ArcDesignTokenResource TextSecondary}"`
   `FontSize="{arc:ArcDesignTokenResource TypeSM}"`
4. **不要**自己定义 SolidColorBrush 画刷资源

### B. 用 `arc:Card` / `arc:Button`，不要手搓 Border + Button

手搓的 Border 没有跟随主题的背景/边框/圆角，视觉也不统一。
`arc:Card` 自带 Title/Description/圆角/边框/阴影，`arc:Button` 有
`Variant = Primary | Secondary | Ghost | Danger | Text`。

**已核实的 token 白名单**（其它名字不存在，写错编译不报错但值取不到）：
`Foreground` `TextSecondary` `TextMuted` `Surface` `SurfaceRaised` `SurfaceMuted`
`Border` `BorderSubtle` `BorderStrong` `TypeXS` `TypeSM` `TypeBase` `TypeLG` `TypeXL`

> 注意：**没有** `SurfaceHover`、**没有** `Type2XL`。我第一版就写错了这两个。

### C. 验证界面不要截桌面

`CopyFromScreen` 会被其它窗口遮挡，反而截到浏览器/QQ，浪费好几轮。
**正确做法**：程序内置截图 —— `RenderTargetBitmap.Render(window)`，
输出到 `screenshots\`。已实现为 `ScreenshotService`。

### D. `CaptureHandler` 绑定时机

`OnDataContextChanged` 时控件还没挂上可视树，`TopLevel.GetTopLevel(this)` 返回 null，
导致截图能力一直是 null、点按钮无反应。
**正确做法**：在 `AttachedToVisualTree` 之后再绑定，且调用方要带重试。

### E. 窗口尺寸不能等于屏幕尺寸

设成 1280x820 时实际窗口是 1295x817（含边框/DPI 缩放），
**超出 1280x800 屏幕**，右侧窗口按钮和底部「设置」被裁掉。
用 1180x740、最小 960x600。

### F. 启动即崩溃且无窗口 —— 看 stderr，不要猜

`Start-Process -RedirectStandardError` 能直接拿到 .NET 异常堆栈。
我第一轮就是靠它定位到 `KeyNotFoundException: Static resource 'StatusDotConverter' not found`。
根因：转换器只定义在 `MainView.Resources`，而 `OverviewView` 构造时父级资源字典不可用。
**做法**：`StaticResource` 引用的东西一律注册到 `App.axaml` 的 `Application.Resources`；
另外加了 `AppDiagnostics` 把未处理异常写进 `%LOCALAPPDATA%\MeiyouToolbox\logs\crash-*.log`。

### G. 跨线程更新 UI → 原生崩溃 0xe0434352

在 `Task.Run` 的 async 回调里直接改绑定到界面的 `ObservableCollection` 和属性，
表现为窗口一闪 + 原生 `0xe0434352`。
**做法**：
- `await ... .ConfigureAwait(true)` 回到 UI 上下文
- 或在改集合/属性前 `if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(...); return; }`

### H. 属性名遮蔽 BCL —— 又一个实例

`IScreenshotService.Directory` 属性让 `Directory.CreateDirectory(...)`
解析到 `string` 上，报 `CS1929`。
**已被这套写法坑过三种**：`Task`（属性）、`AppVersion`（静态类）、`Directory`（属性）。
**规则**：成员名不要用 `Task`、`Action`、`Timer`、`Path`、`File`、`Directory`、`Console`、`Environment`。

---

## 零、编译期踩坑实录（2026-11-14，从红到绿共 7 轮）

本地没有 .NET SDK，只能靠 GitHub Actions 反馈，每轮约 4 分钟。
以下 7 条都是**实际让 CI 变红**的原因，写功能时避开它们能省大量时间。

### 0.1 属性名 / 类名遮蔽 BCL 类型 —— 最隐蔽的一类

```csharp
// ❌ 报 CS1061: 'BackgroundTaskProgress' does not contain a definition for 'Run'
[ObservableProperty] private BackgroundTaskProgress _task = ...;   // 生成属性 Task
...
_ = Task.Run(...);   // 解析到生成的属性，不是 System.Threading.Tasks.Task
```

- `[ObservableProperty] private Xxx _task` 会生成名为 **`Task`** 的 public 属性，
  它**遮蔽同命名空间/同作用域的 `System.Threading.Tasks.Task` 类型**
- 同类问题：`_appVersion` 生成 `AppVersion` 属性，遮蔽了名为 `AppVersion` 的静态类
- **修法**：属性名不要用 `Task`、`Action`、`Timer` 等 BCL 名；
  必要时写全限定名 `System.Threading.Tasks.Task.Run`
- **规律**：`[ObservableProperty]` 生成的属性名 = 去掉下划线首字母大写，
  **提交前想一遍这个名字会不会撞 BCL**

### 0.2 一个 `.cs` 文件里放两个类 → XAML 找不到类型

```
Unable to find type `MeiyouToolbox.Views.PlaceholderView`
```

`PlaceholderView.axaml.cs` 里同时声明 `PlaceholderView` 和 `PlaceholderViewModel` 时，
XAML 编译器解析不到 `PlaceholderView`。
**修法**：`<Xxx>.axaml.cs` 里**只放** `Xxx` 这一个类，其他类另开文件。

### 0.3 XAML 实例化 VM 必须有公共无参构造函数

```
AVLN3000: Unable to find public constructor for type PlaceholderViewModel()
AVLN3000: Unable to find suitable setter or adder for property Title
```

在 XAML 里写 `<vm:PlaceholderViewModel Title=".." />` 要求：
1. **公共无参构造函数**
2. 属性**可写**（`[ObservableProperty]` 生成的就是可写的 `set`）

只读属性 + 有参构造 = 16 个错误。
**修法**：需要 XAML 直接实例化的 VM，一律用无参构造 + `[ObservableProperty]`。

### 0.4 移动类之后，XAML 的 `using:` 命名空间要同步改

VM 从 `Views` 移到 `ViewModels` 后，`PlaceholderView.axaml` 里
`xmlns:vm="using:MeiyouToolbox.Views"` 没跟着改 → `Unable to resolve type`。
**修法**：改命名空间时全局搜一遍 `using:MeiyouToolbox.`

### 0.5 接口成员必须显式实现为公开属性

```csharp
// 接口里：string AppDirectory { get; }
// 实现里只在构造函数赋值，没写属性声明 → CS0535
public ToolLocator() { AppDirectory = ...; }   // ❌
public string AppDirectory { get; }            // ✅ 必须显式声明
```

### 0.6 `GetRequiredService` 需要 `using Microsoft.Extensions.DependencyInjection`

否则报 `CS1061: 'IServiceProvider' does not contain a definition for 'GetRequiredService'`。
它是**扩展方法**，在 `Microsoft.Extensions.DependencyInjection` 命名空间下。

### 0.7 Lucide 图标绑定 → ARCL002 警告 + 运行时不显示

```
warning ARCL002: LucideIcon.Kind is bound here, so the generator cannot tell which icons it shows
```

源生成器只处理 **XAML 中的字面量 Kind**。用绑定/数据传 Kind 时：
- 编译只给警告（不红），但**图标静默不显示**，只在 Avalonia 日志留一条
- **修法**：改用字面量分支渲染 + 在 csproj 里显式声明：
  ```xml
  <LucideIcon Include="Activity;FolderOpen;HardDriveDownload;..." />
  ```
- 也可 `<LucideIconsIncludeAll>true</LucideIconsIncludeAll>`（体积变大）

**这套库的图标名有 2141 个枚举成员，写错名字也是静默失败** —— 已把权威清单存到
`C:\Users\Administrator\Documents\dsh\MEIZU_tools\reference\_lucide_check\LucideIconKind.g.cs`，用前先校验。

### 0.8 其余两条

- **csproj 里的通配符 `<None Include="Tools/**" />`** 在目录不存在时可能引入空项，
  已移除；若确需分发工具，改为按具体路径列出
- **`.slnx` / 解决方案文件不是必需的**：直接 `dotnet build <csproj>` 即可

---

## 一、环境与网络

### 1. 本机直连 GitHub / NuGet 全部不通
- 现象：`curl.exe https://api.github.com` → `http_code=000`；`Invoke-RestMethod` → "基础连接已经关闭"
- 可用路径：**SSH 经 `ssh.github.com:443`**
- 结论：**无法从本机查 CI 结果**，只能靠 workflow 把产物推到分支后 `git fetch` 取回

### 2. Windows 下 `git ls-remote` 起初报 cygwin 错
- 现象：`sh.exe: *** fatal error - couldn't create signal pipe, Win32 error 5`
- 原因：沙箱限制，不是认证问题
- 解决：给完全权限后**正常**。认证成功的标志是 `Hi FengFaQ!`

### 3. 非交互 SSH 认证写法（实测可用）
```powershell
$askDir="$env:TEMP\gkiauth"
New-Item -ItemType Directory -Force -Path $askDir | Out-Null
Set-Content "$askDir\askpass.cmd" '@echo 162636' -Encoding Ascii
$env:SSH_ASKPASS="$askDir\askpass.cmd"
$env:SSH_ASKPASS_REQUIRE='force'
$env:DISPLAY='dsh'
$env:Path="$askDir;$env:Path"
git config core.sshCommand "ssh -F C:/Users/Administrator/.ssh/config"
```

### 4. `~/.ssh/config` 里有一条历史无效条目
`IdentityFile C:/xiancheng/.ssh/id_rsa` 不存在，会打印告警但**无碍**，实际用 `~/.ssh/162636`。

---

## 二、TKCode.Arc

### 5. 它要求 **Avalonia 12**，不是 11
- `build/Versions.props`：`AvaloniaVersion=12.1.5`，范围 `[12.1.5, 13.0.0)`
- NuGet 上 12.1.6 已发布，用 12.1.6 安全
- **若误用 Avalonia 11，编译会大面积报 API 不存在**

### 6. 它要求 **.NET 10 或 11**，模板默认 `net10.0`
- 仓库 `global.json` 锁 `11.0.100-rc2`，但**发布包本身 targets net8.0 / net10.0 / net11.0**
- 用 `net10.0` 最稳

### 7. **不要**在 App.axaml 里 StyleInclude FluentTheme
Arc 在运行时自己注册样式与令牌（`ArcRuntime.cs` 的 `Resources.MergedDictionaries.Add`）。
手工引入 FluentTheme 会与 Arc 主题冲突。

### 8. `UseArcBlocks()` 已包含 `UseArcControls()`
官方模板注释明确写了。所以只需要 `UseArcBlocks()`。

### 9. `Sidebar` 在 `TKCode.Arc.Blocks` 包里
只用 `TKCode.Arc` 是找不到 `Sidebar` 的，必须加 `TKCode.Arc.Blocks` 包引用。

### 10. `UseOptimizedPublish=false` 必须加
官方模板注释：".NET 11 SDK 的优化发布模式会丢弃 Avalonia 编译后的 XAML"，
表现为运行时 "No precompiled XAML found"。

---

## 三、Avalonia 绑定

### 11. 父级 DataContext 会被子控件覆盖 → IsVisible 绑定失效
**错误写法**：
```xml
<Grid>
  <views:OverviewView DataContext="{Binding Overview}" IsVisible="{Binding IsOverview}" />
</Grid>
```
`IsVisible` 绑定解析时 DataContext 已变成 `Overview`，找不到 `IsOverview`。

**正确写法**：把可见性放在**外层容器**：
```xml
<Border IsVisible="{Binding IsOverview}">
  <views:OverviewView DataContext="{Binding Overview}" />
</Border>
```

### 12. RadioButton 双向绑定 + Command 会打架
用 `IsChecked="{Binding IsXxx, Mode=OneWay}"`，点击由 `Command` 改变 VM 状态，
避免 `RadioButton` 自己改绑定源导致状态错乱。

### 13. `AvaloniaUseCompiledBindingsByDefault=true` 时必须声明 `x:DataType`
否则编译期绑定报错。每个 View 都要写 `x:DataType="vm:XxxViewModel"`。

---

## 四、CMD / 参考实现的历史坑（供借鉴，不在本工程复现）

### 14. `goto` 到不存在的标签会**直接终止批处理**
原工具箱 `煤油工具箱.bat:L887` 写 `goto UNLOCKBL-ODM2`，而标签是 `:UNLOCKBL-ODM-2`
（少一个连字符）→ 用户读 SN 失败时脚本静默崩溃。
**结论**：新工程用方法调用，不用 goto。

### 15. `if ... & goto :eof` 的短路陷阱
原 `slot.bat:L56` 等三处的 `if not "%func%"=="set" ENDLOCAL & ... & goto :eof`，
`goto :eof` **恒执行**，导致切槽位功能整体失效。
**结论**：多语句分支必须用括号包起来。

### 16. 变量名不一致导致初始化失效
原 `write.bat:L108` 写 `set n=0`，但 `L114` 用的是 `set /a num+=1` —— 名字不同。
**结论**：变量命名要统一，最好由编译器/分析器兜底（这正是换语言的理由之一）。

---

## 五、参考实现的关键技术点（写刷机功能时要复用）

### 17. 9008 通信四层
```
QSaharaServer.exe -p \\.\COM<n> -s 13:<programmer>   ← 推引导
qcedlcmdhelper.exe -p COM<n> -m sig -d <授权码>       ← 出示签名
fh_loader.exe ... --configure=<xml>                   ← 初始化存储
fh_loader.exe ... --sendxml=<xml> [--convertprogram2read]  ← 读/写
```
需要授权时先 `fh_loader.exe --nop` 探测（`Authenticate failed` 即需要签名）。

### 18. 读写共用同一套 XML，靠 flag 区分
- 写：`--sendxml=a.xml`
- 读：`--sendxml=a.xml --convertprogram2read`
XML 结构都是 `<program filename=.. physical_partition_number=.. start_sector=.. num_partition_sectors=.. SECTOR_SIZE_IN_BYTES=.. sparse=.. />`

### 19. configure 自适应重试（最值得复用的一段）
解析 `port_trace.txt` 的报错，改 XML 参数后重试（最多 5 次）：
| 报错 | 修正 |
|---|---|
| `Hash table 8192 larger than supported 0` | 删 `MaxDigestTableSizeInBytes` |
| `Tool doesn't come from ZTE.` | 加 `Oem="ZTE"` |
| `payload to target size is too large` | 用设备回报的 `MaxPayloadSizeToTargetSupported` 替换 |

### 20. 扇区大小 / GPT 常量
| 存储 | 扇区 | GPT 主 | GPT 备 |
|---|---|---|---|
| emmc | 512 | 34 | 33 |
| ufs | 4096 | 6 | 5 |
备份 XML 里 `start_sector="NUM_DISK_SECTORS-33."`（末尾点号是语法一部分）

### 21. 刷完必须设 `setbootablestoragedrive`
- **emmc / spinor → value=0**
- **ufs → 按当前槽位：aonly 或 a → 1，b → 2**
- 判据在 `bin\mzbox.bat:275-305`：先 `slot qcedl chk` 取 `slot__cur` 再决定
- **漏了会"刷成功但不开机"**
- ⚠️ 早先误记为"ufs→1、emmc→2"，已在 `FlashService.SetBootableStorageDriveAsync` 修正

### 22. persist 分区绝不能写回
`persist` 存 TEE 密钥、指纹/传感器校准。覆盖后**指纹永久失效且不可修复**。
备份包里保留 `persist.img` 文件，但写回前必须把 `rawprogram*.xml` 里
`filename="persist.img"` 清空。

### 23. 解锁码写入 private 的偏移（auth-private 方案）
```
0x11000 (69632)  len 4   "LOCK"
0x11004 (69636)  len 256 解锁码
0x11104 (69892)  len 16  "factory"
0x11114 (69908)  len 16  版本标记 mTest3.10.7.0 或 rde5.4.5.0(18s/18sPro)
```
另有一组 `0x14000` 的相同写入被原实现**注释掉了** —— 可能是部分机型解锁失败的原因。

### 24. Fastboot 解锁码分段下发
512 字符解锁码切成 **16 段 × 32 字符**，逐条 `fastboot oem unlock:<32字符>`。

### 25. QCN 必须走 DIAG 而非 EDL
`QCNTool.exe -r -p <端口> -f <目录> -n <文件名>`（读）
`QCNTool.exe -w -p <端口> -f <完整路径>`（写）
注意**读写参数格式不一致**。原始输出含 IMEI，新工程应默认脱敏。

### 26. QCN 工具成功判定字符串
- 读：`Reading QCN from phone... OK`
- 写：`Writing Data File to phone... OK`

### 27. `gpttool` 的 `-o` 只对 `convert:` 生效
`print:` 系列**一律写 stdout**，加 `-o` 会被忽略（不会报错，只是产物不见）。
要落盘必须自己重定向 stdout。
另外 `print:qcedl_rawprogramxml` 直接吐出完整 `<program .../>`，
`start_sector="NUM_DISK_SECTORS-33."`、`start_byte_hex="(512*NUM_DISK_SECTORS)-16896."`
都已算好，只要自己填 `physical_partition_number`（输出里是空串）。

### 28. GPT 原始数据不能直接解析
9008 回读的 512KB 原始扇区，必须先
`gpttool -p raw.bin -f convert:gptmain -o gpt_main<N>.bin`
转换后才能喂给 `print:default:#insl:sector:10`。
512KB 常量：emmc `SECTOR_SIZE_IN_BYTES="512" num_partition_sectors="1024"`；
ufs / spinor `4096` / `128`。`start_sector="0"`、`label="PrimaryGPT"`。

### 29. `[GeneratedRegex]` 不能带实现体
`private static partial Regex Foo() => ...` **编译失败**。
只能是 `private static partial Regex Foo();`，逻辑要放到非 partial 方法里分发。

### 30. `[RelayCommand]` 方法只能返回 `Task`，不能返回 `Task<bool>`
源生成器不支持值返回。需要返回值的逻辑抽成普通 `private async Task<T>`，
命令方法只 `=> await XxxAsync()` 转发。

### 31. `AppDiagnostics` 只有 `Install()` 和 `Write()`
没有 `Report`。异常写 `AppDiagnostics.Write(message, ex)`，
落在 `%LOCALAPPDATA%\MeiyouToolbox\logs\crash-*.log`。

### 32. 没有 `AppServicesHost` 这个类
要拿服务用 `App.Services.GetService<T>()`。

### 33. `AttachedToVisualTree` 的事件参数类型是 `VisualTreeAttachmentEventArgs`
写成 `VisualTreeEventArgs` 编译不过。

### 34. 工程没引 `Avalonia.Controls.DataGrid`
表格只能用「手写表头 Grid + ListBox + ItemTemplate」实现。

### 35. `x:DataType` 不能写 `x:String`
要写 `xmlns:sys="using:System"` 然后 `x:DataType="sys:String"`。

### 36. ⚠️ 绝不用 PowerShell 读写含中文的源码文件
PowerShell 5.1 的 `Get-Content` 默认按 **GBK(ANSI)** 解码 UTF-8 文件，
再 `Set-Content -Encoding utf8` 写回 → **中文全部双重编码损坏**
（`清理` → `娓呯悊`，`槽位` → `妲戒綅`，`分区` → `鍒嗗尯`）。
**这种损坏不会产生 U+FFFD**，只查替换字符的检测完全查不出来。

检测要按典型乱码字符：
```powershell
$text = [System.IO.File]::ReadAllText($f, [System.Text.Encoding]::UTF8)
$bad  = $text.Contains('娓') -or $text.Contains('妲') -or $text.Contains('鍒') -or $text.Contains('鎿')
```
**规则：一律用 read / edit / write 工具改源码，不经过 PowerShell。**

### 37. `git push` 前必须重建 SSH askpass
`ssh_askpass: exec(...\askpass.cmd): No such file or directory` 说明脚本被删了。
`C:\Users\Administrator\Documents\dsh\MEIZU_tools\askpass.cmd` 内容 `@echo 162636`
（ASCII、无 BOM、无换行）。推送前设：
```powershell
$env:SSH_ASKPASS = "...\askpass.cmd"; $env:SSH_ASKPASS_REQUIRE = "force"; $env:DISPLAY = "dsh"
```

### 38. 应用图标要设两条通路，缺一不可
| 通路 | 设置位置 | 管什么 |
|---|---|---|
| `<ApplicationIcon>Assets\app.ico</ApplicationIcon>` | csproj `PropertyGroup` | exe 在资源管理器 / 任务栏 |
| `Icon="/Assets/app.png"` + `<AvaloniaResource Include="Assets\app.png" />` | xaml `Window` + csproj `ItemGroup` | 窗口左上角 |

只设第一条 → **窗口图标仍是空白方块**。只设第二条 → exe 图标是默认的。
`.ico` 用于 exe（多尺寸内嵌，系统按 DPI 自动选档）；`Window.Icon` 用 `.png`，
Avalonia 读 PNG 最稳，不必让它去解 ICO 容器。

### 39. 生成 ICO 用 Pillow 原生写入器，别手写
手写「PNG-only ICO 容器」在本机验证能读，但 MSBuild 嵌入 PE 资源时兼容性没保证。
用 `Image.save(format="ICO", sizes=[...])`：Pillow 会自己决定每档用 BMP(DIB) 还是 PNG 条目
（小尺寸 BMP、256 PNG），这是 Windows 各版本兼容性最好的组合。

### 40. 素材 PNG 裁剪要看 alpha 阈值，别信 `getbbox()` 的原始结果
`alpha.getbbox()` 返回 `(0,143,917,868)` 时左边界是 0，看起来内容贴边了 ——
实际是几个 alpha 值 1~8 的**不可见杂散像素**。
先 `alpha.point(lambda v: 255 if v > 8 else 0)` 再 `getbbox()`，
真实边界是 `(114,144,911,867)`，四边留白才均匀。
另外裁完要**补成正方形**（内容居中、四周留 6% 呼吸空间），否则图标在方形槽位里会变形或偏心。
