using System.Windows;
using BlueJays.App.Services;
using BlueJays.App.ViewModels;
using BlueJays.Core.Models;
using L = BlueJays.App.Localization.Localizer;

namespace BlueJays.App.Views;

public partial class ConversationWindow : Window
{
    private readonly List<LanguageChoice> choices = LanguageChoice.Create();
    public ConversationOptions? Result { get; private set; }
    public ConversationWindow(AppSettings settings)
    {
        InitializeComponent(); WindowSizing.FitToWorkArea(this);
        NameBox.Text = L.F("Conversation {0}", DateTime.Now.ToString("MM-dd HH:mm"));
        foreach (var choice in choices)
        {
            choice.Spoken = settings.SpeechLanguage == "auto" ? choice.Code is "en" or "ja" : choice.Code == settings.SpeechLanguage;
            choice.Target = choice.Name == settings.TargetLanguage || choice.Code == "ja";
        }
        DataContext = choices;
    }
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var spoken = choices.Where(c => c.Spoken).Select(c => c.Code).Distinct().ToList();
        var targets = choices.Where(c => c.Target).Select(c => c.Name).ToList();
        if (string.IsNullOrWhiteSpace(NameBox.Text) || spoken.Count == 0 || targets.Count > 4)
        { ErrorText.Text = L.T("Enter a name, select at least one spoken language and no more than four output languages."); return; }
        Result = new() { Name = NameBox.Text.Trim(), SpokenLanguages = spoken, TargetLanguages = targets, Topic = TopicBox.Text.Trim(), Glossary = GlossaryBox.Text.Trim() };
        DialogResult = true;
    }
}
