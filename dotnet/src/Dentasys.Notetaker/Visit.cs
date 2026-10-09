using System.Text.Json;

namespace Dentasys.Notetaker;

/// <summary>One line of a diarized transcript.</summary>
public sealed record TranscriptLine(string Speaker, string Text);

/// <summary>
/// A synthetic visit: a script standing in for the diarized transcript of a real
/// one, and the answer key a correct note must satisfy. Synthetic because real
/// visits are PHI; the lab never holds any.
/// </summary>
public sealed record Visit
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<TranscriptLine> Transcript { get; init; } = [];
    public AnswerKey Expected { get; init; } = new();

    public static Visit Load(string path) =>
        JsonSerializer.Deserialize<Visit>(File.ReadAllText(path), ClinicalNoteDraft.Json)
        ?? throw new InvalidDataException($"{path}: empty visit");

    public static IReadOnlyList<Visit> LoadAll(string directory) =>
        Directory.GetFiles(directory, "*.json").OrderBy(p => p).Select(Load).ToList();
}

/// <summary>
/// What the note must contain. Items match on tooth and surfaces exactly, and on
/// description by any one keyword -- the wording is the drafter's to choose, the
/// tooth number is not.
/// </summary>
public sealed record AnswerKey
{
    public IReadOnlyList<string> ChiefComplaint { get; init; } = [];
    public IReadOnlyList<KeyItem> Findings { get; init; } = [];
    public IReadOnlyList<KeyItem> ProceduresPerformed { get; init; } = [];
    public IReadOnlyList<KeyPerio> Perio { get; init; } = [];
    public IReadOnlyList<KeyItem> MedicalHistory { get; init; } = [];
    public IReadOnlyList<KeyItem> Plan { get; init; } = [];

    /// <summary>
    /// The transcript is unclear on purpose (an inaudible tooth number, say). The
    /// right draft flags it for the provider instead of guessing.
    /// </summary>
    public bool ReviewFlagRequired { get; init; }

    /// <summary>Small talk the note must not repeat. Each term must occur in the transcript.</summary>
    public IReadOnlyList<string> Forbidden { get; init; } = [];
}

public sealed record KeyItem
{
    public string? Tooth { get; init; }

    /// <summary>
    /// For an item the visit ties to a tooth without saying its number in the same
    /// breath -- sutures at the extraction site, a scan for the implant site. With
    /// <see cref="Tooth"/> null: no tooth, a span, or this tooth all match; any
    /// other single tooth is still an error.
    /// </summary>
    public string? AcceptTooth { get; init; }
    public string? Surfaces { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
}

public sealed record KeyPerio
{
    public string Tooth { get; init; } = "";
    public int DepthMm { get; init; }
}
