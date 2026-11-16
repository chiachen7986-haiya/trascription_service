using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Markup;

namespace BlueJays.App.Localization;

/// <summary>Embedded UI strings. Transcript text, source names and API language IDs are never translated here.</summary>
public sealed class Localizer : INotifyPropertyChanged
{
    private static readonly Dictionary<string, string[]> Catalog = Load();
    public static Localizer Current { get; } = new();
    public static IReadOnlyList<string> SupportedLanguages { get; } = ["en", "ja", "zh-Hans", "zh-Hant"];
    public string Language { get; private set; } = "en";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<EventArgs>? LanguageChanged;
    public string this[string key] => For(Language, key);
    public static string T(string key) => Current[key];
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
    public static string For(string language, string key, params object?[] args)
    {
        var value = Catalog.GetValueOrDefault(key);
        var column = language switch { "ja" => 1, "zh-Hans" => 2, "zh-Hant" => 3, _ => 0 };
        var translated = value is null ? key : value[column];
        return args.Length == 0 ? translated : string.Format(CultureInfo.InvariantCulture, translated, args);
    }
    public void SetLanguage(string language)
    {
        var next = SupportedLanguages.Contains(language) ? language : "en";
        if (Language == next) return;
        Language = next;
        PropertyChanged?.Invoke(this, new("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
    public static string SummaryLanguage(string language) => language switch
    { "ja" => "Japanese", "zh-Hans" => "Simplified Chinese", "zh-Hant" => "Traditional Chinese", _ => "English" };
    public static string MakeKey(string english) => string.Concat(Regex.Matches(english, "[A-Za-z0-9]+")
        .Cast<Match>().Take(10).Select(m => char.ToUpperInvariant(m.Value[0]) + m.Value[1..]));
    public static bool HasTranslation(string key) => Catalog.ContainsKey(key);
    public static IEnumerable<string> Keys => Catalog.Keys;

    private static Dictionary<string, string[]> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BlueJays.Strings.tsv")!;
        using var reader = new StreamReader(stream);
        var result = new Dictionary<string, string[]>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var values = line.Split('\t');
            if (values.Length != 4 || values.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("Invalid localization row: " + line);
            result.Add(values[0], values);
            var key = MakeKey(values[0]);
            if (key != values[0]) result.Add(key, values);
        }
        return result;
    }
}

/// <summary>Live one-way binding: changing UI language refreshes existing windows and templates.</summary>
public sealed class TrExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]") { Source = Localizer.Current, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
