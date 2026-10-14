using System.Globalization;
using System.Windows.Data;
using SneakyFlow.Models;

namespace SneakyFlow.UI;

public static class DisplayFormat
{
    public static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }
    public static string Bytes(long value)
    {
        var parts = SizeParts(value);
        return $"{parts.Number} {parts.Unit}";
    }

    public static (string Number, string Unit) SizeParts(long value)
    {
        string[] units = [L.T("Б"), L.T("КБ"), L.T("МБ"), L.T("ГБ"), L.T("ТБ")];
        double size = Math.Max(0, value);
        var unit = 0;
        while (size >= 1000 && unit < units.Length - 1) { size /= 1000; unit++; }
        return (size.ToString(unit == 0 ? "0" : "0.#", L.Culture), units[unit]);
    }

    // File sizes and network speeds use decimal byte units.
    public static string SpeedNumber(long value) => (Math.Max(0, value) / 1_000_000d).ToString("0.###", L.Culture);
    public static string Speed(long value) => L.F($"{SpeedNumber(value)} МБ/с");
}

public sealed class SpeedNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => DisplayFormat.SpeedNumber(System.Convert.ToInt64(value ?? 0));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => DisplayFormat.Bytes(System.Convert.ToInt64(value ?? 0));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class SpeedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => DisplayFormat.Speed(System.Convert.ToInt64(value ?? 0));
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed record PriorityOption(FileDownloadPriority Value, string Label);
public static class PriorityOptions
{
    public static IReadOnlyList<PriorityOption> All => [new(FileDownloadPriority.High, L.T("Высокий")), new(FileDownloadPriority.Normal, L.T("Обычный")), new(FileDownloadPriority.Low, L.T("Низкий")), new(FileDownloadPriority.Skip, L.T("Не загружать"))];
}

public sealed class PriorityLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => PriorityOptions.All.FirstOrDefault(x => Equals(x.Value, value))?.Label ?? L.T("Обычный");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
