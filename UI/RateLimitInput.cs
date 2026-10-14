using System.Globalization;

namespace SneakyFlow.UI;

public static class RateLimitInput
{
    // Keep the saved profile and engine limits compatible, while displaying MB/s.
    public static string Format(int kibibytes) => (kibibytes * 1024m / 1_000_000m).ToString("0.######", L.Culture);
    public static int Parse(string text, string label)
    {
        var normalized = text.Trim().Replace(',', '.');
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || value < 0 || value > 2147.482624m)
            throw new FormatException(L.F($"{label}: введите скорость от 0 до 2147,482624 МБ/с."));
        // Positive limits must never round to zero, which means unlimited to the engine.
        return value == 0 ? 0 : Math.Max(1, (int)decimal.Round(value * 1_000_000m / 1024m));
    }
}
