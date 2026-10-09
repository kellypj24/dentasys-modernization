using System.Net;
using System.Net.Http.Json;
using Dentasys.CaptureAgent;
using Dentasys.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Dentasys.Notes.Api.Tests;

/// <summary>
/// Who may do what. Every rule here is one a body field used to decide, or that
/// nothing decided at all.
/// </summary>
[Collection("api")]
public sealed class AuthTests : IDisposable
{
    private const string OtherPractice = "001505";
    private readonly NotesApiHost _host;
    private readonly Spool _spool;
    private readonly string _spoolDir;

    public AuthTests(NotesApiHost host)
    {
        _host = host;
        _host.Cloud.Down = false;
        (_spool, _spoolDir) = NotesApiHost.NewSpool();
    }

    public void Dispose() => Directory.Delete(_spoolDir, recursive: true);

    private async Task<Guid> DraftedNoteAsync()
    {
        var id = NotesApiHost.Record(_spool, NotesApiHost.Visit);
        await new Uploader(_spool, _host.AgentHttp()).DrainAsync();
        await _host.ProcessAsync();
        return id;
    }

    [Fact]
    public async Task Without_a_token_nothing_answers()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Http().GetAsync($"practices/{NotesApiHost.Practice}/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Http().PutAsync($"captures/{Guid.NewGuid()}/chunks/0", new ByteArrayContent([1]))).StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_rejected()
    {
        var forged = _host.Http();
        forged.DefaultRequestHeaders.Authorization = new("Bearer",
            Auth.IssueLabToken("some-other-key-that-is-long-enough-000", "dr.lee", "clinician", NotesApiHost.Practice, "DDS1"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync($"practices/{NotesApiHost.Practice}/notes")).StatusCode);
    }

    [Fact]
    public async Task A_workstation_cannot_upload_into_another_practice()
    {
        var id = NotesApiHost.Record(_spool, NotesApiHost.Visit);

        // Registering a capture for a practice the token does not name...
        var report = await new Uploader(_spool, _host.AgentHttp(OtherPractice)).DrainAsync();
        Assert.Equal(0, report.CapturesCompleted);
        Assert.Equal(0, _host.NotesScalar("SELECT COUNT(*) FROM notes.capture WHERE capture_id = @id", new { id }));

        // ...and adding chunks to another practice's capture, once it exists.
        await new Uploader(_spool, _host.AgentHttp()).DrainAsync();
        var put = await _host.AgentHttp(OtherPractice).PutAsync($"captures/{id}/chunks/99", new ByteArrayContent([1]));
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task Agents_cannot_review_and_clinicians_cannot_upload()
    {
        var id = await DraftedNoteAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.AgentHttp().GetAsync($"notes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _host.ClinicianHttp().PutAsync($"captures/{id}/chunks/99", new ByteArrayContent([1]))).StatusCode);
    }

    [Fact]
    public async Task A_clinician_sees_only_their_own_practice()
    {
        var id = await DraftedNoteAsync();
        var outsider = _host.ClinicianHttp("dr.out", "DDS1", OtherPractice);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"practices/{NotesApiHost.Practice}/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"notes/{id}")).StatusCode);
    }

    [Fact]
    public async Task Another_dentist_can_edit_but_only_the_notes_provider_can_sign()
    {
        var id = await DraftedNoteAsync();
        var kim = _host.Review("dr.kim", "DDS2");
        var note = (await kim.GetAsync(id))!;
        Assert.Equal(SignStatus.NotYourNote, await kim.SignAsync(id, note.Version!.Value, acknowledgedLocalDraft: false));
        Assert.Equal(SignStatus.Signed, await _host.Review().SignAsync(id, note.Version!.Value, acknowledgedLocalDraft: false));
    }

    [Fact]
    public async Task The_signer_recorded_is_the_token_not_anything_in_the_body()
    {
        var id = await DraftedNoteAsync();
        var note = (await _host.Review().GetAsync(id))!;

        // A body that claims to be someone else is ignored, not trusted.
        var r = await _host.ClinicianHttp().PostAsJsonAsync($"notes/{id}/sign",
            new { version = note.Version, acknowledgedLocalDraft = false, actor = "dr.mallory" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        await _host.ProcessAsync();
        Assert.Equal("dr.lee", _host.ChartSigner(id));
    }

    [Fact]
    public async Task Audio_the_transcriber_cannot_read_is_refused_before_upload()
    {
        var r = await _host.AgentHttp().PostAsJsonAsync("captures", new
        {
            captureId = Guid.NewGuid(), practiceId = NotesApiHost.Practice, patientId = 1, providerCode = "DDS1",
            consentRecorded = true, audioFormat = "audio/x-unknown",
        });
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.StatusCode);
    }

    [Fact]
    public void The_service_refuses_to_start_with_no_authentication_configured()
    {
        using var unconfigured = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Notes:Workers", "false"));
        var ex = Assert.ThrowsAny<Exception>(() => unconfigured.CreateClient());
        Assert.Contains("no authentication configured", ex.ToString());
    }
}
