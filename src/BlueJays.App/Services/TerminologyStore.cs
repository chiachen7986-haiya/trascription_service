using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlueJays.Core.Models;

namespace BlueJays.App.Services;

public sealed class TerminologyStore(string dataDirectory)
{
    public const string FileName = "terminology.v1.json";
    private readonly string path = Path.Combine(dataDirectory, FileName);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private sealed record DictionaryFile(int Version, TerminologyDictionary[] Dictionaries);

    internal static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException;

    public TerminologyDictionary[] Load() => File.Exists(path) ? Read(path) : [];

    public void Save(TerminologyDictionary dictionary)
    {
        var dictionaries = Load().Where(existing => existing.Id != dictionary.Id).Append(Normalize(dictionary));
        Write(path, dictionaries);
    }

    public void Delete(Guid id) => Write(path, Load().Where(dictionary => dictionary.Id != id));

    public TerminologyDictionary Import(string source)
    {
        var dictionaries = Read(source);
        if (dictionaries.Length != 1) throw new InvalidDataException("Select a file containing one dictionary.");
        var imported = dictionaries[0] with { Id = Guid.NewGuid() };
        var existing = Load();
        var copyName = imported.Name;
        for (var suffix = 2; existing.Any(dictionary => string.Equals(dictionary.Name, copyName, StringComparison.OrdinalIgnoreCase)); suffix++)
            copyName = $"{imported.Name} ({suffix})";
        imported = imported with { Name = copyName };
        Write(path, existing.Append(imported));
        return imported;
    }

    public void Export(string destination, TerminologyDictionary dictionary)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a location outside the dictionary library file.");
        Write(destination, [dictionary]);
    }

    private static TerminologyDictionary[] Read(string source)
    {
        using var input = File.OpenRead(source);
        var file = JsonSerializer.Deserialize<DictionaryFile>(input, JsonOptions);
        if (file is not { Version: 1, Dictionaries: not null })
            throw new InvalidDataException("Unrecognized dictionary file version.");
        var dictionaries = file.Dictionaries.Select(Normalize).ToArray();
        if (dictionaries.Select(dictionary => dictionary.Id).Distinct().Count() != dictionaries.Length)
            throw new InvalidDataException("The file contains duplicate dictionary IDs.");
        return dictionaries;
    }

    private static TerminologyDictionary Normalize(TerminologyDictionary dictionary)
    {
        if (dictionary is null || dictionary.Id == Guid.Empty || string.IsNullOrWhiteSpace(dictionary.Name) || dictionary.Entries is null ||
            dictionary.Entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Term) || string.IsNullOrWhiteSpace(entry.Language)))
            throw new InvalidDataException("A dictionary is missing a name, term, or term language.");
        return dictionary with
        {
            Name = dictionary.Name.Trim(),
            Entries = dictionary.Entries.Select(entry => new TerminologyEntry(
                SingleLine(entry.Term), SingleLine(entry.Language).ToLowerInvariant() switch
                {
                    "zh-hans" => "zh-Hans",
                    "zh-hant" => "zh-Hant",
                    var code => code
                },
                entry.Language.Trim().Equals("ja", StringComparison.OrdinalIgnoreCase) ? SingleLine(entry.Reading) : "",
                SingleLine(entry.Meaning))).ToArray()
        };
    }

    private static string SingleLine(string? value) => value?.ReplaceLineEndings(" ").Trim() ?? "";

    private static void Write(string target, IEnumerable<TerminologyDictionary> dictionaries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var temporary = target + ".tmp";
        using (var output = File.Create(temporary))
            JsonSerializer.Serialize(output, new DictionaryFile(1, dictionaries.ToArray()), JsonOptions);
        File.Move(temporary, target, overwrite: true);
    }
}
