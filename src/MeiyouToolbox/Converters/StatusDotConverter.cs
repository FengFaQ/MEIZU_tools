using Avalonia.Data.Converters;
using Avalonia.Media;
using TKCode.Arc;

namespace MeiyouToolbox.Converters;

/// <summary>连接状态 → 状态圆点颜色（绿=已连接，灰=未连接）。</summary>
public sealed class StatusDotConverter : IValueConverter
{
    private static readonly IBrush Connected = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush Disconnected = new SolidColorBrush(Color.Parse("#9CA3AF"));

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Connected : Disconnected;

    public object ConvertBack(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>侧栏导航选中态 → Arc 按钮变体（选中用 Primary，未选中用 Text）。</summary>
public sealed class NavVariantConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? ButtonVariant.Secondary : ButtonVariant.Text;

    public object ConvertBack(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
