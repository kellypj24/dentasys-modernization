using Dentasys.Notetaker;
using Xunit;

namespace Dentasys.Notes.Tests;

/// <summary>
/// The notes service against the real store, with the cloud cut and restored on
/// purpose. Each test is one scenario from docs/NOTETAKER.md "What happens when a
/// link is down".
/// </summary>
[Collection("notes")]
public sealed class NotesLifecycleTests
{
    private readonly NotesDb _db;
    private readonly ManualClock _clock = new();
    private readonly SwitchableDrafter _cloud = new("cloud:test", "cloud draft");
    private readonly SwitchableDrafter _local = new("local:test", "local draft");
    private readonly NotesService _service;
    private readonly ChartWriter _chart;

    public NotesLifecycleTests(NotesDb db)
    {
        _db = db;
        _service = new NotesService(db.Options, _clock);
        _chart = new ChartWriter(db.Options, _clock);
    }

    private JobRunner Runner(bool withLocal) => new(_db.Options, new FixtureTranscriber(), _cloud, withLocal ? _local : null, _clock);

    [Fact]
    public async Task A_visit_goes_from_audio_to_the_chart()
    {
        var id = await _db.CaptureAsync(_service, NotesDb.Upgraded);
        await Runner(withLocal: false).RunUntilIdleAsync();

        var note = (await _service.GetAsync(id))!;
        Assert.Equal(NoteState.Drafted, note.State);
        Assert.Equal("cloud:test", note.CurrentSource);

        var edit = await _service.SaveEditAsync(id, note.CurrentVersion!.Value,
            note.Current! with { ChiefComplaint = "decay #19, edited" }, "dr.lee");
        Assert.Equal(EditResult.Saved, edit.Result);
        Assert.Equal(EditResult.Saved, (await _service.SignAsync(id, edit.Version!.Value, "dr.lee")).Result);

        Assert.Equal(1, await _chart.RunUntilIdleAsync());
        Assert.Equal(NoteState.Charted, (await _service.GetAsync(id))!.State);
        Assert.Equal("dr.lee", _db.PracticeScalar<string>(NotesDb.Upgraded,
            "SELECT SIGNED_BY FROM CLINICAL_NOTE WHERE NOTE_ID = @id", new { id }));
        Assert.Contains("decay #19, edited", _db.PracticeScalar<string>(NotesDb.Upgraded,
            "SELECT NOTE_TEXT FROM CLINICAL_NOTE WHERE NOTE_ID = @id", new { id }));
    }

    [Fact]
    public async Task Resent_chunks_are_acknowledged_once_and_missing_ones_are_named()
    {
        var id = Guid.NewGuid();
        await _service.RegisterCaptureAsync(new CaptureRegistration(id, NotesDb.Upgraded, 41701, null, "DDS1", true));
        await _service.RegisterCaptureAsync(new CaptureRegistration(id, NotesDb.Upgraded, 41701, null, "DDS1", true));
        var chunks = FixtureTranscriber.ToChunks(NotesDb.Transcript, chunkBytes: 64);

        Assert.True(await _service.PutChunkAsync(id, 0, chunks[0]));
        Assert.False(await _service.PutChunkAsync(id, 0, chunks[0]));   // the agent re-sent after a timeout

        var partial = await _service.CompleteUploadAsync(id, chunks.Count);
        Assert.False(partial.Complete);
        Assert.Equal(Enumerable.Range(1, chunks.Count - 1), partial.MissingChunks);
        Assert.Equal(NoteState.AwaitingAudio, (await _service.GetAsync(id))!.State);

        for (var i = 1; i < chunks.Count; i++) await _service.PutChunkAsync(id, i, chunks[i]);
        Assert.True((await _service.CompleteUploadAsync(id, chunks.Count)).Complete);
        Assert.True((await _service.CompleteUploadAsync(id, chunks.Count)).Complete);
        Assert.Equal(1, _db.Scalar<int>("SELECT COUNT(*) FROM notes.job WHERE note_id = @id", new { id }));
        await Runner(false).RunUntilIdleAsync();
    }

