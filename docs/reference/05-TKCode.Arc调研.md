# 05 · TKCode.Arc 组件库 API 调研

> 调研对象：`AIDotNet/TKCode.Arc` v1.1.0（Avalonia 12 组件库，MIT）
> 源码：`/tmp/arc_src`（WSL，只读）
> 调研方式：逐文件阅读源码 + 比对 NuGet 已发布包（`api.nuget.org` 的 nuspec 与 nupkg 实体内容）
> **重要前提**：调研环境（WSL）没有 .NET SDK（`dotnet: command not found`），因此本文所有代码**未经实际编译验证**。凡是靠源码直接读出的签名，可信度高；凡是"能编译通过"的推断，均在文中标注来源依据。

---

## 0. 五分钟结论（先看这个）

| 问题 | 答案 |
|---|---|
| 最终 App 该引用哪个包？ | **`TKCode.Arc`**（这一个就够，它带 `Core` + `Foundation` + StreamMark）。`TKCode.Arc.Core` / `.Foundation` 是**底层依赖**，不要手动引用 |
| 要不要写 `App.axaml` 引 StyleInclude？ | **不需要**。Arc 的**全部主题注册都在 C# 的 `UseArc(...)` 里**，模板注释原话：`Arc needs no App.axaml` |
| 起步正确姿势 | `AppBuilder.Configure<App>().UsePlatformDetect().UseArc(arc => arc.UseArcControls()...)`，然后 `App : Application`（**不需要 partial，不需要 App.axaml**） |
| 目标框架 | `net8.0` / `net10.0` / `net11.0`（`Terminal` 只有 `net10.0;net11.0`） |
| Avalonia 版本 | `12.1.5`，依赖区间 `[12.1.5, 13.0.0)` |
| 所有包都发布了吗？ | **是，全部 23 个包都有 1.1.0 正式版**（含 Terminal / Pdf.Native / Video.Native / Audio.Native） |
| 预置"主题"有哪些 | 没有 Fluent/Simple 那种主题名。只有：**外观**（Light/Dark/System）+ **8 种强调色** + **2 个可选样式包**（Pixel / Corners） |
| 控件数量 | 541 个公开控件类（`TKCode.Arc` 191、`Blocks` 152、`Special` 132、`Mobile` 26、`Core` 21、`AdvancedCharts` 8、`Dock` 2、`Workflow` 4、`Video` 3、`Terminal` 1、`Lucide` 1） |
| 命名空间 | **扁平单命名空间 `TKCode.Arc`**，XAML 前缀 `xmlns:arc="https://github.com/AIDotNet/TKCode.Arc"` |

**最容易踩的坑（本文档存在的最大价值）**：

1. **不要**在 `App.axaml` 里写 `StyleInclude` / `ResourceInclude` 去引 Arc 的主题 —— Arc 根本没有可供 `<StyleInclude>` 引用的主题字典文件。想"按 Avalonia 教程那样引样式"必然失败。
2. **不要**忘了在 `UseArc(...)` 里调 `UseArcControls()` —— 漏了就是"控件全裸奔 / 空白"，这是官方 Troubleshooting 第一条。
3. **必须**写 `arc:` 前缀。`Button`、`Slider`、`TreeView`、`ContextMenu`、`DatePicker`、`TimePicker`、`Calendar`、`Carousel`、`SplitButton`、`TabItem`、`TreeViewItem` 这些名字与 Avalonia 自带控件**同名**，不写前缀会解析到 Avalonia 的原生控件。
4. **Lucide 图标是按需编译的**：`<lucide:LucideIcon Kind="Bell" />` 写在 XAML 里没问题；但 `Kind` 来自**绑定或数据**时，必须在 csproj 声明 `<LucideIcon Include="Bell" />`，否则**画不出来**（只在 Avalonia 日志里 warning）。

---

## A. 起步方式

### A1. 根 README 与文档怎么说

来源：`/tmp/arc_src/README.md`

- 库定位："The visual language and spring motion of Arc, as native Avalonia controls"，移植 Arc 的 112 个免费组件。
- 包表（原文）：

| Package | Contents |
|---|---|
| `TKCode.Arc` | 112 个组件，22 个分组，外加 `Markdown`、`PropertyGrid`、`Cascader`、`TransferBox` |
| `TKCode.Arc.Blocks` | 页面级组合（AI chat、pricing、billing、dashboard、settings、team…） |
| `TKCode.Arc.AdvancedCharts` | `DataGrid` + `Sunburst`/`SankeyFlow`/`FunnelChart`/`RadarChart` 等 |
| `TKCode.Arc.Special` | 动画 AI 讲解器、指针特效、loader、surface、插画 |
| `TKCode.Arc.Mobile` | 触摸优先控件（44px 触摸目标），**尚未在真机验证** |
| `TKCode.Arc.Workflow` | 工作流画布 |
| `TKCode.Arc.Dock` | IDE 式 dock 布局 |
| `TKCode.Arc.Terminal` | `TerminalView`，基于 libghostty-vt，**native 库随包** |
| `TKCode.Arc.Pdf` / `.Pdf.Native` / `.Pdf.Browser` | `PdfViewer`，桌面 PDFium / WASM pdf.js |
| `TKCode.Arc.Audio` / `.Audio.Native` / `.Audio.Browser` | `IAudioPlayer`，桌面 miniaudio |
| `TKCode.Arc.Video` / `.Video.Native` / `.Video.Browser` | `VideoPlayer` / `VideoSurface` |
| `TKCode.Arc.Themes.Pixel` | 像素风主题（只有 token 覆盖，无控件） |
| `TKCode.Arc.Themes.Corners` | 全局圆角风格（方形 / 缩放） |
| `TKCode.Arc.Icons.Lucide` | 全量 Lucide；source generator 只编译用到的图标 |
| `TKCode.Arc.Fonts` | Geist / Geist Mono，可选像素字体 |
| `TKCode.Arc.Foundation` | token / 主题 / 语言 / 包注册运行时，**只依赖 Avalonia** |
| `TKCode.Arc.Core` | ArcDesign tokens、调色板、运动引擎、共享 primitives、内置 Lucide glyph |
| `TKCode.Arc.Templates` | `dotnet new` 模板 |

- 兼容性（原文）：`.NET net8.0 / net10.0 / net11.0`，`TKCode.Arc.Terminal` 为 `net10.0;net11.0`；Avalonia 12，版本锁在 `build/Versions.props`。

> 注：仓库根目录**没有 `docs/` 目录**（已确认 `ls docs` → no docs dir）。文档实际发布在 `https://docs.tkcode.si/`，仓库里只有 `README.md` / `README.zh-CN.md` / `AGENTS.md` / `CHANGELOG.md` / `templates/TKCode.Arc.Templates/README.md`。

### A2. App 模板的真实结构

来源：`/tmp/arc_src/templates/TKCode.Arc.Templates/README.md`、`content/_shared/`

模板不是"一个 csproj + App.axaml"，而是：

```
MyApp/
  MyApp.csproj        TKCode.Arc, TKCode.Arc.Fonts, TKCode.Arc.Icons.Lucide,
                      Avalonia.Desktop, Avalonia.Fonts.Inter
  Program.cs          桌面入口点
  AppSetup.cs         UseArcApp()：字体 + UseArc(...) + 启动主题
  App.cs              创建主窗口（桌面）或主视图（浏览器）；Arc needs no App.axaml
  Views/              MainWindow 与 MainView
  Navigation/         AppPages（页面清单）与 PageHost（显示当前页）
  Models/, Pages/     示例数据与页面
  ViewModels/         --mvvm toolkit 时
  Services/           --di 时
  Settings/           --settings 时
  Localization/       --i18n 时
```

**关键事实**：`Views/MainView.axaml` 才是内容，`Views/MainWindow.axaml` 只是桌面窗口外壳。这是为了 xplat（浏览器只显示 MainView）。

### A3. 模板文件完整内容（原样抄录）

> 以下内容来自模板源文件，其中的 `ArcStarterApp` / `ArcDisplayName` / `ArcWindowWidth` 等是模板占位符，`dotnet new` 时被替换。手工建项目时直接改成你自己的名字。

#### `Program.cs`

来源：`templates/TKCode.Arc.Templates/content/ArcStarterApp/Program.cs`

```csharp
using Avalonia;

namespace ArcStarterApp;

internal static class Program
{
    // Avalonia is not ready until Main runs: do not use any Avalonia or Arc API before BuildAvaloniaApp().
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the XAML previewer of your IDE. UseArcApp() (AppSetup.cs) sets up the fonts, Arc and the theme.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .UseArcApp()
                  .LogToTrace();
}
```

#### `AppSetup.cs`（**模板专属文件，不在 NuGet 库里**）

来源：`templates/TKCode.Arc.Templates/content/_shared/App/Common/AppSetup.cs`

```csharp
using Avalonia;
using TKCode.Arc;
using TKCode.Arc.Fonts;

namespace ArcStarterApp;

public static class AppSetup
{
    /// <summary>Fonts, Arc's packages and the theme of the first frame.</summary>
    public static AppBuilder UseArcApp(this AppBuilder builder) =>
        builder.WithInterFont()     // Inter for body text (Avalonia.Fonts.Inter)
               .WithArcFonts()      // Geist for display text, Geist Mono for code (TKCode.Arc.Fonts)
               .UseArc(arc => arc
                   .UseArcControls() // Arc's controls, themes and design tokens
                   // 设置首帧主题：跟随系统明暗 + Neutral 强调色
                   .WithTheme(t => t.Appearance(ArcAppearance.System).Accent(ArcAccentPreset.Neutral)));
}
```

> `UseArcApp()` 是模板自己在 `AppSetup.cs` 里定义的扩展方法，**不属于 TKCode.Arc 库**。手写项目时不要照抄调用它，要么把这段搬进自己的项目，要么直接在 `Program.cs` 里展开。

#### `App.cs`

来源：`templates/TKCode.Arc.Templates/content/_shared/App/App.cs`

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ArcStarterApp.Views;

namespace ArcStarterApp;

