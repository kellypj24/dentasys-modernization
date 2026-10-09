using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace Dentasys.CaptureAgent;

public sealed record DrainReport(int ChunksSent, int CapturesCompleted, bool Offline, IReadOnlyList<string> Problems);

/// <summary>
/// Empties the spool into the notes service. Run it on a timer; every pass picks
/// up where the last one stopped, including after the agent itself was killed,
/// because all of its state is the spool on disk.
///
/// The first network failure ends the pass. A dead link is retried on the next
/// timer tick, not hammered in a loop; nothing is lost in between because nothing
/// leaves the spool until the server has acknowledged it.
///
/// The HttpClient carries the workstation's own token (role capture_agent, one
/// practice). A provider's sign-in is never used for uploads: the agent runs
/// whether or not anyone is signed in at that desk.
/// </summary>
public sealed class Uploader
{
    private readonly Spool _spool;
    private readonly HttpClient _http;

    public Uploader(Spool spool, HttpClient http)
    {
        _spool = spool;
        _http = http;
    }

    public async Task<DrainReport> DrainAsync(CancellationToken ct = default)
    {
        int sent = 0, completed = 0;
        var problems = new List<string>();

        foreach (var id in _spool.Captures())
        {
            try
            {
                var m = _spool.ReadManifest(id);
                (await _http.PostAsJsonAsync("captures", new
                {
                    m.CaptureId, m.PracticeId, m.PatientId, m.ApptId, m.ProviderCode, m.ConsentRecorded, m.AudioFormat,
                }, ct)).EnsureSuccessStatusCode();

                foreach (var n in _spool.PendingChunks(id))
                {
                    byte[] audio;
                    try { audio = _spool.ReadChunk(id, n); }
                    catch (CryptographicException)
                    {
                        // Altered on disk or moved between captures. Sending it would put
                        // the wrong audio in someone's chart; leaving it is the safe failure.
                        problems.Add($"{id}: chunk {n} failed authentication; not sent");
                        goto nextCapture;
                    }

                    var put = await _http.PutAsync($"captures/{id}/chunks/{n}", new ByteArrayContent(audio), ct);
                    if (put.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
                    {
                        _spool.Acknowledge(id, n);
                        sent++;
                    }
                    else
                    {
                        problems.Add($"{id}: chunk {n} -> {(int)put.StatusCode}");
                        goto nextCapture;
                    }
                }

                if (m.ChunkCount is { } count && _spool.PendingChunks(id).Count == 0)
                {
                    var done = await (await _http.PostAsJsonAsync($"captures/{id}/complete", new { chunkCount = count }, ct))
                        .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CompleteResponse>(ct);
                    if (done!.Complete)
                    {
                        _spool.Remove(id);
                        completed++;
                    }
                    else
                    {
                        problems.Add($"{id}: server is missing chunks {string.Join(",", done.MissingChunks)} the agent no longer holds");
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new DrainReport(sent, completed, Offline: true, problems);
            }
            nextCapture:;
        }
        return new DrainReport(sent, completed, Offline: false, problems);
    }

    private sealed record CompleteResponse(bool Complete, IReadOnlyList<int> MissingChunks);
}
