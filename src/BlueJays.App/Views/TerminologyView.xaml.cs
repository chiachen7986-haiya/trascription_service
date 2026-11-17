using System.Windows;
using System.Windows.Controls;
using BlueJays.App.Services;
using BlueJays.App.ViewModels;
using BlueJays.Core.Models;
using Microsoft.Win32;
using L = BlueJays.App.Localization.Localizer;

namespace BlueJays.App.Views;

public partial class TerminologyView : UserControl
{
    private readonly List<LanguageChoice> languages = LanguageChoice.Create().Select(choice => choice.Name switch
    {
        "Chinese (Traditional)" => new LanguageChoice("zh-Hant", choice.Name),
        "Chinese (Simplified)" => new LanguageChoice("zh-Hans", choice.Name),
        _ => choice
    }).Append(new("und", "Other language")).ToList();
    private TerminologyStore? store;
    private TerminologyDictionary? SelectedDictionary => DictionaryBox.SelectedItem as TerminologyDictionary;
    private sealed record EntryRow(TerminologyEntry Entry, string LanguageName)
    {
        public string Term => Entry.Term;
        public string Details => string.Join(" · ", new[] { LanguageName, Entry.Reading, Entry.Meaning }
            .Where(value => value.Length != 0));
    }

    public TerminologyView()
    {
        InitializeComponent();
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedIndex = 0;
    }

    public void Initialize(TerminologyStore library)
    {
        store = library;
        Run(() => Reload(null), "Could not load dictionaries.");
    }

    private void Reload(Guid? selectedId)
    {
        DictionaryBox.ItemsSource = store!.Load().OrderBy(dictionary => dictionary.Name).ToArray();
        if (selectedId is { } id) DictionaryBox.SelectedValue = id;
        else DictionaryBox.SelectedIndex = DictionaryBox.Items.Count == 0 ? -1 : 0;
    }

    private void Dictionary_Changed(object sender, SelectionChangedEventArgs e)
    {
        var dictionary = SelectedDictionary;
        EntryEditor.IsEnabled = ExportButton.IsEnabled = DeleteDictionaryButton.IsEnabled = dictionary is not null;
        NameBox.Text = dictionary?.Name ?? "";
        SaveDictionaryButton.Content = L.T(dictionary is null ? "Create dictionary" : "Save name");
        EntriesBox.ItemsSource = dictionary?.Entries.Select(entry => new EntryRow(entry, LanguageName(entry.Language))).ToArray();
        SetEntry(null);
    }

    private string LanguageName(string code) => languages.FirstOrDefault(choice => choice.Code == code)?.DisplayName ?? code;

    private void NewDictionary_Click(object sender, RoutedEventArgs e)
    {
        DictionaryBox.SelectedIndex = -1;
        NameBox.Clear();
        NameBox.Focus();
        StatusText.Text = "";
    }

    private void SaveDictionary_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { StatusText.Text = L.T("Enter a dictionary name."); return; }
        var dictionary = SelectedDictionary ?? new(Guid.NewGuid(), "", []);
        Save(dictionary with { Name = NameBox.Text.Trim() });
    }

    private void DeleteDictionary_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDictionary is not { } dictionary || MessageBox.Show(Window.GetWindow(this),
                L.F("Delete the dictionary “{0}”?", dictionary.Name), L.T("Delete dictionary"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Run(() => { store!.Delete(dictionary.Id); Reload(null); }, "Could not delete the dictionary.", "Dictionary deleted.");
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = L.T("Dictionary JSON (*.json)|*.json"), CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        Run(() => Reload(store!.Import(dialog.FileName).Id), "Could not import the dictionary.", "Dictionary imported.");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDictionary is not { } dictionary) return;
        var dialog = new SaveFileDialog { Filter = L.T("Dictionary JSON (*.json)|*.json"), DefaultExt = ".json", FileName = "dictionary.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        Run(() => store!.Export(dialog.FileName, dictionary), "Could not export the dictionary.", "Dictionary exported.");
    }

    private void Entry_Changed(object sender, SelectionChangedEventArgs e) => SetEntry((EntriesBox.SelectedItem as EntryRow)?.Entry);

    private void SetEntry(TerminologyEntry? entry)
    {
        if (entry is not null && !languages.Any(choice => choice.Code == entry.Language))
        {
            languages.Add(new(entry.Language, entry.Language));
            LanguageBox.Items.Refresh();
        }
        TermBox.Text = entry?.Term ?? "";
        LanguageBox.SelectedValue = entry?.Language ?? "en";
        ReadingBox.Text = entry?.Reading ?? "";
        MeaningBox.Text = entry?.Meaning ?? "";
        SaveEntryButton.Content = L.T(entry is null ? "Add entry" : "Save entry");
        DeleteEntryButton.IsEnabled = entry is not null;
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e) =>
        ReadingFields.Visibility = LanguageBox.SelectedValue as string == "ja" ? Visibility.Visible : Visibility.Collapsed;

    private void NewEntry_Click(object sender, RoutedEventArgs e)
    {
        EntriesBox.SelectedIndex = -1;
        SetEntry(null);
        TermBox.Focus();
    }

    private void SaveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDictionary is not { } dictionary || LanguageBox.SelectedValue is not string language) return;
        if (string.IsNullOrWhiteSpace(TermBox.Text)) { StatusText.Text = L.T("Enter a term or acronym."); return; }
        var entry = new TerminologyEntry(TermBox.Text.Trim(), language, language == "ja" ? ReadingBox.Text.Trim() : "", MeaningBox.Text.Trim());
        var entries = dictionary.Entries.ToList();
        if (EntriesBox.SelectedIndex < 0) entries.Add(entry);
        else entries[EntriesBox.SelectedIndex] = entry;
        Save(dictionary with { Entries = entries.ToArray() });
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDictionary is not { } dictionary || EntriesBox.SelectedIndex < 0) return;
        var entries = dictionary.Entries.ToList();
        entries.RemoveAt(EntriesBox.SelectedIndex);
        Save(dictionary with { Entries = entries.ToArray() });
    }

    private void Save(TerminologyDictionary dictionary) =>
        Run(() => { store!.Save(dictionary); Reload(dictionary.Id); }, "Could not save the dictionary.", "Dictionary saved.");

    private void Run(Action operation, string error, string? success = null)
    {
        if (store is null) return;
        try { operation(); StatusText.Text = success is null ? "" : L.T(success); }
        catch (Exception ex) when (TerminologyStore.IsFileError(ex)) { StatusText.Text = L.T(error); }
    }
}
