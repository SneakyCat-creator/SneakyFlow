using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SneakyFlow;

public sealed class L : INotifyPropertyChanged
{
    public sealed record Entry(string Ru, string En);
    private static readonly Dictionary<string, Entry> Catalog = Load();
    private static readonly Dictionary<string, string> English = Catalog.Values.ToDictionary(x => x.Ru, x => x.En);
    private static readonly Dictionary<string, string> Russian = Catalog.Values.GroupBy(x => x.En).ToDictionary(x => x.Key, x => x.First().Ru);
    private static readonly Lazy<(Regex Ru, Regex En, Entry Text)[]> Formats = new(() => Catalog.Values
        .Where(x => Regex.IsMatch(x.Ru, @"\{\d+(?:[^}]*)\}"))
        .Select(x => (Pattern(x.Ru), Pattern(x.En), x)).ToArray());
    public static L Instance { get; } = new();
    public static string Language { get; private set; } = "ru";
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "en" ? "en-US" : "ru-RU");
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => Catalog.TryGetValue(key, out var value) ? Language == "en" ? value.En : value.Ru : key;
    public static string T(string text) => Language == "en" && English.TryGetValue(text, out var value) ? value : text;
    public static string F(FormattableString text) => string.Format(Culture, T(text.Format), text.GetArguments());
    // Translate retained journal entries after switching languages. Only complete
    // application templates match; peer addresses, torrent names and errors stay intact.
    public static string Existing(string text) => Existing(text, 0);
    private static string Existing(string text, int depth)
    {
        if ((Language == "en" ? English : Russian).TryGetValue(text, out var exact)) return exact;
        if (depth > 2) return text;
        foreach (var format in Formats.Value)
        {
            var match = (Language == "en" ? format.Ru : format.En).Match(text);
            if (!match.Success) continue;
            return Regex.Replace(Language == "en" ? format.Text.En : format.Text.Ru, @"\{(\d+)[^}]*\}",
                m => Existing(match.Groups["a" + m.Groups[1].Value].Value, depth + 1));
        }
        return text;
    }
    private static Regex Pattern(string template)
    {
        var chunks = Regex.Split(template, @"(\{\d+[^}]*\})");
        var pattern = string.Concat(chunks.Select(x => Regex.IsMatch(x, @"^\{\d+")
            ? "(?<a" + Regex.Match(x, @"\d+").Value + ">.*?)" : Regex.Escape(x)));
        return new Regex("\\A" + pattern + "\\z", RegexOptions.Singleline | RegexOptions.NonBacktracking);
    }
    public static void SetLanguage(string language)
    {
        Language = language == "en" ? "en" : "ru";
        CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
    }
    private static Dictionary<string, Entry> Load()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("SneakyFlow.Strings.json")
            ?? throw new InvalidOperationException("Missing language catalog");
        return JsonSerializer.Deserialize<Dictionary<string, Entry>>(resource)!;
    }
}