/// <summary>
/// The application. Arc needs nothing here: no base class, no App.axaml and no styles. UseArcApp() (AppSetup.cs)
/// registers the themes, and Arc's base theme styles the window and the Avalonia controls Arc's templates use.
/// </summary>
public class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        switch (ApplicationLifetime)
        {
            // A desktop window.
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow();
                break;
            // A browser page or a phone screen: the content without a window.
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView();
                break;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
```

（上面是 `--mvvm none --settings false` 分支的展开结果；启用 settings/mvvm/di/i18n 时会多出 `AppSettings.Current.TrackTheme()`、`CreateMainViewModel()`、`Services` 等。）

#### `App.axaml` —— **Arc 没有这个文件**

来源：`templates/TKCode.Arc.Templates/content/_shared/App/App.cs` 的 XML 注释原文

> *"The application. Arc needs nothing here: no base class, no App.axaml and no styles."*

`templates/` 全目录 `.axaml` 文件清单中**不存在任何 `<Application>` 根元素的文件**（已验证：`grep -rln "<Application" --include=*.axaml templates/` 无结果）。

#### `Views/MainWindow.axaml`（`--custom-titlebar` 打开时，用 `ArcWindow`）

来源：`templates/TKCode.Arc.Templates/content/_shared/App/Common/Views/MainWindow.axaml`

```xml
<arc:ArcWindow xmlns="https://github.com/avaloniaui"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               xmlns:arc="https://github.com/AIDotNet/TKCode.Arc"
               xmlns:lucide="https://github.com/AIDotNet/TKCode.Arc/lucide"
               xmlns:views="using:ArcStarterApp.Views"
               x:Class="ArcStarterApp.Views.MainWindow"
               Title="ArcDisplayName"
               Width="ArcWindowWidth"
               Height="ArcWindowHeight"
               MinWidth="ArcWindowMinWidth"
               MinHeight="ArcWindowMinHeight"
               WindowStartupLocation="CenterScreen">
    <!-- Arc's title bar replaces the system one: the app's icon and title, the window buttons (on macOS the system's
         traffic lights) and three areas for your own controls: TitleBar.LeftContent, CenterContent and RightContent.
         Its empty part moves the window; a double click maximizes it. -->
    <arc:ArcWindow.TitleBar>
        <arc:TitleBar>
            <arc:TitleBar.Icon>
                <lucide:LucideIcon Kind="Sparkles" Size="14" VerticalAlignment="Center" />
            </arc:TitleBar.Icon>
        </arc:TitleBar>
    </arc:ArcWindow.TitleBar>
    <views:MainView />
</arc:ArcWindow>
```

#### `Views/MainWindow.axaml`（默认，用原生 `Window`）

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="using:ArcStarterApp.Views"
        x:Class="ArcStarterApp.Views.MainWindow"
        Title="ArcDisplayName"
        Width="ArcWindowWidth"
        Height="ArcWindowHeight"
        MinWidth="ArcWindowMinWidth"
        MinHeight="ArcWindowMinHeight"
        WindowStartupLocation="CenterScreen">
    <views:MainView />
</Window>
```

#### `Views/MainWindow.axaml.cs`

来源：`templates/TKCode.Arc.Templates/content/_shared/App/Common/Views/MainWindow.axaml.cs`

```csharp
using Avalonia.Controls;
using TKCode.Arc;

namespace ArcStarterApp.Views;

/// <summary>The desktop window around <see cref="MainView"/>.</summary>
public partial class MainWindow : ArcWindow   // 或 : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```

#### `Views/MainView.axaml`（Blank 布局，完整可用示例）

来源：`templates/TKCode.Arc.Templates/content/_shared/App/Layouts/Blank/Views/MainView.axaml`

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:arc="https://github.com/AIDotNet/TKCode.Arc"
             xmlns:lucide="https://github.com/AIDotNet/TKCode.Arc/lucide"
             x:Class="ArcStarterApp.Views.MainView">
    <DockPanel>
        <!-- The top bar: the app name and the light/dark switch. -->
        <Border DockPanel.Dock="Top"
                Height="56"
                Padding="20,0"
                BorderThickness="0,0,0,1"
                BorderBrush="{arc:ArcDesignTokenResource BorderSubtle}">
            <Grid ColumnDefinitions="Auto,*,Auto,Auto" ColumnSpacing="10">
                <Border Width="28"
                        Height="28"
                        CornerRadius="8"
                        VerticalAlignment="Center"
                        Background="{arc:ArcDesignTokenResource Foreground}">
                    <lucide:LucideIcon Kind="Sparkles"
                                       Size="14"
                                       HorizontalAlignment="Center"
                                       VerticalAlignment="Center"
                                       Foreground="{arc:ArcDesignTokenResource Background}" />
                </Border>
                <TextBlock Grid.Column="1"
                           Text="ArcDisplayName"
                           VerticalAlignment="Center"
                           FontWeight="Medium"
                           Foreground="{arc:ArcDesignTokenResource Foreground}" />
                <!-- Applies the other appearance through ArcThemeManager and plays the reveal transition. -->
                <arc:ThemeSwitcher Grid.Column="3" IsIconOnly="True" />
            </Grid>
        </Border>

        <!-- The page. Replace it with your own content. -->
        <ScrollViewer HorizontalScrollBarVisibility="Disabled">
            <arc:Card MaxWidth="560" Margin="24,48" VerticalAlignment="Top">
                <StackPanel Spacing="16">
                    <StackPanel Spacing="8">
                        <TextBlock FontSize="{arc:ArcDesignTokenResource TypeLG}"
                                   FontWeight="Medium"
                                   Foreground="{arc:ArcDesignTokenResource Foreground}">
                            <Run Text="Welcome to" /><Run Text=" ArcDisplayName" />
                        </TextBlock>
                        <TextBlock Text="Replace this text with your own."
                                   TextWrapping="Wrap"
                                   FontSize="{arc:ArcDesignTokenResource TypeSM}"
                                   Foreground="{arc:ArcDesignTokenResource TextSecondary}" />
                    </StackPanel>
                    <arc:Input Label="Your name"
                               Placeholder="Ada Lovelace"
                               Description="Shown in the greeting." />
                    <arc:Switch Content="Product updates" IsChecked="True" />
                    <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right">
                        <arc:Button Content="Read the docs" Variant="Secondary" />
                        <arc:Button Content="Get started" />
                    </StackPanel>
                </StackPanel>
            </arc:Card>
        </ScrollViewer>
    </DockPanel>
</UserControl>
```

### A4. `App.axaml` 里到底要不要引 StyleInclude / ResourceInclude？

**答案：不要，一个都不要。** 这是本次调研最重要的结论，理由有三条，全部有源码依据：

1. **Arc 的主题字典不是文件，是 C# 注册的。**
   `ArcRuntime.Install()` 把三样东西塞进 `application.Resources.MergedDictionaries`：
   ```csharp
   application.Resources.MergedDictionaries.Add(theme.Resources);      // token 字典
   application.Resources.MergedDictionaries.Add(themes);               // 控件主题（惰性加载）
   application.Resources.MergedDictionaries.Add(languages.Resources);  // 语言字典
   ```
   来源：`src/TKCode.Arc.Foundation/Registration/ArcRuntime.cs`（`Install` 方法）
   → 也就是说，**资源在 `UseArc()` 里已经并进去了**，你的 `App.axaml` 再 merge 一次是重复且找不到入口的。

2. **Arc 没有 `<StyleInclude Source="avares://.../Arc.axaml"/>` 这种聚合文件，而且是刻意的。**
   `AGENTS.md` 明令：*"Never create `*Themes.axaml` aggregates."*
   主题文件都是 `xxx/Themes/XxxTheme.axaml`，且 `x:ClassModifier="internal"`，由 Foundation 生成器在首次用到该控件类型时才实例化（lazy）。它们**不是**给 App 手工 include 的。

3. **Avalonia 内置控件的主题由 `ArcBaseTheme` 自动补。**
   `src/TKCode.Arc.Core/Theme/Base/ArcBaseTheme.cs`：`UseArcCore()` 的 commit 回调里调 `ArcBaseTheme.TryRegister(application)`，为 `ScrollViewer`、`ScrollBar`、`ItemsControl`、`PathIcon`、`TextSelectionHandle`、`OverlayPopupHost`、`PopupRoot`、`EmbeddableControlRoot`、`Window` 提供 Arc 风格默认主题。
   **它会"让位"**：如果你在 `App.axaml` 里加了 `FluentTheme` 或 `SimpleTheme`，对应类型就不注册（测试 `Yields_To_FluentTheme` / `Yields_To_SimpleTheme` 验证了这点，来源 `tests/TKCode.Arc.BaseTheme.Tests/ArcBaseThemeTests.cs`）。

**结论性建议**：

- 想要 **Arc 原生外观**（滚动条是 Arc 的细胶囊、Window 有 Arc 背景/字体/overlay 层）→ **不要写 App.axaml，或写一个空的**。
- 已有项目里已有 `FluentTheme` → 保留它也能跑，Arc 会自动让位，代价是滚动条等内置控件长得不像 Arc。这种情况**不需要**额外加任何 StyleInclude。

**为什么"样式引入错就整个界面空白"这个担心在这里不成立**：Arc 的样式不走 `StyleInclude` 这条路。真正会导致"空白"的是漏调 `UseArcControls()`（官方 Troubleshooting 第 1 条：*"Arc controls appear unstyled or empty → `UseArcControls()` is not called in `UseArc(...)`"*），以及所有控件都包在一个没有 `VisualLayerManager` 的模板里（overlay 类控件失效）。

---

## B. 主题与样式

### B1. 有哪些"预置主题"

**没有 Fluent/Simple/FluentDark 那种"主题名"。** Arc 的主题模型是四个正交维度（来源：`src/TKCode.Arc.Foundation/Theme/ArcThemeState.cs`）：

| 维度 | 类型 | 取值 | 说明 |
|---|---|---|---|
| 外观 | `ArcAppearance` | `Light` / `Dark` / `System` | `System` 跟随操作系统，并监听变化实时切换 |
| 强调色 | `ArcAccentPreset` | `Neutral` / `Violet` / `Blue` / `Green` / `Amber` / `Orange` / `Coral` / `Rose` | 也可以传任意 `Color` seed，OKLCH 派生 |
| 运动级别 | `ArcMotionLevel` | `Off` / `Reduced` / `Full` | 命名空间 `TKCode.Arc.Motion` |
| 预设（preset） | `ArcThemePreset` | `ArcPixelPresets.Pixel`、`ArcCornerPresets.Square` / `.Half` / `Create(style)` | 整包 token 覆盖，按 slot 互斥 |

对应的"额外样式包"只有两个（都是**可选 NuGet 包**，只覆盖 token，不含控件）：

- `TKCode.Arc.Themes.Pixel`（`UseArcPixelTheme()`）→ 像素风：方角、硬阴影、16 色板、像素字体、2px 描边
- `TKCode.Arc.Themes.Corners`（`UseArcCorners()`）→ 只改圆角：`ArcCornerStyle.Default` / `.Square` / `.Scale(factor)`

源码：`src/TKCode.Arc.Themes.Pixel/ArcPixelPresets.cs`、`src/TKCode.Arc.Themes.Corners/ArcCornerPresets.cs`

### B2. 亮/暗切换 与 ThemeVariant

**有 `ThemeVariant` 支持，但你不是直接操作它。** `ArcThemeManager.Commit()` 内部会同步设置：

```csharp
// src/TKCode.Arc.Foundation/Theme/ArcThemeManager.cs
// The variant first: built-in theme dictionaries keyed by variant and ActualThemeVariant readers agree with the
// tokens by the time the resource notification goes out.
_application.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
```

三种切换方式：

**(a) 启动时定首帧主题**

```csharp
.UseArc(arc => arc
    .UseArcControls()
    .WithTheme(t => t.Appearance(ArcAppearance.System).Accent(ArcAccentPreset.Neutral)))
```

**(b) 运行时改（推荐，任意控件里都能调）**

```csharp
using TKCode.Arc;

// 只改外观，强调色等其它设置保持不变
ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));

// 改强调色（用预置）
ArcTheme.Apply(t => t.Accent(ArcAccentPreset.Violet));

// 改强调色（用品牌色，OKLCH 派生明暗两套）
ArcTheme.Apply(t => t.Accent(Color.Parse("#0E7C86")));

// 多个一起改
ArcTheme.Apply(t => t
    .Appearance(ArcAppearance.Light)
    .Accent(ArcAccentPreset.Blue)
    .Token(nameof(ArcDesignTokenKind.Danger), "#D92D20")   // 覆盖单个 token
    .MotionLevel(ArcMotionLevel.Reduced));