    [Fact]
    public async Task No_consent_means_nothing_is_stored()
    {
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.RegisterCaptureAsync(new CaptureRegistration(id, NotesDb.Upgraded, 41701, null, "DDS1", false)));
        Assert.Null(await _service.GetAsync(id));
    }

    [Fact]
    public async Task Cloud_down_with_no_local_model_queues_and_waits_then_converges()
    {
        var id = await _db.CaptureAsync(_service, NotesDb.Upgraded);
        _cloud.Down = true;
        var runner = Runner(withLocal: false);

        await runner.RunUntilIdleAsync();
        Assert.Equal(NoteState.Drafting, (await _service.GetAsync(id))!.State);
        Assert.Contains("no route to host", _db.Scalar<string>(
            "SELECT last_error FROM notes.job WHERE note_id = @id AND kind = 'draft'", new { id }));

        // Backoff: nothing is due until the delay passes, so a dead link is not hammered.
        Assert.Equal(0, await runner.RunUntilIdleAsync());
        _clock.Advance(_db.Options.RetryDelay(1));
        await runner.RunUntilIdleAsync();
        Assert.Equal(2, _db.Scalar<int>("SELECT attempts FROM notes.job WHERE note_id = @id AND kind = 'draft'", new { id }));

        _cloud.Down = false;
        _clock.Advance(_db.Options.RetryCap);
        await runner.RunUntilIdleAsync();
        var note = (await _service.GetAsync(id))!;
        Assert.Equal(NoteState.Drafted, note.State);
        Assert.Equal("cloud:test", note.CurrentSource);
    }

    [Fact]
    public async Task Cloud_down_drafts_locally_then_an_untouched_local_draft_is_replaced_by_the_cloud()
    {
        var id = await _db.CaptureAsync(_service, NotesDb.Upgraded);
        _cloud.Down = true;
        var runner = Runner(withLocal: true);

        await runner.RunUntilIdleAsync();
        var local = (await _service.GetAsync(id))!;
        Assert.Equal(NoteState.Drafted, local.State);
        Assert.Equal("local:test", local.CurrentSource);

        _cloud.Down = false;
        _clock.Advance(_db.Options.RetryCap);
        await runner.RunUntilIdleAsync();
        var cloud = (await _service.GetAsync(id))!;
        Assert.Equal("cloud:test", cloud.CurrentSource);
        Assert.Equal(2, cloud.Versions);
    }

    [Fact]
    public async Task A_cloud_draft_that_arrives_after_the_provider_edited_is_stored_not_shown()
    {
        var id = await _db.CaptureAsync(_service, NotesDb.Upgraded);
        _cloud.Down = true;
        var runner = Runner(withLocal: true);
        await runner.RunUntilIdleAsync();

        var local = (await _service.GetAsync(id))!;
        await _service.SaveEditAsync(id, local.CurrentVersion!.Value, local.Current! with { ChiefComplaint = "mine" }, "dr.lee");

        _cloud.Down = false;
        _clock.Advance(_db.Options.RetryCap);
        await runner.RunUntilIdleAsync();

        var after = (await _service.GetAsync(id))!;
        Assert.Equal("human:dr.lee", after.CurrentSource);
        Assert.Equal("mine", after.Current!.ChiefComplaint);
        Assert.Equal(1, after.UnappliedVersions);
    }

    [Fact]
    public async Task A_redraft_for_a_note_signed_while_the_cloud_was_down_is_abandoned_and_the_signature_stands()
    {
        var id = await _db.CaptureAsync(_service, NotesDb.Upgraded);
        _cloud.Down = true;
        var runner = Runner(withLocal: true);
        await runner.RunUntilIdleAsync();

        var local = (await _service.GetAsync(id))!;
        await _service.SignAsync(id, local.CurrentVersion!.Value, "dr.lee");

        _cloud.Down = false;
        _clock.Advance(_db.Options.RetryCap);
        var callsBefore = _cloud.Calls;
        await runner.RunUntilIdleAsync();

        Assert.Equal(callsBefore, _cloud.Calls);
        Assert.Equal("abandoned", _db.Scalar<string>("SELECT state FROM notes.job WHERE note_id = @id AND kind = 'redraft'", new { id }));
        Assert.Equal("local:test", (await _service.GetAsync(id))!.CurrentSource);
        await _chart.RunUntilIdleAsync();
    }

    [Fact]
    public async Task An_edit_based_on_a_stale_version_is_rejected_not_merged()
    {
        var id = await _db.CaptureAsync(_service, NotesDb.Upgraded);
        await Runner(false).RunUntilIdleAsync();
        var v1 = (await _service.GetAsync(id))!;

        var first = await _service.SaveEditAsync(id, v1.CurrentVersion!.Value, v1.Current! with { ChiefComplaint = "a" }, "dr.lee");
        var second = await _service.SaveEditAsync(id, v1.CurrentVersion!.Value, v1.Current! with { ChiefComplaint = "b" }, "dr.kim");

        Assert.Equal(EditResult.Saved, first.Result);
        Assert.Equal(EditResult.Conflict, second.Result);
        Assert.Equal("a", (await _service.GetAsync(id))!.Current!.ChiefComplaint);
        Assert.Equal(EditResult.Conflict, (await _service.SignAsync(id, v1.CurrentVersion!.Value, "dr.kim")).Result);
    }

    [Fact]
    public async Task A_signed_note_cannot_be_edited_only_amended_and_the_addendum_reaches_the_chart()
    {
        var id = await _db.CaptureAsync(_service, NotesDb.Upgraded);
        await Runner(false).RunUntilIdleAsync();
        var note = (await _service.GetAsync(id))!;
        await _service.SignAsync(id, note.CurrentVersion!.Value, "dr.lee");

        Assert.Equal(EditResult.NotEditable,
            (await _service.SaveEditAsync(id, note.CurrentVersion!.Value, note.Current!, "dr.lee")).Result);

        var addendum = await _service.AddAddendumAsync(id, "Patient called: sensitivity resolved.", "dr.lee");
        Assert.NotNull(addendum);
        Assert.Equal(2, await _chart.RunUntilIdleAsync());
        Assert.Equal(id, _db.PracticeScalar<Guid>(NotesDb.Upgraded,
            "SELECT PARENT_NOTE_ID FROM CLINICAL_NOTE WHERE NOTE_ID = @addendum", new { addendum }));
    }

    [Fact]
    public async Task A_practice_without_the_upgrade_holds_the_note_until_the_table_exists()
    {
        var id = await _db.CaptureAsync(_service, NotesDb.NotUpgraded);
        await Runner(false).RunUntilIdleAsync();
        var note = (await _service.GetAsync(id))!;
        await _service.SignAsync(id, note.CurrentVersion!.Value, "dr.lee");

        await _chart.RunUntilIdleAsync();
        Assert.Equal(NoteState.Signed, (await _service.GetAsync(id))!.State);
        Assert.Contains("has not installed 07.04.00",
            _db.Scalar<string>("SELECT last_error FROM notes.chart_write WHERE item_id = @id", new { id }));

        // The practice's upgrade window comes round.
        var upgrade = File.ReadAllText(Path.Combine(RepoRoot(), "legacy", "upgrades", "07.04.00_clinical_note.sql"));
        _db.PracticeExecute(NotesDb.NotUpgraded, upgrade.Replace("\nGO", "\n"));
        try
        {
            Assert.Equal(0, await _chart.RunUntilIdleAsync());   // still inside the hold
            _clock.Advance(_db.Options.ChartHold);
            Assert.Equal(1, await _chart.RunUntilIdleAsync());
            Assert.Equal(NoteState.Charted, (await _service.GetAsync(id))!.State);
            Assert.Equal(1, _db.PracticeScalar<int>(NotesDb.NotUpgraded,
                "SELECT COUNT(*) FROM CLINICAL_NOTE WHERE NOTE_ID = @id", new { id }));
        }
        finally
        {
            _db.PracticeExecute(NotesDb.NotUpgraded, "DROP TABLE IF EXISTS CLINICAL_NOTE");
        }
    }

    [Fact]
    public async Task Delivering_the_same_note_twice_writes_one_chart_row()
    {
        var id = await _db.CaptureAsync(_service, NotesDb.Upgraded);
        await Runner(false).RunUntilIdleAsync();
        await _service.SignAsync(id, (await _service.GetAsync(id))!.CurrentVersion!.Value, "dr.lee");

        // The worker writes the row and dies before marking it delivered...
        var row = await _chart.LoadAsync(id);
        await _chart.WriteToChartAsync(row);
        // ...so the outbox still says pending, and the next worker delivers again.
        Assert.Equal(1, await _chart.RunUntilIdleAsync());

        Assert.Equal(NoteState.Charted, (await _service.GetAsync(id))!.State);
        Assert.Equal(1, _db.PracticeScalar<int>(NotesDb.Upgraded,
            "SELECT COUNT(*) FROM CLINICAL_NOTE WHERE NOTE_ID = @id", new { id }));
    }

    [Fact]
    public async Task A_worker_whose_lease_expired_cannot_record_a_result()
    {
        var id = await _db.CaptureAsync(_service, NotesDb.Upgraded);
        var transcribe = new JobRunner(_db.Options, new FixtureTranscriber(), _cloud, null, _clock);
        Assert.True(await transcribe.RunOnceAsync());            // transcription done; the draft job is due

        var slow = new BlockingDrafter("cloud:slow");
        var stuck = new JobRunner(_db.Options, new FixtureTranscriber(), slow, null, _clock).RunOnceAsync();
        await slow.Entered.Task;                                 // worker A holds the draft job, mid-call

        _clock.Advance(_db.Options.Lease + TimeSpan.FromSeconds(1));
        Assert.True(await Runner(false).RunOnceAsync());         // worker B takes the expired lease and finishes

        slow.Release();                                          // A's call finally returns
        await stuck;

        var note = (await _service.GetAsync(id))!;
        Assert.Equal("cloud:test", note.CurrentSource);
        Assert.Equal(1, note.Versions);                          // A's late result was refused, not added
    }

    [Fact]
    public async Task Two_workers_never_claim_the_same_outbox_row()
    {
        var id = await _db.CaptureAsync(_service, NotesDb.Upgraded);
        await Runner(false).RunUntilIdleAsync();
        await _service.SignAsync(id, (await _service.GetAsync(id))!.CurrentVersion!.Value, "dr.lee");

        // Two workers race for the same outbox row: only one claim succeeds.
        var a = new ChartWriter(_db.Options, _clock);
        var b = new ChartWriter(_db.Options, _clock);
        var results = await Task.WhenAll(a.RunOnceAsync(), b.RunOnceAsync());
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(1, _db.Scalar<int>("SELECT attempts FROM notes.chart_write WHERE item_id = @id", new { id }));
    }

    [Fact]
    public async Task The_audit_log_records_who_did_what_without_any_note_text()
    {
        var id = await _db.CaptureAsync(_service, NotesDb.Upgraded);
        await Runner(false).RunUntilIdleAsync();
        await _service.SignAsync(id, (await _service.GetAsync(id))!.CurrentVersion!.Value, "dr.lee");
        await _chart.RunUntilIdleAsync();

        var details = _db.Scalar<string>(
            "SELECT STRING_AGG(action + ':' + ISNULL(detail, ''), '|') FROM notes.audit WHERE note_id = @id", new { id });
        Assert.Contains("signed", details);
        Assert.Contains("charted", details);
        Assert.DoesNotContain("nineteen", details);
        Assert.DoesNotContain("cloud draft", details);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "justfile"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
