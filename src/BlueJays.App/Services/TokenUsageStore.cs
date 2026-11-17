using System.IO;
using System.Text.Json;
using BlueJays.Core.Translation;

namespace BlueJays.App.Services;

public sealed record DailyTokenUsage(DateOnly Day, long Input = 0, long CachedInput = 0, long Output = 0, long Unreported = 0)
{
    public long Total => Input + Output;
}

public sealed class TokenUsageStore
{
    public const string FileName = "token-usage.v1.json";
    private readonly string path;
    private readonly object gate = new();
    private readonly Dictionary<DateOnly, DailyTokenUsage> days = new();
    private readonly bool readable = true;
    private bool saveFailed;

    public TokenUsageStore(string dataDirectory)
    {
        path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path)) return;
        try
        {
            using var stream = File.OpenRead(path);
            var saved = JsonSerializer.Deserialize<DailyTokenUsage[]>(stream);
            if (saved is null || saved.Any(day => day is null || day.Day == default || day.Input < 0 ||
                day.CachedInput < 0 || day.CachedInput > day.Input || day.Output < 0 || day.Unreported < 0) ||
                saved.Select(day => day.Day).Distinct().Count() != saved.Length)
                throw new JsonException("Invalid usage history.");
            foreach (var day in saved) days.Add(day.Day, day);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            readable = false;
        }
    }

    public (DailyTokenUsage[] Days, bool StorageWarning) Snapshot()
    {
        lock (gate) return (days.Values.ToArray(), !readable || saveFailed);
    }

    public void Record(ApiTokenUsage? usage) => Record(usage, DateOnly.FromDateTime(DateTime.Now));

    public void Record(ApiTokenUsage? usage, DateOnly day)
    {
        lock (gate)
        {
            var previous = days.GetValueOrDefault(day) ?? new(day);
            days[day] = usage is { } tokens ? previous with
            {
                Input = previous.Input + tokens.Input,
                CachedInput = previous.CachedInput + tokens.CachedInput,
                Output = previous.Output + tokens.Output
            } : previous with { Unreported = previous.Unreported + 1 };
            if (!readable) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                using (var output = File.Create(temporary)) JsonSerializer.Serialize(output, days.Values);
                File.Move(temporary, path, overwrite: true);
                saveFailed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                saveFailed = true;
            }
        }
    }
}