// 读当前状态
ArcAppearance now = ArcTheme.State.Appearance;
bool isDark = ArcTheme.Manager.IsDark;
```

**(c) 用现成的 `ThemeSwitcher` 控件（模板就是这么干的）**

```xml
<arc:ThemeSwitcher IsIconOnly="True" Variant="Reveal" />
```

属性：`Appearance`（`ThemeSwitcherAppearance?`，null=跟随 ActualThemeVariant）、`Variant`（`Reveal` / `Eclipse` / `Split` / `Rise`）、`Label`、`Text`（默认 "Switch theme"）、`IsIconOnly`。
事件 `ThemeChangeRequested`：不标 `Handled` 就由它自己调 `ArcThemeManager` 应用；标了 `Handled` 就你自己来（比如先存盘）。
来源：`src/TKCode.Arc/Buttons/ThemeSwitcher/ThemeSwitcher.cs`

**(d) 局部主题作用域（并排预览明暗两版时用）**

```csharp
ArcThemeScope.SetAppearance(element, ArcAppearance.Dark);   // 只固定明暗
ArcThemeScope.SetState(element, someArcThemeState);          // 固定整个 state
```

来源：`src/TKCode.Arc.Foundation/Theme/ArcThemeScope.cs`

### B3. 资源键名 —— 全部 177 个 `ArcDesignTokenKind`

**没有 `ArcAccentBrush` 这种字符串键。** 资源键是**装箱的枚举值** `TKCode.Arc.DesignTokens.ArcDesignTokenKind`，XAML 里通过生成出来的 markup extension 访问：

```xml
Background="{arc:ArcDesignTokenResource Surface}"
Foreground="{arc:ArcDesignTokenResource Foreground}"
FontSize="{arc:ArcDesignTokenResource TypeSM}"
CornerRadius="{arc:ArcDesignTokenResource RadiusPanel}"
```

- 命名空间 `TKCode.Arc.DesignTokens`（源文件 `src/TKCode.Arc.Foundation.Generators/TokenModel.cs`：`TokensNamespace => Namespace + ".DesignTokens"`）
- 已发布包中确认存在（`TKCode.Arc.Core` 的 XML 文档）：`T:TKCode.Arc.DesignTokens.ArcDesignTokenKind`、`ArcDesignTokens`、`ArcDesignTokenResourceExtension`
- 控件自己的 token 用 `{arc:<控件名>TokenResource <TokenName>}`，例如 `{arc:ButtonTokenResource ...}`、`{arc:CardTokenResource ContentPadding}`、`{arc:FieldTextBoxTokenResource SelectionFill}`。已发布 `TKCode.Arc` 包里有 552 个 `TKCode.Arc.DesignTokens.*` 类型（每个控件 3 个：`XxxTokenKind` / `XxxTokens` / `XxxTokenResourceExtension`）。

C# 侧读 token：

```csharp
ArcTokens.Bind(card, Border.BackgroundProperty, ArcDesignTokenKind.Surface);   // 跟随主题变化
Color accent = ArcTokens.GetColor(chart, ArcDesignTokenKind.Accent);           // 读一次
CornerRadius r = ArcTokens.GetCornerRadius(owner, ArcDesignTokenKind.RadiusPanel, new CornerRadius(26));
```

**全局 token 全表**（来源：`src/TKCode.Arc.Core/Theme/ArcDesignToken.cs`，共 177 个）：

**中性色阶（12）**
`Neutral0` `Neutral1` `Neutral2` `Neutral3` `Neutral4` `Neutral5` `Neutral6` `Neutral7` `Neutral8` `Neutral9` `Neutral10` `Neutral11`

**表面 / 文本 / 描边（11）**
`Background` `Surface` `SurfaceRaised` `SurfaceMuted` `Scrim` `Foreground` `TextSecondary` `TextMuted` `Border` `BorderSubtle` `BorderStrong`

**强调色（5）**
`Accent` `AccentStrong` `AccentSubtle` `AccentForeground` `AccentPreset`

**选择类控件（9）**
`ControlOn` `ControlOnSubtle` `ControlGlyph` `ControlTrack` `ControlTrackHover` `ControlThumb` `ControlThumbOn` `ControlFill` `ControlThumbShadow`

**状态与图表系列（10）**
`Success` `Warning` `Danger` `SuccessSubtle` `WarningSubtle` `DangerSubtle` `Series1` `Series2` `Series3` `Series4`

**阴影与焦点（16）**
`ShadowResting` `ShadowRaised` `ShadowFloating` `FocusRing` `FocusRingWidth` `FocusRingOffset` `EdgeThickness` `FocusEdgeThickness` `EdgeThicknessTop` `EdgeThicknessBottom` `EdgeThicknessLeft` `EdgeThicknessRight` `EdgeThicknessRightBottom` `EdgeThicknessNoBottom` `EdgeThicknessNoRight` `EdgeThicknessNoLeft`

**几何（57）**
`RadiusControl`(18) `RadiusPanel`(26) `RadiusSurface`(34) `RadiusPill`(9999)
`ControlSizeSM`(36) `ControlSizeMD`(44) `ControlSizeLG`(50)
`GlyphSizeSM`(16) `GlyphSizeMD`(20) `GlyphSizeLG`(24) `IconStrokeWidth`(1.75)
`Space1`(4) `Space2`(8) `Space3`(12) `Space4`(16) `Space5`(20) `Space6`(24) `Space8`(32) `Space10`(40) `Space12`(48) `Space16`(64) `Space20`(80) `Space24`(96)
`RadiusStep1` `RadiusStep1p25` `RadiusStep1p5` `RadiusStep2` `RadiusStep3` `RadiusStep4` `RadiusStep5` `RadiusStep6` `RadiusStep7` `RadiusStep8` `RadiusStep9` `RadiusStep10` `RadiusStep11` `RadiusStep12` `RadiusStep13` `RadiusStep14` `RadiusStep14p5` `RadiusStep15` `RadiusStep15Bottom` `RadiusStep16` `RadiusStep17` `RadiusStep18` `RadiusStep19` `RadiusStep19Top` `RadiusStep20` `RadiusStep20Top` `RadiusStep21` `RadiusStep22` `RadiusStep24` `RadiusStep25` `RadiusStep25Bottom` `RadiusStep28` `RadiusStep33` `RadiusStep33Bottom`

**排版（18）**
`DisplayFontFamily` `BodyFontFamily` `MonoFontFamily`
`TypeXS`(12) `TypeSM`(14) `TypeBase`(16) `TypeLG`(18) `TypeXL`(22) `Type2XL`(28) `Type3XL`(36) `Type4XL`(52) `Type5XL`(72)
`WeightRegular` `WeightMedium` `TrackingDisplay`(-0.03) `TrackingBody`(-0.01) `LeadingDisplay`(1.1) `LeadingBody`(1.4)

**运动（24）**
`DurationInstant`(120ms) `DurationFast`(160ms) `DurationExit`(180ms) `DurationStandard`(240ms) `DurationConsidered`(480ms) `DurationSpring`(580ms)
`EaseEnter` `EaseExit` `EaseStandard` `EaseInOut`
`SpringSmoothDuration` `SpringSmoothBounce` `SpringSnappyDuration` `SpringSnappyBounce` `SpringMorphDuration` `SpringMorphBounce` `SpringResponsiveStiffness` `SpringResponsiveDamping` `SpringGentleStiffness` `SpringGentleDamping`
`StaggerChar` `StaggerWord` `StaggerLine` `StaggerItem`

**效果与策略（7）**
`BlurSubtle`(2) `BlurSoft`(4) `BlurText`(8) `BackdropBlurDialog`(7) `BackdropBlurDrawer`(4) `MotionMode` `ReduceTransparency`

**内容宽度与媒体（8）**
`ContentMaxWidthSM`(640) `ContentMaxWidthMD`(760) `ContentMaxWidthLG`(880) `ContentMaxWidthXL`(1120)
`MediaSizeSM`(64) `MediaSizeMD`(128) `MediaSizeLG`(192) `MediaSizeXL`(256)

**8 种强调色的实际色值**（来源：`src/TKCode.Arc.Core/Theme/ArcPalette.cs`）

| Preset | Seed | Accent(light) | Accent(dark) |
|---|---|---|---|
| Neutral（默认） | `#353535` | `#353535` | `#BEBEBE` |
| Violet | `#7747FF` | `#7747FF` | `#A78BFF` |
| Blue | `#0562EF` | `#0562EF` | `#6BA3FF` |
| Green | `#0DB879` | `#0DB879` | `#53DCA6` |
| Amber | `#F3AD20` | `#F3AD20` | `#FFCB62` |
| Orange | `#F48120` | `#F48120` | `#FFAD5E` |
| Coral | `#F15F55` | `#F15F55` | `#FF9386` |
| Rose | `#ED4E9D` | `#ED4E9D` | `#FF8BC5` |

### B4. 字体

`src/TKCode.Arc.Core/Theme/ArcFonts.cs`：

```csharp
public static readonly FontFamily Body    = new("fonts:Inter#Inter, $Default");
public static readonly FontFamily Display = new("fonts:Geist#Geist, fonts:Inter#Inter, $Default");
public static readonly FontFamily Mono    = new("fonts:GeistMono#Geist Mono, SF Mono, Menlo, Cascadia Mono, Consolas, DejaVu Sans Mono, Courier New, $Default");
```

对应注册调用：`WithInterFont()`（来自 `Avalonia.Fonts.Inter`）+ `WithArcFonts()`（来自 `TKCode.Arc.Fonts`）。不装就回退系统字体（不会崩）。

---

## C. 控件清单

### C1. 命名空间与 XAML 前缀（先记住这个）

| 程序集 | CLR 命名空间 | XML 命名空间 | 前缀 |
|---|---|---|---|
| `TKCode.Arc.Foundation` | `TKCode.Arc` | `https://github.com/AIDotNet/TKCode.Arc` | `arc` |
| `TKCode.Arc.Core` | `TKCode.Arc`、`TKCode.Arc.DesignTokens`、`TKCode.Arc.Motion`、`TKCode.Arc.Primitives`、`TKCode.Arc.Localization` | 同上 | `arc` |
| `TKCode.Arc` | `TKCode.Arc`、`TKCode.Arc.DesignTokens`、`TKCode.Arc.Localization` | 同上 | `arc` |
| `TKCode.Arc.Blocks` / `.Dock` / `.Workflow` / `.Video` / `.Pdf` / `.Special` / `.AdvancedCharts` | `TKCode.Arc` 等 | 同上 | `arc` |
| `TKCode.Arc.Mobile` | `TKCode.Arc.Mobile`、`TKCode.Arc.Mobile.DesignTokens` | `https://github.com/AIDotNet/TKCode.Arc.Mobile` | `arcm` |
| `TKCode.Arc.Icons.Lucide` | `TKCode.Arc.Icons.Lucide` | `https://github.com/AIDotNet/TKCode.Arc/lucide` | `lucide` |

来源：`src/*/Properties/AssemblyInfo.cs`（各包的 `[assembly: XmlnsDefinition]` / `[assembly: XmlnsPrefix]`）

标准用法：

```xml
xmlns:arc="https://github.com/AIDotNet/TKCode.Arc"
xmlns:lucide="https://github.com/AIDotNet/TKCode.Arc/lucide"
xmlns:arcm="https://github.com/AIDotNet/TKCode.Arc.Mobile"
```

C# 侧注意别名（`Button` 等与 Avalonia 冲突）：

```csharp
using Avalonia.Controls;
using ArcButton = TKCode.Arc.Button;
```

### C2. `TKCode.Arc`（主包，191 个控件，22 组）

> 完整类名逐条来自源码遍历（`public class Xxx : <Avalonia 基类>`），并按目录分组。`(abstract)` 表示抽象基类不可直接实例化。

**Activity（4）**：`ChatComposer` `ChatThread` `CommentThread` `Timeline`

**Avatars（4）**：`Avatar` `AvatarGroup` `Badge` `PresenceStack`

**Buttons（15）**：`ActionButton` `ActionSwap` `Button` `ButtonGroup` `ButtonGroupItem` `ConfirmMorph` `CopyButton` `ExpandingButtonGroup` `ExpandingButtonGroupItem` `FloatingButtonGroup` `FloatingButtonGroupItem` `FloatingButtonGroupSeparator` `IconMorph` `SplitButton` `ThemeSwitcher`

**Cards（4）**：`Card` `EmptyState` `MetricCard` `MetricTicker`

**Charts（15）**：`ActivityHeatmap` `AnimatedCounter` `BarChart` `BrushChart` `ChartBase`(abstract) `DonutChart` `Gauge` `LineChart` `Ridgeline` `SlopeChart` `Sparkline` `SplitBar` `Streamgraph` `Treemap` `WaffleChart`

**Content（4）**：`ArcWindow` `Markdown` `TitleBar` `WindowCaptionButtons`

**Editors（7）**：`AvatarUpload` `FileDropzone` `PropertyGrid` `PropertyGridCategoryItem` `PropertyGridItem` `RichTextEditor` `SignaturePad`

**Expand（9）**：`Accordion` `AccordionItem` `ExpandableCard` `ExpandableClamp`(abstract) `ExpandableContent` `ExpandableText` `ResizablePanel` `ResizablePanels` `ScrollArea`

**Forms（2）**：`Form` `FormItem`

**Gestures（4）**：`HoldToConfirm` `Reactions` `SwipeActions` `SwipeActionsRow`

**Illustrations（5）**：`DataFlow` `Illustration`(abstract) `MultiRegionFailover` `PaymentFlow` `VectorSearch`

**Layout（4）**：`BackToTop` `CyclicPanel` `TilePanel` `WaterfallPanel`

> 另有 `Sticky`（`src/TKCode.Arc/Layout/Sticky/Sticky.cs`）：它**不是控件**，而是 `public static class Sticky`，类似 CSS `position: sticky`，通过附加属性挂在任意 `Control` 上：
>
> | 附加属性 | 类型 | 说明 |
> |---|---|---|
> | `arc:Sticky.IsEnabled` | `bool` | 开启 sticky |
> | `arc:Sticky.Offset` | `double` | 吸顶偏移（对应 `top`） |
> | `arc:Sticky.ZIndex` | `int` | 吸附时的层级 |
> | `arc:Sticky.IsStuck` | `bool` | 只读，当前是否已吸附 |
>
> 事件 `StuckChanged` 携带 `StickyStuckChangedEventArgs`。
> 用法：`<Border arc:Sticky.IsEnabled="True" arc:Sticky.Offset="0" />`

**Media（4）**：`CardStack` `Carousel` `ImageCompare` `PageViewer`

**Menus（9）**：`ContextMenu` `DropdownMenu` `MenuEntry` `MenuEntryLabel` `MenuEntrySeparator` `MenuSurface` `RadialMenu` `RadialMenuItem` `UserMenu`

