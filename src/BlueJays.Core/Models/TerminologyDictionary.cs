namespace BlueJays.Core.Models;

public sealed record TerminologyEntry(string Term, string Language, string Reading = "", string Meaning = "");

public sealed record TerminologyDictionary(Guid Id, string Name, TerminologyEntry[] Entries)
{
    public ConversationOptions ApplyTo(ConversationOptions conversation)
    {
        if (Entries.Length == 0) return conversation;
        var hints = Entries.Select(entry => $"{entry.Term} [{entry.Language}]" +
            (entry.Reading.Length == 0 ? "" : $" ({entry.Reading})") +
            (entry.Meaning.Length == 0 ? "" : $" = {entry.Meaning}"));
        return conversation with
        {
            Glossary = Join(hints),
            RecognitionGlossary = Join(Entries.Select(entry => entry.Term))
        };

        string Join(IEnumerable<string> entries) => string.Join("\n", entries.Prepend(conversation.Glossary)
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}
