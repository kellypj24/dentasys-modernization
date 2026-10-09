namespace Dentasys.Notetaker;

/// <summary>
/// Transcript in, draft out. The local model, a BAA-covered cloud endpoint and a
/// test double all sit behind this, so the eval harness scores them identically
/// and the notes service can fall back from one to another when the cloud is
/// unreachable.
/// </summary>
public interface INoteDrafter
{
    /// <summary>Recorded on every draft so the provider can see what produced it.</summary>
    string Source { get; }

    Task<DraftResult> DraftAsync(IReadOnlyList<TranscriptLine> transcript, CancellationToken ct = default);
}

public sealed record DraftResult
{
    public ClinicalNoteDraft? Draft { get; init; }
    public string? Error { get; init; }
    public TimeSpan Elapsed { get; init; }
    public int? PromptTokens { get; init; }
    public int? OutputTokens { get; init; }
}