**Messages（5）**：`Alert` `AnnouncementBar` `CookieConsent` `Toast` `ToastStack`

**Navigation（7）**：`Breadcrumb` `BreadcrumbItem` `FluidTabControl` `FluidTabItem` `Pagination` `TabItem` `Tabs`

**Overlays（13）**：`BottomSheet` `BottomSheetSurface` `Dialog` `DialogSurface` `Drawer` `DrawerSurface` `HoverCard` `HoverCardProfile` `ModalOverlay`(abstract) `ModalSurface`(abstract) `Popover` `PopoverSurface` `Tooltip`

**Pickers（5）**：`Calendar` `ColorPicker` `DatePicker` `DateRangePicker` `TimePicker`

**Progress（6）**：`Countdown` `OnboardingChecklist` `Progress` `Skeleton` `Stepper` `UsageMeter`

**Selects（9）**：`Cascader` `ChipGroup` `Combobox` `MorphSelect` `MultiSelect` `Select` `SelectItem` `TransferBox` `TreeSelect`

**Sliders（4）**：`ElasticSlider` `RangeHistogramSlider` `RulerSlider` `Slider`

**SpecialInputs（9）**：`MentionInput` `MoneyInput` `NumberField` `OnScreenKeyboard` `PhoneInput` `ScrubInput` `ShortcutRecorder` `TagInput` `TokenTextEditor`

**Tables（12）**：`CodeBlock` `FilterBox`(abstract) `FilterMenu` `FilterToolbar` `JsonViewer` `NumberFilterBox` `ReorderableList` `SortableDataTable` `Table` `TextFilterBox` `TreeTable` `TreeView`

**TextEffects（5）**：`InViewTitle` `SlotText` `TextMorph` `TextReveal` `TextShimmer`

**TextFields（12）**：`ExpandingSearch` `Field` `FieldFrame` `FieldIconButton` `FieldMessage` `FieldTextBox`(abstract) `InlineEdit` `Input` `PasswordField` `PasswordStrength` `SearchField` `Textarea`

**Toggles（14）**：`BillingPrice` `BillingToggle` `Checkbox` `EnumSelector` `RadioCard` `RadioCards` `RadioGroup` `RadioGroupItem` `Rating` `SegmentedControl` `Switch` `ToggleGroup` `ToggleGroupItem` `TogglesItem`(abstract)

### C3. 其他包

**`TKCode.Arc.Core`（21，primitives，多用于自研控件）**
`ArcDesign`（已废弃的空 Control，仅为源码兼容） `ArcGlyph` `ArcContentSwap` `ArcImage` `ArcOverlayHost` `ArcOverlayLayer` `ArcPopup` `ArcPopupFrame` `ArcRing` `ArcShrinkRow` `ArcSpinner` `ArcViewportHost` `DecoratedTextBlock` `GlidingIndicator` `ModalHost` `MorphGlyph` `PresenceHost` `RollingDigits` `SharedElementFrame` `SpringSizeDecorator` `StrokeDrawGlyph`

**`TKCode.Arc.Icons.Lucide`（1）**：`LucideIcon`（`ArcGlyph` 子类）

**`TKCode.Arc.AdvancedCharts`（8）**：`DataGrid` `FunnelChart` `RaceBarChart` `RadarChart` `RealtimeStream` `SankeyFlow` `Sunburst` `WindRoseChart`

**`TKCode.Arc.Blocks`（152，页面级组合）**
`AccountHealth` `ActivityFeed` `ActivityTerrain` `AgentApprovals` `AgentBuilder` `AgentGallery` `AgentMemory` `AgentOrchestrator` `AgentRoutines` `AgentRun` `AgentTasks` `AiAnswer` `AiChat` `AiComposer` `AiFeatureLaunch` `AiInlineEdit` `AiInsightCard` `AiSearchBar` `AiSetupWizard` `AiSidePanel` `ApiKeys` `AvailabilityPicker` `BillingOverview` `BlogGrid` `BudgetVariance` `CallInsights` `CancelFlow` `CartDrawer` `CatchUp` `ChangelogFeed` `CheckoutSummary` `ChoroplethExplorer` `CohortRetention` `CommandPalette` `CommandPaletteDialog` `ComparisonTable` `ConnectedScatter` `ContactSection` `CreditTopUp` `CtaSection` `CustomerGalaxy` `DeliveryTracker` `DocsSidebar` `DocumentExtraction` `DomainVerify` `EmptyStates` `EnvVariables` `EvalSuite` `ExplodedView` `FaqSection` `FeatureBento` `FeatureVoting` `FeedbackBoard` `FileUpload` `GanttTimeline` `GoalTracker` `HelpCenter` `HeroSection` `HeroSignup` `HorizonChart` `ImportWizard` `InboxSidebar` `InboxTriage` `Integrations` `InvitePeople` `InvoiceStudio` `JourneyFlow` `KanbanBoard` `KnowledgeBase` `KpiDrilldown` `LayoutMorph` `LeadScoring` `LineReplay` `LiveCheckout` `Login` `LoginCentered` `LoginImmersive` `LoginSplit` `LogoMarquee` `MagicLinkSignIn` `MarketTerminal` `McpServers` `MediaPlayer` `MetricExplorer` `MetricsDashboard` `ModelArena` `ModelMenu` `ModelPicker` `ModelPill` `ModelSummary` `MrrWaterfall` `MultiStepForm` `NewsletterSignup` `NotificationCenter` `OnboardingIllustration` `OnboardingSurvey` `OtpInput` `PageHeader` `PageHeaderTab` `PivotBuilder` `PlanComparison` `PricingCalculator` `ProductDetail` `ProductListing` `ProfilePage` `ProjectBoard` `PromptStudio` `ReleaseReadiness` `ResearchRun` `RevenueExplorer` `RevenueGlobe` `RolesPermissions` `ScrollStory` `SearchResults` `SeatManager` `SeatMap` `SecuritySettings` `SelectionToolbar` `SemanticZoom` `SemanticZoomPanel` `SettingsCommand` `SettingsGroup` `SettingsPage` `SettingsPageNavItem` `SettingsRow` `SettingsSection` `Sidebar` `SignIn` `SignupForm` `SiteFooter` `SiteHeader` `SmallMultiples` `SmartScheduler` `SpendAlert` `SpotlightGrid` `StatsBand` `StudioPerspectives` `SupportConversation` `SupportWidget` `TaskBreakdown` `TeamDirectory` `TeamMembers` `TeamShowcase` `TwoFactorSetup` `UptimeStatus` `UsageBilling` `UsageForecast` `UsagePricing` `WalletCard` `WebhookTester` `Webhooks` `WeekCalendar`

**`TKCode.Arc.Special`（132）**
`ActionMorph` `ActivityRings` `AgentCursor` `AgentHandoff` `AgentLoop` `AgentPlan` `AgentStatus` `AgentTimeline` `AiCommandPalette` `AiCommandPaletteDialog` `AiMascot` `AiSpark` `AiTranslate` `AiWorkflow` `AskYourData` `BlackHole` `BookingPill` `BranchGraph` `BuildingBlocks` `CardToArticle` `ChargeSession` `CodeSuggestion` `ConfirmPopover` `ConnectorMark` `ConnectorSetup` `ContextWindow` `ControlCenter` `CoverFlow` `CubeMascot` `DateReel` `DesignToCode` `DictationButton` `DictationControl` `DnsResolution` `DotGrid` `EmojiPicker` `EnrichColumn` `Explainer`(abstract) `FeedbackActions` `GenerationStats` `GhostMascot` `GhostText` `GlassCard` `GlassMenu` `GlassMenuSeparator` `GlassSegment` `GlassSegmented` `GlassTab` `GlassTabBar` `GravityWell` `HoloCard` `ImageGeneration` `ImageReveal` `ImageTrail` `InkFluid` `InstallSnippet` `KeycapKeyboard` `LightboxGallery` `LinkUnfurl` `LiquidTabBar` `LiquidTabBarItem` `LiveVisitors` `LlmCostCalculator` `MagnifyDock` `MagnifyDockItem` `MeetingSummary` `MegaMenuMorph` `MegaMenuMorphLink` `MixtureOfExperts` `ModelCard` `ModelTraining` `MorphBlock` `MorphLoader` `MorphNav` `NowPlaying` `NowPlayingLevels` `OrbitLogos` `OrbitMenu` `OrbitMenuAction` `PageCurl` `PhotoGrid` `PiiRedaction` `PixelMascot` `PrizeWheel` `ProductGallery` `PromptCache` `ProviderKeys` `ProviderStatus` `ReasoningBlock` `ReasoningTree` `ReferralCard` `ReplyDraft` `ResponseFeedback` `SandText` `ShareAccess` `ShareSheet` `SheetStack` `SheetStackSheet` `SkeletonMorph` `SketchToImage` `SmartFormFill` `SocialProofToast` `SourceCards` `SourceCitation` `SpeculativeDecoding` `SpeechPlayer` `StarterPrompts` `StickerPeel` `StretchRefresh` `StretchRefreshActivityRow` `TeamSwitcher` `TeamSwitcherTile` `TicketRouter` `TicketTear` `TimeDial` `TldrToggle` `TocProgress` `ToneTuner` `ToolCall` `ToolCallGroup` `ToolDock` `ToolDockItem` `TrainingRun` `TrialBanner` `TypewriterTerminal` `UndoToast` `UpgradePrompt` `VoiceControls` `VoiceOrb` `VoiceRecorder` `VoiceTranscript` `WalletStack`

**`TKCode.Arc.Mobile`（26，前缀 `arcm:`）**
`ActionSheet` `ActionSheetItem` `Button` `Checkbox` `Dialog` `IconButton` `ListCell` `ListGroup` `ListItem` `Navbar` `NavigationStack` `Page` `PullToRefresh` `Radio` `RadioCard` `RadioCards` `Searchbar` `Segmented` `SegmentedItem` `Sheet` `Switch` `Tabbar` `TabbarItem` `TextField` `Toast` `Toolbar`

**`TKCode.Arc.Dock`（2）**：`DockLayout` `DockPane`；模型类 `DockGroup` `DockSplit` `DockDocumentArea` `DockWorkspace` `DockFloat`

**`TKCode.Arc.Workflow`（4）**：`WorkflowCanvas` `WorkflowMinimap` `WorkflowNodeCard` `WorkflowZoomControls`

**`TKCode.Arc.Video`（3）**：`VideoPlayer` `VideoSurface` `SimulatedVideoPlayer`

**`TKCode.Arc.Terminal`（1）**：`TerminalView`（命名空间 `TKCode.Terminal.Avalonia`，**注意不是 `TKCode.Arc`**）

### C4. 常用控件的关键属性速查

全部来自源码 `public static readonly StyledProperty<...>` 声明。

**`Button`**（`src/TKCode.Arc/Buttons/Button/Button.cs`）
`Variant`：`ButtonVariant` = `Primary`(默认) / `Secondary` / `Ghost` / `Danger` / `Text`
`Size`：`ControlSize` = `Small` / `Medium`(默认) / `Large`
`IsLoading`（`bool`，加载中吞噬点击但保留键盘焦点）
`IsPressScaleEnabled`（`bool`，默认 true）
`LabelSwapMode`：`ContentSwapMode` = `None` / `Fade` / `BlurSwap`(默认) / `Roll` / `Pop` / `Slide`
`LabelSpring`：`ArcSpring` = `Smooth` / `Snappy` / `Morph`(默认) / `Responsive` / `Gentle`

**`Card`**（`src/TKCode.Arc/Cards/Card/Card.cs`，`ContentControl`）
`Title` `Description` `Media` `Action` `Avatar` `Meta` `Status` `Details` `IsDetailsOpen` `ScrimBrush` `BackdropBlur` `CloseButtonName`
> `Details` 一设，整张卡就变成 quick-look 触发器（点开原地放大）。

**`Input`**（`src/TKCode.Arc/TextFields/Input/Input.cs` : `FieldTextBox`）
继承 Avalonia `TextBox`（`Text` / `PlaceholderText` / `IsReadOnly` 全在），额外：
`Label` `Description` `ErrorMessage` `IsInvalid` `IsLabelVisible` `Size` `Prefix` `Suffix` `Placeholder`（与 `PlaceholderText` 双向同步）

