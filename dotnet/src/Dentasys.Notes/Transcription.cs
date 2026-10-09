using System.Text;
using System.Text.Json;
using Dentasys.Notetaker;

namespace Dentasys.Notes;

/// <summary>Recorded audio as the capture agent uploaded it, with the format it declared at registration.</summary>
public sealed record AudioInput(string Format, IReadOnlyList<byte[]> Chunks);

/// <summary>
/// One diarized stretch of speech. Speaker is whatever the engine and role
/// attribution produced: "DENTIST" when roles are resolved, "Speaker 2" when not.
/// Confidence is the engine's own, 0 to 1.
/// </summary>
public sealed record TranscriptSegment(string Speaker, int StartMs, int EndMs, string Text, double Confidence);

/// <summary>A speech-to-text result, and what the drafter is allowed to see of it.</summary>
public sealed record Transcription(string Engine, IReadOnlyList<TranscriptSegment> Segments)
{
    /// <summary>Below this the words are a guess, and a guess must not reach a note as fact.</summary>
    public const double UnclearBelow = 0.6;

    public IReadOnlyList<TranscriptSegment> Unclear => Segments.Where(s => s.Confidence < UnclearBelow).ToList();

    /// <summary>
    /// The transcript the drafter sees: unclear speech is marked as unclear rather
    /// than presented as what was said, so a tooth number the engine half-heard
    /// arrives as a question, not a fact.
    /// </summary>
    public IReadOnlyList<TranscriptLine> ForDrafter() =>
        Segments.Select(s => new TranscriptLine(s.Speaker,
            s.Confidence < UnclearBelow ? $"[unclear, {s.Confidence:P0} confidence] {s.Text}" : s.Text)).ToList();

    /// <summary>
    /// Review flags the service adds to every draft of this visit, whatever the
    /// model wrote. Whether a draft mentions an unclear passage is up to the model;
    /// whether the provider is told about it is not.
    /// </summary>
    public IReadOnlyList<string> ReviewFlags() =>
        Unclear.Select(s => $"Unclear speech at {TimeSpan.FromMilliseconds(s.StartMs):mm\\:ss} ({s.Speaker}): " +
                            $"\"{s.Text}\". Check against your own recollection before signing.").ToList();
}

/// <summary>
/// Audio in, diarized transcript out. A cloud speech service (Azure AI Speech,
/// AWS Transcribe) sits behind this in production; the contract is what keeps
/// that a configuration change rather than a rewrite.
/// </summary>
public interface ITranscriber
{
    /// <summary>The audio formats this engine accepts. Captures in anything else are refused at registration.</summary>
    IReadOnlyCollection<string> SupportedFormats { get; }

    Task<Transcription> TranscribeAsync(AudioInput audio, CancellationToken ct = default);
}

/// <summary>
/// The lab's stand-in for speech-to-text: the "audio" is a synthetic visit's
/// transcript as UTF-8 JSON, split across chunks. It exercises every hand-off
/// real audio takes -- chunking, re-sends, reassembly -- and returns the same
/// shape a real engine does: timed, diarized segments with confidence.
///
/// Speakers come back already labelled by role. A real diarizer returns
/// "Speaker 1, Speaker 2"; attributing those to dentist, hygienist and patient
/// is its own problem, listed in docs/NOTETAKER.md.
/// </summary>
public sealed class FixtureTranscriber : ITranscriber
{
    public const string Format = "application/x-dentasys-transcript+json";

    public IReadOnlyCollection<string> SupportedFormats { get; } = [Format];

    public Task<Transcription> TranscribeAsync(AudioInput audio, CancellationToken ct = default)
    {
        if (audio.Format != Format) throw new NotSupportedException($"audio format {audio.Format}");
        var json = Encoding.UTF8.GetString(audio.Chunks.SelectMany(c => c).ToArray());
        var lines = JsonSerializer.Deserialize<List<TranscriptLine>>(json, ClinicalNoteDraft.Json)
                    ?? throw new InvalidDataException("empty transcript");

        // Stand-in timings, about three seconds a line; the scripts' own markers
        // for what a microphone would have lost become low confidence.
        var segments = lines.Select((l, i) => new TranscriptSegment(
            l.Speaker, i * 3000, i * 3000 + 2800, l.Text,
            l.Text.Contains("[inaudible]") || l.Text.Contains("[crosstalk]") ? 0.35 : 0.97)).ToList();
        return Task.FromResult(new Transcription("fixture", segments));
    }

    /// <summary>Splits a transcript into fake audio chunks, as the capture agent would upload them.</summary>
    public static IReadOnlyList<byte[]> ToChunks(IReadOnlyList<TranscriptLine> transcript, int chunkBytes = 256) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(transcript, ClinicalNoteDraft.Json))
            .Chunk(chunkBytes).ToList();
}
