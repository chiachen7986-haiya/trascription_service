namespace BlueJays.Core.Translation;

/// <summary>Transport metadata only: never put meeting text or private reasoning in an error.</summary>
internal sealed class CompletionTruncatedException : Exception
{
    public CompletionTruncatedException(string message = "The API reached its output limit before finishing.") : base(message) { }
}

internal sealed class CompletionReasoningDetectedException : Exception { }