**`Select` / `Combobox` / `MultiSelect`**（`SelectingItemsControl`）
`Label` `Description` `Placeholder` `Size` `IsOpen` `MaxDropDownHeight` `TextSelector` `ValueSelector` `DisabledSelector`
`Combobox` 另有 `EmptyText` `FilterPredicate` `SearchText`；`MultiSelect` 另有 `MaxVisibleChips` `IsSearchable` `SearchPlaceholder` `SelectedValues`
子项用 `arc:SelectItem`（`Content` `Value` `Icon` `Meta` `Keywords` `Group`）。
`SelectedValue` 来自 Avalonia 12 的 `SelectingItemsControl.SelectedValue`（已在 Avalonia 12.1.5 的 XML 文档中确认存在）。

**`Progress`**（`Avalonia.Controls.Primitives.RangeBase`）
`Label` `IsValueVisible` `Percentage`(只读) `IsComplete`(只读)；用 `Value` / `Maximum`（`Minimum` 按 0 处理）。

**`Table`**（`ContentControl`，`[Content]` 是 `Columns`）
`ItemsSource` `SortColumn` `SortKey` `SortDirection` `IsSortable` `IsSelectable` `SelectsOnRowClick` `IsStriped` `SelectedItems` `RowKeySelector` `Caption` `EmptyMessage` `IsLoading` `LoadingMessage` `ItemNameOne` `ItemNameOther` `SelectedNoun` `SelectAllLabel` `ClearSelectionLabel`
列：`DataTableColumn`（`Header` `Key` `SortMemberPath` `IsSortable` `IsNumeric` `Width` `StringFormat` `CellTemplate` `TextWrapping` `MaxLines`），工厂 `DataTableColumn.Create<T>(header, selector, key?)`。

**`Dialog`**（`ModalOverlay`）
`Size`(`ControlSize`) `ShowHeader`；附加属性 `arc:Dialog.Result`（点在按钮上，点击即带结果关闭）
静态方法 `Dialog.ShowAsync(owner, title, content, description, params DialogAction[])` → `Task<object?>`
`Dialog.ConfirmAsync(owner, title, description?, confirmText?, cancelText?, isDanger?)` → `Task<bool>`
`Dialog.BuildActions(IEnumerable<DialogAction>)` → `Control`

**`Drawer`**：`Side`(`DrawerSide`) `IsFullScreen` `ShowHeader` `HeaderContent` `ShowGrabber` `PanelCornerRadius` `PanelHeight` `PanelSize` `PanelMaxHeight`

**`Toast`**（静态 API）
`Toast.Show(owner, title, kind, description?, action?, duration?, id?)` → `string`（返回 id，可复用 id 原地更新）
`Toast.Update(owner, id, update)` / `Toast.Dismiss(owner, id?)`
`ToastKind` = `Success` / `Info`(默认) / `Warning` / `Error` / `Loading`（`Loading` 不超时，等待 `Update` 变成结果）
`ToastAction(string label, Action invoke)` 或 `(string label, Action<string> invoke)`

**`SegmentedControl`**（`SelectingItemsControl`）：`SelectedValue` + `arc:SegmentedOption`（`Value` `Label` `Accessory` `IsEnabled` `IsVisible`），事件 `ItemIntent`

**`Tabs`**（`SelectingItemsControl`）+ `TabItem`（`HeaderedContentControl`：`Header` 是标签文字，`Content` 是面板）：`ActivationMode` `ContentMargin` `ScrollLeftLabel` `ScrollRightLabel` `SelectedContent`

**`Sidebar`**（在 `TKCode.Arc.Blocks`）
`SelectedItem` `SelectedKey` `IsCollapsed` `Header` `Footer` `IsFilterVisible` `FilterText` `FilterPlaceholder` `NoMatchesText` `IsCollapseButtonVisible` `CollapseButtonPlacement` `CollapseButtonText` `ExpandButtonText` `ResizeMode`（`Push` / `Overlay`）`UnreadText` `Label` `SelectCommand` `ExpandedWidth`
子项：`arc:SidebarSection`（`Header` `IsCollapsible` `IsExpanded` `IsVisible`，内容是 `AvaloniaList<SidebarItem>`）、`arc:SidebarItem`（`Key` `Label` `Icon` `TrailingIcon` `Badge` `IsUnread` `IsAction` `IsExpanded` `IsEnabled` `IsVisible` `Command` `CommandParameter` `Tag`）
事件：`ItemInvoked`、`SelectionChanged`

**`DockLayout`**（`TKCode.Arc.Dock`）：`ActivePane` `IsLocked` `PaneResolver` `EmptyDocumentContent` `Workspace`；`SaveLayout()` → `string`、`LoadLayout(string)` → `bool`、事件 `LayoutChanged`
`DockPane`：`Id` `Title` `Icon`(Geometry) `CanClose` `CanFloat` `CanAutoHide` `CanMove` `CanDockAsDocument` `IsModified` `PreferredSide` `PreferredSize`
`DockSplit`：`Orientation`、`Size`；`DockGroup`：`ActivePane`、`Size`；`DockDocumentArea`：`Size`

### C5. 自定义 Window（`ArcWindow`）—— 无边框自定义标题栏

来源：`src/TKCode.Arc/Content/ArcWindow/ArcWindow.cs`、`TitleBar.cs`、`Themes/ArcWindowTheme.axaml`

**刻意不叫 `Window`**（源码注释原文）：*"Named `ArcWindow`, not `Window`: C# looks up types in enclosing namespaces before `using` directives, so a `TKCode.Arc.Window` would silently replace Avalonia's `Window` in every file under a `TKCode.Arc` namespace."*

`ArcWindow` 自动做的事（静态构造函数）：

```csharp
ExtendClientAreaToDecorationsHintProperty.OverrideDefaultValue<ArcWindow>(true);
WindowDecorationsThemeProperty.OverrideDefaultValue<ArcWindow>(s_noDrawnDecorations);  // 空主题，去掉系统自绘标题栏
```

**不需要你绑任何最小化/最大化/关闭命令** —— 标题栏里的三个按钮由 `WindowCaptionButtons` 内部驱动窗口：

- 空白处按下 → `BeginMoveDrag` 拖动窗口；双击 → `ToggleMaximize()`
- Windows 上标题栏被声明为系统 caption（右键系统菜单、Aero Snap、Snap Layouts 可用）
- macOS 上保留系统红绿灯（`CaptionButtons="Auto"` 在 macOS 解析为 `System`）
- 最大化/还原时 `:maximized` 伪类切换，按钮图形自动换 restore 图标

**属性**

| 属性 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `TitleBar` | `TitleBar?` | 自动 `new TitleBar()` | 顶部标题栏；设 `null` 即无 |
| `IsTitleBarVisible` | `bool` | `true` | 隐藏后内容顶到窗口最上（再配 `ExtendClientAreaToDecorationsHint=False` 可拿回系统标题栏） |

`ArcWindowToken`：`MinWidth`=360、`MinHeight`=240（默认最小尺寸）。
`TitleBarToken`：`Height`=40、`Padding`=(14,0,6,0)、`CaptionButtonSize`=30、`IconSize`=18、`TitleFontSize`=13…

**`TitleBar` 属性**：`Icon` `Title` `LeftContent` `CenterContent` `RightContent` `CaptionButtons`(`Auto`/`Drawn`/`System`/`None`) `ShowTopmostButton`
方法：`ToggleMaximize()`
伪类：`:maximized` `:fullscreen` `:inactive` `:drawn-buttons` `:system-buttons` `:has-right` `:topmost-button`

**完整用法 XAML（可直接抄）**

```xml
<arc:ArcWindow xmlns="https://github.com/avaloniaui"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               xmlns:arc="https://github.com/AIDotNet/TKCode.Arc"
               xmlns:lucide="https://github.com/AIDotNet/TKCode.Arc/lucide"
               x:Class="MyApp.Views.MainWindow"
               Title="My App"
               Width="1100" Height="720"
               MinWidth="800" MinHeight="480"
               WindowStartupLocation="CenterScreen">

    <arc:ArcWindow.TitleBar>
        <arc:TitleBar ShowTopmostButton="True">
            <!-- 标题左侧图标：任意控件 -->
            <arc:TitleBar.Icon>
                <lucide:LucideIcon Kind="Sparkles" Size="14" VerticalAlignment="Center" />
            </arc:TitleBar.Icon>

            <!-- 标题右侧、caption 按钮左边的自定义控件区 -->
            <arc:TitleBar.RightContent>
                <StackPanel Orientation="Horizontal" Spacing="6" Margin="0,0,6,0">
                    <arc:ThemeSwitcher IsIconOnly="True" />
                </StackPanel>
            </arc:TitleBar.RightContent>

            <!-- 标题栏中间（例如搜索框） -->
            <arc:TitleBar.CenterContent>
                <arc:SearchField Width="320" Placeholder="Search…" />
            </arc:TitleBar.CenterContent>
        </arc:TitleBar>
    </arc:ArcWindow.TitleBar>

    <!-- 内容 -->
    <views:MainView />
</arc:ArcWindow>
```

```csharp
using Avalonia.Controls;
using TKCode.Arc;

namespace MyApp.Views;

public partial class MainWindow : ArcWindow
{
    public MainWindow() => InitializeComponent();
}
```

**场景 B：自己画 header，想要系统按钮**（`IsTitleBarVisible="False"` + 在自定义 header 里放按钮）

```xml
<arc:ArcWindow IsTitleBarVisible="False" ExtendClientAreaToDecorationsHint="True" ...>
    <DockPanel>
        <Border DockPanel.Dock="Top" Height="48" Padding="12,0" ...>
            <Grid ColumnDefinitions="*,Auto">
                <TextBlock Text="My App" VerticalAlignment="Center" />
                <!-- 驱动它所在窗口的最小化/最大化/关闭 -->
                <arc:WindowCaptionButtons Grid.Column="1" VerticalAlignment="Center" />
            </Grid>
        </Border>
        <views:MainView />
    </DockPanel>
</arc:ArcWindow>
```

`WindowCaptionButtons` 属性：`ShowMinimizeButton` `ShowMaximizeButton` `ShowCloseButton`（默认全 true）、`IsPreview`（只画不驱动，做 mockup 用）。

> ⚠️ 一个窗口只能有一套 caption 按钮（要么 `TitleBar` 自带的，要么你自己放的）。

### C6. 图标系统

**两套，别混。**

#### (1) 内置 glyph：`ArcIcons`（Core 自带，222 个）

```xml
xmlns:arc="https://github.com/AIDotNet/TKCode.Arc"
<!-- 用在需要 Geometry 的属性上：菜单图标、DockPane.Icon 等 -->
<arc:DockPane Icon="{x:Static arc:ArcIcons.Folder}" ... />
```

C#：`ArcIcons.Folder`（`Geometry` 类型），`ArcIcons.Parse(svgPathData)` 可解析任意 24×24 SVG path。
来源：`src/TKCode.Arc.Core/Glyphs/ArcIcons.g.cs`（由 `tools/lucide/generate_lucide.py` 从 lucide-static 1.59.0 生成）。

包含：`ALargeSmall` `Activity` `Archive` `ArrowDown` `ArrowDownUp` `ArrowDownWideNarrow` `ArrowLeft` `ArrowRight` `ArrowUp` `ArrowUpDown` … `Check` … `File` `FileText` `Folder` `FolderKanban` … `House` … `List` … `Sparkles` … `TriangleAlert` … `Terminal` … `SlidersHorizontal`（222 个）。

#### (2) 全量 Lucide：`TKCode.Arc.Icons.Lucide`

**XAML 引用语法**（`LucideIcon` 是 `ArcGlyph` 子类，`Control`）：

```xml
xmlns:lucide="https://github.com/AIDotNet/TKCode.Arc/lucide"

<!-- 画一个图标 -->
<lucide:LucideIcon Kind="Sparkles" Size="20" />
<lucide:LucideIcon Kind="Bell" Size="16" Foreground="{arc:ArcDesignTokenResource TextMuted}" />

<!-- 需要 Geometry 的属性用 LucideGeometry markup extension -->
<arc:SidebarItem Key="home" Label="Home" Icon="{lucide:LucideGeometry House}" />
<arc:DockPane Title="Explorer" Icon="{x:Static arc:ArcIcons.Folder}" />
```

**C#**：

```csharp
using TKCode.Arc.Icons.Lucide;

var icon = new LucideIcon { Kind = LucideIconKind.Sparkles, Size = 16 };
Geometry g = Lucide.Geometry(LucideIconKind.Bell);       // 找不到时返回空 Geometry 并写 Avalonia 日志 warning
bool has  = LucideIconData.IsRegistered(LucideIconKind.Bell);
```

`LucideIconKind` 枚举共 **2155 个**成员（`src/TKCode.Arc.Icons.Lucide/LucideIconKind.g.cs`）。

