using Avalonia;
using TKCode.Arc;
using TKCode.Arc.Fonts;

namespace MeiyouToolbox;

/// <summary>
/// 应用如何装配 Avalonia 与 Arc：所有宿主（桌面 Program.cs、测试宿主）都从
/// <c>AppBuilder.Configure&lt;App&gt;()</c> 开始，选平台后调用 <see cref="UseArcApp"/>。
/// </summary>
public static class AppSetup
{
    /// <summary>字体、Arc 的包，以及第一帧的主题。</summary>
    public static AppBuilder UseArcApp(this AppBuilder builder) =>
        builder.WithInterFont()      // 正文用 Inter（Avalonia.Fonts.Inter）
               .WithArcFonts()       // 标题用 Geist，等宽用 Geist Mono（TKCode.Arc.Fonts）
               .UseArc(arc => arc
                   .UseArcBlocks()   // Arc 控件 + 页面级 blocks（Sidebar/Card 等），内部已注册 UseArcControls()
                   // 固定为浅色外观：整套界面按浅色设计，避免跟随系统暗色导致对比度问题。
                   // 运行时仍可用 ArcTheme.Apply(t => t.Appearance(...)) 切换。
                   .WithTheme(t => t.Appearance(ArcAppearance.Light)
                                    .Accent(ArcAccentPreset.Neutral)));
}
