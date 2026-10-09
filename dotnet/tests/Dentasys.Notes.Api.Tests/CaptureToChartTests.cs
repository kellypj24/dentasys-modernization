using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Dentasys.CaptureAgent;
using Dentasys.Client;
using Xunit;

namespace Dentasys.Notes.Api.Tests;

/// <summary>
/// The workstation and the service together, over HTTP: the capture agent's
/// spool and uploader on one side, the review client on the other.
/// </summary>
[Collection("api")]
public sealed class CaptureToChartTests : IDisposable
{
    private readonly NotesApiHost _host;
    private readonly Spool _spool;
    private readonly string _spoolDir;

    public CaptureToChartTests(NotesApiHost host)
    {
        _host = host;
        _host.Cloud.Down = false;
        (_spool, _spoolDir) = NotesApiHost.NewSpool();
    }

    public void Dispose() => Directory.Delete(_spoolDir, recursive: true);

    [Fact]
    public async Task A_recorded_visit_reaches_the_chart_and_leaves_nothing_on_the_workstation()
    {
        var id = NotesApiHost.Record(_spool, NotesApiHost.Visit);

        var report = await new Uploader(_spool, _host.Http()).DrainAsync();
        Assert.Equal(1, report.CapturesCompleted);
        Assert.Empty(_spool.Captures());

        await _host.ProcessAsync();
        var review = _host.Review();
        Assert.Contains(await review.QueueAsync(NotesApiHost.Practice), q => q.NoteId == id);

        var note = (await review.GetAsync(id))!;
        Assert.Equal(id, note.NoteId);
        Assert.Equal("cloud:test", note.Source);
        Assert.False(note.IsLocalDraft);
        Assert.Equal("cloud draft", note.Draft!.ChiefComplaint);
        Assert.Contains("cloud draft", note.Text);

        Assert.Equal(SignStatus.Signed, await review.SignAsync(id, note.Version!.Value, "dr.lee", acknowledgedLocalDraft: false));
        await _host.ProcessAsync();
        Assert.Equal(1, _host.ChartCount(id));
    }

    [Fact]
    public async Task With_the_service_unreachable_audio_waits_in_the_spool_and_uploads_later()
    {
        var id = NotesApiHost.Record(_spool, NotesApiHost.Visit);

        using var deadLink = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/"), Timeout = TimeSpan.FromSeconds(5) };
        var offline = await new Uploader(_spool, deadLink).DrainAsync();
        Assert.True(offline.Offline);
        Assert.Equal(_spool.ReadManifest(id).ChunkCount, _spool.PendingChunks(id).Count);

        var online = await new Uploader(_spool, _host.Http()).DrainAsync();
        Assert.False(online.Offline);
        Assert.Equal(1, online.CapturesCompleted);
        Assert.Equal(1, _host.NotesScalar("SELECT COUNT(*) FROM notes.job WHERE note_id = @id AND kind = 'transcribe'", new { id }));
    }

    [Fact]
    public async Task An_agent_killed_mid_upload_resumes_and_every_chunk_arrives_once()
    {
        var id = NotesApiHost.Record(_spool, NotesApiHost.Visit);
        var total = _spool.ReadManifest(id).ChunkCount!.Value;

        // Register plus three chunks get through, then the connection dies.
        var first = await new Uploader(_spool, _host.Http(new CutAfter(4))).DrainAsync();
        Assert.True(first.Offline);
        Assert.Equal(3, first.ChunksSent);
        Assert.Equal(total - 3, _spool.PendingChunks(id).Count);

        // A new agent process, same spool.
        var second = await new Uploader(_spool, _host.Http()).DrainAsync();
        Assert.Equal(total - 3, second.ChunksSent);
        Assert.Equal(1, second.CapturesCompleted);
        Assert.Equal(total, _host.NotesScalar("SELECT COUNT(*) FROM notes.capture_chunk WHERE capture_id = @id", new { id }));
    }