**按需编译规则（重要）**：

生成器会扫描 C# 代码、XAML 属性值和 `<ItemGroup>` 里的 `<LucideIcon Include="..."/>`，把用到的图标 path data 编译进**你的程序集**。若 `Kind` 来自绑定/数据，必须显式声明：

```xml
<ItemGroup>
    <LucideIcon Include="Bell;ArrowRight" />   <!-- PascalCase 或 lucide 原名（kebab-case） -->
    <!-- 或者全量编译（体积换省心） -->
    <!-- <LucideIconsIncludeAll>true</LucideIconsIncludeAll> -->
</ItemGroup>
```

来源：`src/TKCode.Arc.Icons.Lucide/buildTransitive/TKCode.Arc.Icons.Lucide.targets`

**未声明会怎样**：不报错、不崩，只是**画不出来**（空 Geometry），Avalonia 日志里一条 `Lucide` warning。官方说 XAML 里绑 `LucideIcon.Kind` 且未声明时会出 `ARCL002` 警告。

---

## D. 依赖与版本

### D1. 版本号（`Directory.Build.props` / `Directory.Packages.props` / `build/Versions.props`）

`build/Versions.props`（唯一真源）：

```xml
<ArcVersion>1.1.0</ArcVersion>
<AvaloniaVersion>12.1.5</AvaloniaVersion>
<!-- 包对 Avalonia 的要求：此版本或任意更晚的 12.x -->
<AvaloniaVersionRange>[$(AvaloniaVersion), 13.0.0)</AvaloniaVersionRange>
```

`global.json`（**只用于构建本仓库**，消费方不需要 .NET 11 SDK）：

```json
{ "sdk": { "version": "11.0.100-rc.2.26504.105", "rollForward": "latestFeature", "allowPrerelease": true } }
```

### D2. 目标框架

`build/ProjectDefaults.props`：

```xml
<ArcTargetFrameworks>net8.0;net10.0;net11.0</ArcTargetFrameworks>
```

例外：`TKCode.Arc.Terminal` → `net10.0;net11.0`（源码注释：*"GCHandle<T> and by-ref-like generics need .NET 9+, so no net8.0 asset."*）

Avalonia 12.1.5 本身只有 `net8.0` 和 `net10.0` 两个 TFM group（已验证 nuspec），所以 **`net11.0` 目标其实落在 Avalonia 的 net10.0 资产上** —— 能跑，但如果你只需要稳，选 `net8.0` 或 `net10.0`。

模板默认 `net10.0`，可选 `net8.0` / `net11.0`。

### D3. 包依赖关系图（从 nuspec 实测）

```
TKCode.Arc
 ├─ TKCode.Arc.Core
 │    └─ TKCode.Arc.Foundation
 │         └─ Avalonia [12.1.5,13.0.0)
 ├─ TKCode.StreamMark.Highlighting.TextMate [0.1.2]
 │    └─ (TKCode.StreamMark.Avalonia / .Core [0.1.2])
 └─ Avalonia [12.1.5,13.0.0)

TKCode.Arc.Blocks        → TKCode.Arc, TKCode.Arc.Audio
TKCode.Arc.Audio         → (无 Arc 依赖，纯抽象)
TKCode.Arc.Audio.Native  → TKCode.Arc.Audio
TKCode.Arc.Audio.Browser → TKCode.Arc.Audio
TKCode.Arc.AdvancedCharts→ TKCode.Arc
TKCode.Arc.Special       → TKCode.Arc
TKCode.Arc.Workflow      → TKCode.Arc
TKCode.Arc.Dock          → TKCode.Arc
TKCode.Arc.Pdf           → TKCode.Arc
TKCode.Arc.Pdf.Native    → TKCode.Arc.Pdf
TKCode.Arc.Pdf.Browser   → TKCode.Arc.Pdf
TKCode.Arc.Video         → TKCode.Arc
TKCode.Arc.Video.Native  → TKCode.Arc.Video
TKCode.Arc.Video.Browser → TKCode.Arc.Video
TKCode.Arc.Icons.Lucide  → TKCode.Arc.Core
TKCode.Arc.Fonts         → (只有 Avalonia)
TKCode.Arc.Themes.Pixel  → TKCode.Arc.Core, TKCode.Arc.Themes.Corners
TKCode.Arc.Themes.Corners→ TKCode.Arc.Core
TKCode.Arc.Mobile        → TKCode.Arc.Core
TKCode.Arc.Terminal      → (只有 Avalonia，net10/net11)
```

### D4. `TKCode.Arc` / `.Core` / `.Foundation` 到底什么关系

| 包 | 角色 | 给谁用 |
|---|---|---|
| **`TKCode.Arc.Foundation`** | 主题/token/语言/包注册**运行时**。只有 `ArcRuntime`、`ArcBuilder`、`ArcThemeManager`、`ArcThemeState`、`ArcThemePreset`、`ArcThemeScope`、`ArcLanguage*`、`LanguageTag`。**一个控件都没有**（唯一的 `ArcDesign` 是废弃的空 `Control`）。依赖：只有 Avalonia | 被间接引用，不用手写 |
| **`TKCode.Arc.Core`** | ArcDesign token 定义、调色板、运动引擎（`ArcMotion`、`Springs`）、primitives（`ArcGlyph`/`ArcSpinner`/`ModalHost`/`ArcPopup`…）、内置 Lucide glyph（`ArcIcons`）。依赖 Foundation | 被间接引用，除非你只写自研控件 |
| **`TKCode.Arc`** | **真正的组件包**：191 个控件（Button/Card/Input/Table/Dialog…）。依赖 Core + StreamMark | ✅ **最终 App 引用这一个** |

NuGet 上 `TKCode.Arc` 的描述是 *"Arc-style component library for Avalonia. Ports the free components of Arc (uiarc.dev) with their visual language and spring motion."* —— 这是**通用描述**（`build/PackageMetadata.props` 里所有包共用同一段 Description 模板，`Core`/`Foundation` 的 nuspec 里也是同一句），所以**不能靠描述区分**。区分要看 `PackageId` 和实际内容：

- `TKCode.Arc.Core` 的 nupkg 里 `lib/net10.0/TKCode.Arc.Core.dll` 只有 514 KB
- `TKCode.Arc` 的 nupkg 里 `lib/net10.0/TKCode.Arc.dll` 有 6.86 MB
- `TKCode.Arc.Core` 的 XML 文档里 `TKCode.Arc.DesignTokens.*` 只有 3 个类型（全局 token）；`TKCode.Arc` 里有 552 个（每个控件一套）

**结论：`TKCode.Arc` v1.1.0 就是"给最终 App 用的组件包"，Core/Foundation 是它的内部依赖。**

---

## E. 可用性风险：哪些包"确认可用"，哪些"存疑/不可用"

### E1. 确认可用（已发布到 NuGet.org，有 1.1.0 正式版，且我核对了 nupkg 实体内容）

全部 23 个包**都已发布 1.1.0 正式版**（`api.nuget.org` 的 `index.json` 实测，非 preview）：

| 包 | 最新版 | TFM | 内容验证 |
|---|---|---|---|
| `TKCode.Arc` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ 6.86 MB DLL，552 个控件 token 类型 |
| `TKCode.Arc.Core` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ 514 KB DLL |
| `TKCode.Arc.Foundation` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ 含 `analyzers/dotnet/cs/TKCode.Arc.Foundation.Generators.dll` + `build*/*.targets` |
| `TKCode.Arc.Fonts` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ |
| `TKCode.Arc.Icons.Lucide` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ 含 Lucide 生成器 analyzer + targets |
| `TKCode.Arc.Blocks` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ |
| `TKCode.Arc.AdvancedCharts` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ |
| `TKCode.Arc.Special` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ |
| `TKCode.Arc.Workflow` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ |
| `TKCode.Arc.Dock` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ |
| `TKCode.Arc.Themes.Pixel` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ |
| `TKCode.Arc.Themes.Corners` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ |
| `TKCode.Arc.Mobile` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅（API 可用，但真机未验证） |
| `TKCode.Arc.Terminal` | 1.1.0 | **net10.0 / net11.0 only** | ✅ **native 库齐全**：`runtimes/{win-x64,win-arm64,linux-x64,linux-arm64,osx-x64,osx-arm64}/native/libghostty-vt.*` |
| `TKCode.Arc.Audio` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ |
| `TKCode.Arc.Audio.Native` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ native 齐全（miniaudio: `libtkcode_audio.*`） |
| `TKCode.Arc.Audio.Browser` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ |
| `TKCode.Arc.Pdf` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ |
| `TKCode.Arc.Pdf.Native` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ native 齐全（PDFium `libpdfium.*`，约 7–8 MB/RID）+ `licenses/` |
| `TKCode.Arc.Pdf.Browser` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅（pdf.js 以静态 web 资产打包） |
| `TKCode.Arc.Video` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ |
| `TKCode.Arc.Video.Native` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅ native 齐全（`libtkcode_video.*`；iOS 走 `runtimes/ios`） |
| `TKCode.Arc.Video.Browser` | 1.1.0 | net8.0 / net10.0 / net11.0 | ✅（net8.0 只有 Canvas 模式，无 `ARC_VIDEO_NATIVE`） |
| `TKCode.Arc.Templates` | 1.1.0 | — | ✅ |

**传递依赖也已确认发布**：`TKCode.StreamMark.Core` / `.Avalonia` / `.Highlighting.TextMate` 均有 `0.1.2`（Arc 把它们钉成精确版本 `[0.1.2]`）。

**结论：不存在"某个包没发布导致 CI 编译失败"的风险。** 所有 `PackageId` 都能 restore 到 1.1.0。

### E2. 存疑 / 需要注意（不是"不可用"，是"用错会出事"）

| 项目 | 风险 | 依据 |
|---|---|---|
| **`TKCode.Arc.Terminal` 在 net8.0 上装不了** | csproj 只有 `net10.0;net11.0`。若你的 App 是 net8.0 并引用它 → **restore 失败** | `src/TKCode.Arc.Terminal/TKCode.Arc.Terminal.csproj` |
| **`TKCode.Arc.Terminal` 需要你自备 pty** | 它只是**终端模拟器**，没有进程后端。"Add it to the Output pane's group once your app has a process to talk to"（模板 README 原文）。直接丢进界面是**空白的** | `templates/TKCode.Arc.Templates/README.md` |
| **native 包体积** | `Pdf.Native` 单 RID 约 8 MB；`Terminal` 单 RID 约 2 MB；`Video.Native`/`Audio.Native` 也带 shim。全引用会显著增大发布体积 | 实测 nupkg `unzip -l` |
| **native 包需要显式 Use()** | `ArcPdfNative.Use()` / `ArcVideoNative.Use()` / `ArcAudioNative.Use()` 必须在启动时调；不调就用 simulated backend（PDF 显示"未设置后端"，视频静音假播放）。**这不会编译失败，只是运行时不符合预期** | 各包 `Description` 字段 + 官方 installation 文档 |
| **`TKCode.Arc.Mobile` 真机未验证** | 官方明说："not yet verified on Android or iOS devices"；只在桌面手机框里预览过 | `README.md`、`src/TKCode.Arc.Mobile/*.csproj` |
| **`TKCode.Arc.Video.Browser` net8.0 功能更弱** | net8.0 下没有 `ARC_VIDEO_NATIVE` 常量（因为 `Avalonia.Browser` 只有 net10.0），只有 Canvas 模式 | `src/TKCode.Arc.Video.Browser/*.csproj` 的 `Condition="'$(TargetFramework)' != 'net8.0'"` |
| **.NET 11 SDK 的 optimized publish 会丢编译 XAML** | 用 .NET 11 SDK + AOT publish 会 `No precompiled XAML found`，必须 `<UseOptimizedPublish>false</UseOptimizedPublish>` | 模板 csproj 注释 + 官方 Troubleshooting |
| **不能开 `InvariantGlobalization`** | 输入框/图表/日期的格式化会炸或错乱 | 官方 installation 文档 |
| **`TKCode.Arc.Core` / `.Foundation` 单独引用没意义** | 里面没有可用控件（`ArcDesign` 是废弃空类）。单独引用 = 编译过但界面啥也画不出 | 已核对 `Core` 的 21 个类型全是 primitives |
| **`TKCode.Arc.BaseTheme` 不存在** | 你在 `src/` 下看不到这个目录；只有 `tests/TKCode.Arc.BaseTheme.Tests` 这个**测试工程**（`InternalsVisibleTo` 里出现了名字，容易误以为有包）。base theme 是 `TKCode.Arc.Core` 内的 `ArcBaseTheme` 类 | `ls src/` 无该目录；`grep TKCode.Arc.BaseTheme` 只命中 `InternalsVisibleTo` 与测试目录 |
| **Apple/移动端 iOS targets 未在默认构建里** | `Video.Native` 的 `net11.0-ios`/`-android` 要 `-p:ArcVideoMobile=true` 才加，且 iOS 需先跑 `tools/video/build-ios.sh` | `src/TKCode.Arc.Video.Native/*.csproj` |

