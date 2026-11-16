using BlueJays.App.Localization;
namespace BlueJays.App.ViewModels;

public sealed class LanguageChoice(string code, string name) : ObservableObject
{
    private bool spoken, target;
    public string Code => code;
    public string Name => name;
    public string DisplayName => Localizer.T(name);
    public string SpokenAccessibility => Localizer.F("Spoken: {0}", DisplayName);
    public string TargetAccessibility => Localizer.F("Translate into: {0}", DisplayName);
    public bool Spoken { get => spoken; set => Set(ref spoken, value); }
    public bool Target { get => target; set => Set(ref target, value); }
    public static List<LanguageChoice> Create() =>
    [
        new("en", "English"), new("ja", "Japanese"), new("zh", "Chinese (Traditional)"),
        new("zh", "Chinese (Simplified)"), new("ko", "Korean"), new("fr", "French"),
        new("de", "German"), new("es", "Spanish"), new("pt", "Portuguese"), new("vi", "Vietnamese"),
        new("th", "Thai"), new("id", "Indonesian"), new("hi", "Hindi"), new("ar", "Arabic"),
        new("it", "Italian"), new("ru", "Russian"), new("uk", "Ukrainian"), new("nl", "Dutch")
    ];
}
