using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MeiyouToolbox.Converters;

/// <summary>连接状态 → 状态圆点颜色（绿=已连接，灰=未连接）。</summary>
public sealed class StatusDotConverter : IValueConverter
{
    public static StatusDotConverter Instance { get; } = new();

    private static readonly IBrush Connected = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush Disconnected = new SolidColorBrush(Color.Parse("#6B7280"));

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Connected : Disconnected;

    public object ConvertBack(object? value, Type targetType, object? parameter,
        System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