### E3. 本次调研中"未能确认"的点

诚实清单：

1. **所有代码未经编译** —— WSL 环境无 .NET SDK。骨架的正确性是**按源码签名逐条核对**得出的，不是编译验证过的。
2. **`ArcDesignTokenKind` 的成员顺序/总数** —— 源码里 `ArcDesignToken.cs` 是 `internal sealed class`，`Kind` 枚举由生成器产出，仓库里没有提交 `GeneratedFiles/`。我用的是 `ArcDesignToken.cs` 的属性声明（177 个，按 `#region` 分组）。已发布包 XML 文档里能查到 `T:TKCode.Arc.DesignTokens.ArcDesignTokenKind` 但 XML 只含注释，无法逐条枚举成员。**数量与名字与源码属性一一对应，可信；但如果你的代码要 `switch` 全部成员，请在 IDE 里看一次真实枚举。**
3. **`SelectedValue` 在 `Select`/`Combobox`/`MultiSelect`/`SegmentedControl` 的公开声明** —— 这些类源码里**没有**自己声明 `SelectedValueProperty`（只有 `TreeSelect` 和 `Cascader` 声明了）。它们从 Avalonia 12 的 `SelectingItemsControl.SelectedValue` 继承（已在 Avalonia 12.1.5 的 `Avalonia.Controls.xml` 中确认该属性存在，且 `SegmentedControl` 构造函数里设置了 `SelectedValueBinding = TogglesValueBinding.Create()`；模板 XAML 里也在用 `SelectedValue="{Binding ...}"`）。**结论可用，但"它来自 Avalonia 基类而非 Arc 自定义"这点要记住** —— 意味着行为/类型转换规则遵循 Avalonia 12，不遵循 Arc。
4. **`Sticky` 的运行时行为** —— 已确认它是 `public static class Sticky`，附加属性为 `IsEnabled` / `Offset` / `ZIndex` / `IsStuck`（只读）+ `StuckChanged` 事件；但"吸附在什么容器里有效、是否要求 ScrollViewer"这一点**未验证**。
5. **官方 docs 站点（`docs.tkcode.si`）的页面内容** —— 我只抓了 `installation.md`、`theming.md`、`llms.txt` 作为交叉验证。单控件的 `.api.md` 页面**未逐一抓取**；所以本文的控件属性表来自**源码**而非站点。
6. **`TKCode.Arc.Special` / `.Blocks` 各 152 / 132 个控件的属性** —— 只抽样读了少数（`Sidebar`、`SidebarItem`、`SidebarSection`），其余仅列名。需要具体控件属性时请回源 `src/<包>/Blocks|Special/<分类>/<控件>/`。

---

## F. 最小可编译 App 项目骨架（核心交付物）

> 下面这套是**官方推荐路径的等价物**：不使用模板专属的 `AppSetup.UseArcApp()`，把该做的事直接展开在 `Program.cs` 里，方便复制粘贴。
> 目标框架选 `net10.0`（模板默认值，Avalonia 12.1.5 原生支持；`net8.0` 也可，把 TFM 改掉、并把 `Terminal` 排除即可）。

### F1. `MyApp.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <OutputType>WinExe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <Nullable>enable</Nullable>
        <ImplicitUsings>enable</ImplicitUsings>

        <!-- Arc 官方模板默认开启：绑定走编译期生成（无需反射，可裁剪/AOT） -->
        <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>

        <BuiltInComInteropSupport>false</BuiltInComInteropSupport>

        <!-- 用 .NET 11 SDK 做 AOT/优化发布时必须关掉，否则启动报 "No precompiled XAML found" -->
        <UseOptimizedPublish>false</UseOptimizedPublish>

        <!-- 不要开 InvariantGlobalization：输入框/图表/日期格式化需要 culture 数据 -->
        <!-- <InvariantGlobalization>false</InvariantGlobalization> -->
    </PropertyGroup>

    <ItemGroup>
        <!-- 所有 Avalonia 包必须同一版本；Arc 依赖区间是 [12.1.5, 13.0.0) -->
        <PackageReference Include="Avalonia.Desktop" Version="12.1.5" />
        <PackageReference Include="Avalonia.Fonts.Inter" Version="12.1.5" />

        <!-- 组件库本体（带 Core + Foundation）。整个 App 只需要这一行 Arc 引用 -->
        <PackageReference Include="TKCode.Arc" Version="1.1.0" />

        <!-- Geist / Geist Mono（可选，但不装会回退系统字体，观感差别明显） -->
        <PackageReference Include="TKCode.Arc.Fonts" Version="1.1.0" />

        <!-- Lucide 全量图标（可选；只在需要 arc:ArcIcons 之外的图标时加） -->
        <PackageReference Include="TKCode.Arc.Icons.Lucide" Version="1.1.0" />
    </ItemGroup>

    <ItemGroup>
        <!-- 运行期才决定 Kind 的 Lucide 图标必须在这里声明，否则画不出来（不报错）。
             写死 Kind="Bell" 的 XAML 不需要这一行。 -->
        <!-- <LucideIcon Include="Bell;ArrowRight" /> -->

        <!-- 或者图省事，全量编译（体积换省心） -->
        <!-- <LucideIconsIncludeAll>true</LucideIconsIncludeAll> -->
    </ItemGroup>

</Project>
```

**如果你们的解决方案用 Central Package Management**，把版本挪进 `Directory.Packages.props`：

```xml
<ItemGroup>
    <PackageVersion Include="TKCode.Arc" Version="1.1.0" />
    <PackageVersion Include="TKCode.Arc.Fonts" Version="1.1.0" />
    <PackageVersion Include="TKCode.Arc.Icons.Lucide" Version="1.1.0" />
    <PackageVersion Include="Avalonia.Desktop" Version="12.1.5" />
    <PackageVersion Include="Avalonia.Fonts.Inter" Version="12.1.5" />
</ItemGroup>
```

然后 csproj 里去掉 `Version=`。

### F2. `Program.cs`

```csharp
using Avalonia;
using TKCode.Arc;
using TKCode.Arc.Fonts;

namespace MyApp;

internal static class Program
{
    // Avalonia 在 Main 里跑起来之前不可用：不要在 BuildAvaloniaApp() 之前碰任何 Avalonia / Arc API。
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // IDE 的 XAML 预览器也会调用这个方法。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  // 正文用 Inter（Avalonia.Fonts.Inter）
                  .WithInterFont()
                  // 标题用 Geist、代码用 Geist Mono（TKCode.Arc.Fonts）
                  .WithArcFonts()
                  // Arc 的唯一入口：注册 token / 控件主题 / 语言，并发布首帧主题
                  .UseArc(arc => arc
                      .UseArcControls()   // ← 漏了这句，所有 Arc 控件就是"裸奔/空白"
                      .WithTheme(t => t
                          .Appearance(ArcAppearance.System)          // Light / Dark / System
                          .Accent(ArcAccentPreset.Neutral)))          // 8 种预置之一
                  // 需要更多包时在这里继续链式调用（注意包的 using）：
                  // .UseArcBlocks()            // TKCode.Arc.Blocks
                  // .UseArcAdvancedCharts()    // TKCode.Arc.AdvancedCharts
                  // .UseArcSpecial()           // TKCode.Arc.Special
                  // .UseArcDock()              // TKCode.Arc.Dock
                  .LogToTrace();
}
```

> 若还要用到 `Blocks` 等包，把引用加进 csproj，并在 `UseArc(...)` 里加对应的 `UseXxx()`（见 §F8）。

### F3. `App.axaml`（**可选**，Arc 不需要它）

如果你们的项目模板/规范强制要有 `App.axaml`，可以保留一个**空壳**，它不会伤害 Arc：

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="MyApp.App">
    <!--
        这里什么都不用写。

        ✗ 不要写 <StyleInclude Source="..."/> 去引 Arc 主题 —— Arc 没有可 include 的聚合主题字典，
          它的主题全部由 C# 的 UseArc(...) 注册进 Application.Resources.MergedDictionaries。
        ✗ 不要写 <ResourceInclude/> 去引 Arc 的 token 字典 —— 同上。

        △ 如果你写了 <FluentTheme/> 或 <SimpleTheme/>，Arc 的 base theme 会自动"让位"：
          ScrollViewer / ScrollBar / ItemsControl / PathIcon / 弹窗宿主 / Window 将使用该主题而非 Arc 的，
          于是滚动条、Window 背景/字体、overlay 层都不再是 Arc 的样子。能跑，但不"Arc"。
    -->
</Application>
```

配套的 `App.axaml.cs`：

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyApp.Views;

namespace MyApp;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
```

**更推荐（官方模板的做法）：干脆不要 `App.axaml`。** 此时 `App.cs` 不需要 `partial`，也不需要 `Initialize()`：

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using MyApp.Views;

namespace MyApp;

/// <summary>Arc 不需要 App 类做任何事：没有基类要求、没有 App.axaml、没有 styles。</summary>
public class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow();
                break;
            // 浏览器/手机：只显示内容，没有窗口
            case ISingleViewApplicationLifetime singleView:
                singleView.MainView = new MainView();
                break;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
```

> 两个分支二选一：
> - **有 `App.axaml`** → `public partial class App : Application` + `Initialize()` 里 `AvaloniaXamlLoader.Load(this)`
> - **无 `App.axaml`** → `public class App : Application`，不要 `Initialize()`

### F4. `Views/MainWindow.axaml`（用 `ArcWindow` 自定义标题栏）

```xml
<arc:ArcWindow xmlns="https://github.com/avaloniaui"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               xmlns:arc="https://github.com/AIDotNet/TKCode.Arc"
               xmlns:lucide="https://github.com/AIDotNet/TKCode.Arc/lucide"
               xmlns:views="using:MyApp.Views"
               x:Class="MyApp.Views.MainWindow"
               Title="My App"
               Width="960"
               Height="640"
               MinWidth="480"
               MinHeight="400"
               WindowStartupLocation="CenterScreen">

    <!-- Arc 自己画标题栏：图标 + 标题 + 三个自定义区（Left/Center/Right）+ 系统窗钮。
         空白处可拖动窗口，双击最大化。 -->
    <arc:ArcWindow.TitleBar>
        <arc:TitleBar ShowTopmostButton="False">
            <arc:TitleBar.Icon>
                <lucide:LucideIcon Kind="Sparkles" Size="14" VerticalAlignment="Center" />
            </arc:TitleBar.Icon>

            <arc:TitleBar.RightContent>
                <arc:ThemeSwitcher IsIconOnly="True" Margin="0,0,6,0" />
            </arc:TitleBar.RightContent>
        </arc:TitleBar>
    </arc:ArcWindow.TitleBar>

    <views:MainView />
</arc:ArcWindow>
```

**如果暂时不想用自定义标题栏**，换成原生 `Window`：

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="using:MyApp.Views"
        x:Class="MyApp.Views.MainWindow"
        Title="My App"
        Width="960" Height="640"
        MinWidth="480" MinHeight="400"
        WindowStartupLocation="CenterScreen">
    <views:MainView />
</Window>
```

### F5. `Views/MainWindow.axaml.cs`

```csharp
using TKCode.Arc;                 // ArcWindow
using Avalonia.Controls;          // 若用原生 Window

namespace MyApp.Views;

public partial class MainWindow : ArcWindow   // 原生版本改成 : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```

