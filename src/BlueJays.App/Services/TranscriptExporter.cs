using System.IO;
using System.Net.Http;
using System.Text;
using BlueJays.Core.Models;
using BlueJays.Core.Translation;
using BlueJays.App.Localization;

namespace BlueJays.App.Services;

public sealed record ExportTranslation(string Language, string Text, string Status);
public sealed record ExportCaption(SummaryCaption Caption, IReadOnlyList<ExportTranslation> Translations, string RawText = "");
public sealed record ExportSnapshot(string Name, string Languages, string UiLanguage, IReadOnlyList<ExportCaption> Captions);
public sealed record ExportResult(string? SummaryError);

/// <summary>Exports a fixed snapshot, so recognition and conversation switching can continue safely.</summary>
public sealed class TranscriptExporter(HttpClient http, Action<ApiTokenUsage?>? recordUsage = null)
{
    public static bool CanSummarize(AppSettings settings, string key, bool apiSucceeded)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiModel)) return false;
        try { var endpoint = TranslationClient.GetEndpoint(settings.ApiBaseUrl); return !string.IsNullOrWhiteSpace(key) || endpoint.IsLoopback || apiSucceeded; }
        catch (ArgumentException) { return false; }
    }

    public async Task<ExportResult> ExportAsync(string path, ExportSnapshot snapshot, AppSettings settings, string key,
        bool apiSucceeded, IProgress<string>? progress, CancellationToken token)
    {
        string T(string value) => Localizer.For(snapshot.UiLanguage, value);
        MeetingSummary? summary = null; string? error = null;
        if (CanSummarize(settings, key, apiSucceeded))
        {
            try
            {
                summary = await new MeetingSummaryService(http, recordUsage).GenerateAsync(snapshot.Captions.Select(c => c.Caption).ToArray(),
                    Localizer.SummaryLanguage(snapshot.UiLanguage), settings, key, progress, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { error = T("Summary canceled; transcript retained."); }
            catch (Exception ex) { error = ex is OperationCanceledException ? T("API timeout") : ex.Message; }
        }
        else error = T("Summary unavailable: API is not configured.");

        var text = new StringBuilder().AppendLine($"BlueJays · {snapshot.Name}").AppendLine(snapshot.Languages).AppendLine();
        text.AppendLine(T("Meeting summary")).AppendLine(T("Summary covers the exported captions only. Unconfirmed text may contain recognition errors."));
        if (summary is null) text.AppendLine(error);
        else
        {
            text.AppendLine(summary.Overview).AppendLine().AppendLine(T("Decisions"));
            if (summary.Decisions.Length == 0) text.AppendLine(T("No decisions explicitly mentioned."));
            foreach (var decision in summary.Decisions) text.AppendLine("- " + decision);
            text.AppendLine().AppendLine(T("Action items / To-do"));
            if (summary.ActionItems.Length == 0) text.AppendLine(T("No action items explicitly mentioned."));
            foreach (var action in summary.ActionItems)
                text.AppendLine($"- {action.Task} | {T("Owner")}: {(string.IsNullOrWhiteSpace(action.Owner) ? T("Not specified") : action.Owner)} | {T("Due")}: {(string.IsNullOrWhiteSpace(action.Due) ? T("Not specified") : action.Due)}");
            text.AppendLine().AppendLine(T("Next meeting schedule"));
            if (summary.NextMeetings.Length == 0) text.AppendLine(T("No next meeting schedule explicitly mentioned."));
            foreach (var meeting in summary.NextMeetings) text.AppendLine($"- [{T(meeting.Status == "confirmed" ? "Scheduled" : "Proposed")}] {meeting.Details}");
        }
        text.AppendLine().AppendLine(new string('=', 48)).AppendLine(T("Transcript")).AppendLine();
        foreach (var caption in snapshot.Captions)
        {
            text.AppendLine($"[{caption.Caption.Time:yyyy-MM-dd HH:mm:ss zzz}] {caption.Caption.Source}").AppendLine(caption.Caption.Text);
            if (caption.RawText.Length > 0 && caption.RawText != caption.Caption.Text)
                text.AppendLine($"[{T("Context adjusted · raw STT available")}] {T("Raw recognition before context correction")}: {caption.RawText}");
            foreach (var translation in caption.Translations)
                text.AppendLine($"{T(translation.Language)}: {translation.Text} [{T(translation.Status)}]");
            text.AppendLine();
        }
        // Saving raw text is independent of summarization success or cancellation.
        await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(true), CancellationToken.None);
        return new(error);
    }
}
