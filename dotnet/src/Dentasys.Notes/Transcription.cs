using System.Text;
using System.Text.Json;
using Dentasys.Notetaker;

namespace Dentasys.Notes;

/// <summary>Audio chunks in, diarized transcript out. The cloud speech service sits behind this in production.</summary>
public interface ITranscriber
{
    Task<IReadOnlyList<TranscriptLine>> TranscribeAsync(IReadOnlyList<byte[]> chunks, CancellationToken ct = default);
}

/// <summary>
/// The lab's stand-in for speech-to-text: the "audio" is a synthetic visit's
/// transcript as UTF-8 JSON, split across chunks. It exercises every hand-off
/// the real audio takes -- chunking, re-sends, reassembly -- with no recording.
/// </summary>
public sealed class FixtureTranscriber : ITranscriber
{
    public Task<IReadOnlyList<TranscriptLine>> TranscribeAsync(IReadOnlyList<byte[]> chunks, CancellationToken ct = default)
    {
        var json = Encoding.UTF8.GetString(chunks.SelectMany(c => c).ToArray());
        var lines = JsonSerializer.Deserialize<List<TranscriptLine>>(json, ClinicalNoteDraft.Json)
                    ?? throw new InvalidDataException("empty transcript");
        return Task.FromResult<IReadOnlyList<TranscriptLine>>(lines);
    }

    /// <summary>Splits a transcript into fake audio chunks, as the capture agent would upload them.</summary>
    public static IReadOnlyList<byte[]> ToChunks(IReadOnlyList<TranscriptLine> transcript, int chunkBytes = 256) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(transcript, ClinicalNoteDraft.Json))
            .Chunk(chunkBytes).ToList();
}