### F6. `Views/MainView.axaml`（内容：Arc 控件综合示例）

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:arc="https://github.com/AIDotNet/TKCode.Arc"
             xmlns:lucide="https://github.com/AIDotNet/TKCode.Arc/lucide"
             x:Class="MyApp.Views.MainView">

    <DockPanel>
        <!-- 顶栏 -->
        <Border DockPanel.Dock="Top"
                Height="56"
                Padding="20,0"
                BorderThickness="0,0,0,1"
                BorderBrush="{arc:ArcDesignTokenResource BorderSubtle}">
            <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="10">
                <Border Width="28" Height="28" CornerRadius="8" VerticalAlignment="Center"
                        Background="{arc:ArcDesignTokenResource Foreground}">
                    <lucide:LucideIcon Kind="Sparkles" Size="14"
                                       HorizontalAlignment="Center" VerticalAlignment="Center"
                                       Foreground="{arc:ArcDesignTokenResource Background}" />
                </Border>
                <TextBlock Grid.Column="1" Text="My App" VerticalAlignment="Center"
                           FontWeight="Medium"
                           Foreground="{arc:ArcDesignTokenResource Foreground}" />
                <arc:ThemeSwitcher Grid.Column="2" IsIconOnly="True" />
            </Grid>
        </Border>

        <!-- 主体 -->
        <ScrollViewer HorizontalScrollBarVisibility="Disabled">
            <StackPanel Margin="24,24,24,32" Spacing="20"
                        MaxWidth="{arc:ArcDesignTokenResource ContentMaxWidthMD}"
                        HorizontalAlignment="Left">

                <TextBlock Text="Components"
                           FontSize="{arc:ArcDesignTokenResource Type2XL}"
                           FontWeight="Medium"
                           Foreground="{arc:ArcDesignTokenResource Foreground}" />

                <!-- Card + 表单控件 -->
                <arc:Card Title="Notification settings"
                          Description="Choose how the app keeps you informed.">
                    <StackPanel Spacing="16" Margin="0,12,0,0">
                        <arc:Input x:Name="EmailInput"
                                   Label="Alert email"
                                   Placeholder="ops@acme.com"
                                   Description="Incidents and failed jobs are sent here." />

                        <arc:Select Label="Frequency" Width="220"
                                    HorizontalAlignment="Left"
                                    SelectedValue="Daily">
                            <arc:SelectItem Content="Realtime" Value="Realtime" />
                            <arc:SelectItem Content="Daily"    Value="Daily" />
                            <arc:SelectItem Content="Weekly"   Value="Weekly" />
                        </arc:Select>

                        <arc:SegmentedControl HorizontalAlignment="Left">
                            <arc:SegmentedOption Value="System" Label="System" />
                            <arc:SegmentedOption Value="Light"  Label="Light" />
                            <arc:SegmentedOption Value="Dark"   Label="Dark" />
                        </arc:SegmentedControl>

                        <arc:Switch Content="Product announcements" IsChecked="True" />

                        <StackPanel Orientation="Horizontal" Spacing="8"
                                    HorizontalAlignment="Right">
                            <arc:Button Content="Cancel" Variant="Secondary"
                                        Click="OnCancel" />
                            <arc:Button Content="Save changes"
                                        Click="OnSave" />
                        </StackPanel>
                    </StackPanel>
                </arc:Card>

                <!-- 进度 + 徽章 + 头像 -->
                <arc:Card Title="Status">
                    <StackPanel Spacing="12" Margin="0,12,0,0">
                        <arc:Progress Label="Upload" Value="64" Maximum="100"
                                      IsValueVisible="True" />
                        <StackPanel Orientation="Horizontal" Spacing="10">
                            <arc:Badge Content="Active" Tone="Success" />
                            <arc:Badge Content="Trial"  Tone="Info" />
                            <arc:Badge Content="Past due" Tone="Warning" />
                            <arc:Avatar DisplayName="Ada Lovelace"
                                        Size="Small" Status="Online" />
                        </StackPanel>
                    </StackPanel>
                </arc:Card>

            </StackPanel>
        </ScrollViewer>
    </DockPanel>
</UserControl>
```

### F7. `Views/MainView.axaml.cs`

```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;
using TKCode.Arc;   // Toast / Dialog / ButtonVariant

namespace MyApp.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        // Toast：owner 传任意 Visual 即可（会挂到它所在窗口的 overlay 层）
        Toast.Show(this, "Settings saved", ToastKind.Success,
                   description: "Your changes take effect immediately.");
    }

    private async void OnCancel(object? sender, RoutedEventArgs e)
    {
        // Dialog：确认框，返回 true 才是"确认"
        var confirmed = await Dialog.ConfirmAsync(
            this,
            "Discard changes?",
            "Your unsaved edits will be lost.",
            confirmText: "Discard",
            cancelText: "Keep editing",
            isDanger: true);
    }
}
```

### F8. 需要更多包时的增量步骤

```bash
# 以 Blocks（页面级组合，比如 Sidebar）+ Dock 为例
dotnet add package TKCode.Arc.Blocks        --version 1.1.0
dotnet add package TKCode.Arc.Dock          --version 1.1.0
```

```csharp
// Program.cs 的 UseArc(...) 里追加
.UseArc(arc => arc
    .UseArcControls()
    .UseArcBlocks()     // Blocks 自带 Sidebar / CommandPalette / AiChat …
    .UseArcDock()       // DockLayout / DockPane
    .WithTheme(...))
```

| 想用的东西 | 加这个包 | 加这个调用 |
|---|---|---|
| `Block` 级页面组件（Sidebar、CommandPalette、SettingsPage…） | `TKCode.Arc.Blocks` | `UseArcBlocks()` |
| `DataGrid`、Sunburst/Sankey 等 | `TKCode.Arc.AdvancedCharts` | `UseArcAdvancedCharts()` |
| AI 讲解器、指针特效、Glass 组件 | `TKCode.Arc.Special` | `UseArcSpecial()` |
| IDE 式 dock | `TKCode.Arc.Dock` | `UseArcDock()` |
| 工作流画布 | `TKCode.Arc.Workflow` | `UseArcWorkflow()` |
| PDF 查看 | `TKCode.Arc.Pdf` + `.Pdf.Native`(桌面) | `UseArcPdf()` + `ArcPdfNative.Use()` |
| 视频 | `TKCode.Arc.Video` + `.Video.Native`(桌面) | `UseArcVideo()` + `ArcVideoNative.Use()` |
| 音频 | `TKCode.Arc.Audio` + `.Audio.Native`(桌面) | — + `ArcAudioNative.Use()` |
| 终端（**net10/net11 only**） | `TKCode.Arc.Terminal` | 无 Use 扩展；直接 `new TerminalView()`（需自备 pty） |
| 像素风 | `TKCode.Arc.Themes.Pixel` | `UseArcPixelTheme()` + 运行时 `ArcTheme.Apply(t => t.Pixel())` |
| 方角/缩放角 | `TKCode.Arc.Themes.Corners` | `UseArcCorners()` + 运行时 `ArcTheme.Apply(t => t.Corners(ArcCornerStyle.Square))` |
| 手机控件 | `TKCode.Arc.Mobile` | `UseArcMobile()`（XAML 用 `arcm:` 前缀） |

### F9. 这个骨架为什么能编译（逐条自查表）

| 检查项 | 骨架里的做法 | 依据 |
|---|---|---|
| TFM 与包 TFM 匹配 | `net10.0`，所有包都有 net10.0 | `build/ProjectDefaults.props` |
| Avalonia 版本一致 | 全部 `12.1.5` | `build/Versions.props` + 包 nuspec 的 `[12.1.5, 13.0.0)` |
| Arc 入口被调用 | `UseArc(arc => arc.UseArcControls()...)` | 官方 Troubleshooting 第 1 条 |
| 首帧主题已设 | `WithTheme(t => t.Appearance(...).Accent(...))` | 官方 installation 文档 |
| 字体已注册（否则回退） | `WithInterFont()` + `WithArcFonts()` | `ArcFonts.Body/Display/Mono` 的 fallback 链 |
| XAML 前缀正确 | `arc:` + `lucide:` | 各包 `AssemblyInfo.cs` 的 `XmlnsPrefix` |
| 不写 StyleInclude | 全文没有任何 `<StyleInclude>` | `ArcRuntime.Install` 已 merge 资源 |
| 控件名不与 Avalonia 冲突 | 全部带 `arc:` 前缀 | 官方 installation "Name clashes" |
| 不用模板专属 API | 没调 `UseArcApp()`（那是模板 `AppSetup.cs` 里的） | 模板 `AppSetup.cs` |
| Lucide 图标可用 | XAML 里 `Kind` 全部写死 → 生成器能扫到 | Lucide targets 说明 |
| overlay 类控件可用 | `ArcWindow` 模板里自带 `VisualLayerManager EnableOverlayLayer="True"` | `Themes/ArcWindowTheme.axaml` |
| .NET 11 SDK AOT 兼容 | `<UseOptimizedPublish>false</UseOptimizedPublish>` | 模板 csproj 注释 |
| culture 数据可用 | 注释里提醒不要 `InvariantGlobalization` | 官方 installation 文档 |

---

## 附：一句话速查卡

```text
包            →  TKCode.Arc (+ .Fonts + .Icons.Lucide)
入口          →  AppBuilder.UseArc(arc => arc.UseArcControls()...)
App 类        →  public class App : Application  （无 App.axaml、无 partial、无 styles）
XAML 前缀     →  xmlns:arc="https://github.com/AIDotNet/TKCode.Arc"
                 xmlns:lucide="https://github.com/AIDotNet/TKCode.Arc/lucide"
颜色          →  {arc:ArcDesignTokenResource Surface|Foreground|TextSecondary|Border|Accent|...}
控件 token    →  {arc:ButtonTokenResource Xxx}
窗口          →  <arc:ArcWindow> + <arc:ArcWindow.TitleBar><arc:TitleBar>…
换肤          →  ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark))
一键明暗开关  →  <arc:ThemeSwitcher IsIconOnly="True" />
图标(内置)    →  {x:Static arc:ArcIcons.Check}
图标(Lucide)  →  <lucide:LucideIcon Kind="Bell" Size="16" />
弹窗          →  await Dialog.ConfirmAsync(this, "标题", "说明")
提示          →  Toast.Show(this, "已保存", ToastKind.Success)
```

---

### 来源文件索引

| 结论 | 来源 |
|---|---|
| 包清单/定位 | `README.md` |
| 模板结构与全部示例代码 | `templates/TKCode.Arc.Templates/README.md`、`templates/.../content/_shared/App/**`、`templates/.../content/ArcStarterApp/**` |
| 版本 / TFM | `build/Versions.props`、`build/ProjectDefaults.props`、`Directory.Packages.props`、`global.json` |
| 包依赖 | 各 `src/*/*.csproj` + `api.nuget.org` 的 nuspec |
| 包是否发布 / native 内容 | `api.nuget.org/v3-flatcontainer/*/index.json` 与 nupkg 实体内 `unzip -l` |
| `UseArc` / `ArcBuilder` / `ArcRuntime` | `src/TKCode.Arc.Foundation/Registration/ArcRuntime.cs`、`ArcBuilder.cs` |
| 主题状态 / 外观 / Toggle | `src/TKCode.Arc.Foundation/Theme/ArcThemeState.cs`、`ArcThemeManager.cs`、`ArcTheme.cs`、`ArcThemePreset.cs`、`ArcThemeScope.cs` |
| 全局 token 全表 | `src/TKCode.Arc.Core/Theme/ArcDesignToken.cs` |
| 强调色表 | `src/TKCode.Arc.Core/Theme/ArcPalette.cs`、`ArcThemeConfig.cs` |
| base theme 让位规则 | `src/TKCode.Arc.Core/Theme/Base/ArcBaseTheme.cs`、`tests/TKCode.Arc.BaseTheme.Tests/ArcBaseThemeTests.cs` |
| `xmlns` 约定 | `src/*/Properties/AssemblyInfo.cs` |
| 控件清单 | 全仓 `grep 'public class Xxx : <基类>'`（787 个 .cs 文件扫描） |
| `ArcWindow` / `TitleBar` | `src/TKCode.Arc/Content/ArcWindow/{ArcWindow,TitleBar,WindowCaptionButtons}.cs` + `Themes/*.axaml` |
| 图标系统 | `src/TKCode.Arc.Icons.Lucide/LucideIcon.cs`、`buildTransitive/*.targets`、`src/TKCode.Arc.Core/Primitives/ArcGlyph.cs`、`Glyphs/ArcIcons.g.cs` |
| 字体 | `src/TKCode.Arc.Core/Theme/ArcFonts.cs`、`src/TKCode.Arc.Fonts/ArcFontCollections.cs` |
| 包清单（NuGet 发布范围） | `CHANGELOG.md` 顶部说明段 |
| 工程规则 | `AGENTS.md`（"Never create `*Themes.axaml` aggregates"、"no App.axaml" 等） |
| 交叉验证 | `https://docs.tkcode.si/docs/installation.md`、`https://docs.tkcode.si/docs/theming.md`、`https://docs.tkcode.si/llms.txt` |