    [Fact]
    public void The_spool_holds_no_readable_audio_or_patient_id()
    {
        NotesApiHost.Record(_spool, NotesApiHost.Visit, patientId: 9876543);
        foreach (var file in Directory.GetFiles(_spoolDir, "*.bin", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("nineteen", text);
            Assert.DoesNotContain("9876543", text);
        }
    }

    [Fact]
    public async Task A_chunk_altered_on_disk_is_not_sent()
    {
        var id = NotesApiHost.Record(_spool, NotesApiHost.Visit);
        var path = Directory.GetFiles(_spoolDir, "chunk-000001.bin", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var report = await new Uploader(_spool, _host.Http()).DrainAsync();
        Assert.Contains(report.Problems, p => p.Contains("chunk 1 failed authentication"));
        Assert.Equal(0, report.CapturesCompleted);
        Assert.Equal(0, _host.NotesScalar("SELECT COUNT(*) FROM notes.capture_chunk WHERE capture_id = @id AND chunk_no = 1", new { id }));
    }

    [Fact]
    public void A_chunk_moved_to_another_position_fails_authentication()
    {
        var id = NotesApiHost.Record(_spool, NotesApiHost.Visit);
        var dir = Path.GetDirectoryName(Directory.GetFiles(_spoolDir, "manifest.bin", SearchOption.AllDirectories).Single())!;
        File.Copy(Path.Combine(dir, "chunk-000000.bin"), Path.Combine(dir, "chunk-000002.bin"), overwrite: true);

        Assert.ThrowsAny<CryptographicException>(() => _spool.ReadChunk(id, 2));
    }

    [Fact]
    public async Task Without_consent_neither_the_agent_nor_the_service_stores_anything()
    {
        var id = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() =>
            _spool.Start(new CaptureManifest(id, NotesApiHost.Practice, 1, null, "DDS1", ConsentRecorded: false, null)));
        Assert.Empty(_spool.Captures());

        var r = await _host.Http().PostAsJsonAsync("captures", new
        {
            captureId = id, practiceId = NotesApiHost.Practice, patientId = 1, providerCode = "DDS1", consentRecorded = false,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
    }

    [Fact]
    public async Task A_chunk_for_a_capture_the_service_never_saw_is_refused()
    {
        var r = await _host.Http().PutAsync($"captures/{Guid.NewGuid()}/chunks/0", new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task A_local_model_draft_cannot_be_signed_without_saying_so()
    {
        _host.Cloud.Down = true;
        var id = NotesApiHost.Record(_spool, NotesApiHost.Visit);
        await new Uploader(_spool, _host.Http()).DrainAsync();
        await _host.ProcessAsync();

        var review = _host.Review();
        var note = (await review.GetAsync(id))!;
        Assert.True(note.IsLocalDraft);

        Assert.Equal(SignStatus.NeedsLocalDraftAcknowledgement,
            await review.SignAsync(id, note.Version!.Value, "dr.lee", acknowledgedLocalDraft: false));
        Assert.Equal(SignStatus.Signed,
            await review.SignAsync(id, note.Version!.Value, "dr.lee", acknowledgedLocalDraft: true));
    }

    [Fact]
    public async Task Signing_a_version_someone_else_replaced_is_refused()
    {
        var id = NotesApiHost.Record(_spool, NotesApiHost.Visit);
        await new Uploader(_spool, _host.Http()).DrainAsync();
        await _host.ProcessAsync();

        var review = _host.Review();
        var seen = (await review.GetAsync(id))!;
        var edit = await _host.Http().PutAsJsonAsync($"notes/{id}/draft",
            new { baseVersion = seen.Version, draft = seen.Draft! with { ChiefComplaint = "edited elsewhere" }, actor = "dr.kim" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        Assert.Equal(SignStatus.ChangedSinceOpened,
            await review.SignAsync(id, seen.Version!.Value, "dr.lee", acknowledgedLocalDraft: false));
    }
}
