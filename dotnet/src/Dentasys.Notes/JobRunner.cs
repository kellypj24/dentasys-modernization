using System.Text.Json;
using Dapper;
using Dentasys.Notetaker;
using Microsoft.Data.SqlClient;

namespace Dentasys.Notes;

/// <summary>
/// Runs transcription and drafting jobs, one claim at a time.
///
/// Drafting prefers the cloud. When the cloud fails and a local drafter is
/// configured, the note is drafted locally so the provider is not left waiting,
/// and a redraft job asks the cloud again later. A cloud draft only replaces a
/// local one the provider has not touched; anything that arrives after an edit
/// or a signature is stored and never shown in its place. Without a local
/// drafter the job simply backs off and retries: "queue and wait".
/// </summary>
public sealed class JobRunner
{
    private readonly NotesOptions _options;
    private readonly ITranscriber _transcriber;
    private readonly INoteDrafter _cloud;
    private readonly INoteDrafter? _local;
    private readonly TimeProvider _clock;

    public JobRunner(NotesOptions options, ITranscriber transcriber, INoteDrafter cloud, INoteDrafter? local, TimeProvider clock)
    {
        _options = options;
        _transcriber = transcriber;
        _cloud = cloud;
        _local = local;
        _clock = clock;
    }

    /// <summary>Claims and runs one due job. False when nothing is due.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_options.NotesConnectionString);
        await conn.OpenAsync(ct);

        var job = await Lease.ClaimAsync(conn, "job", _clock.GetUtcNow(), _options.Lease, ct);
        if (job is null) return false;

        try
        {
            switch (job.Kind)
            {
                case "transcribe": await TranscribeAsync(conn, job, ct); break;
                default: await DraftAsync(conn, job, ct); break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Lease.RetryAsync(conn, "job", job, $"{ex.GetType().Name}: {ex.Message}",
                                   _clock.GetUtcNow() + _options.RetryDelay(job.Attempts), ct);
        }
        return true;
    }

    /// <summary>Runs jobs until none is due. For tests and for a scheduled worker run.</summary>
    public async Task<int> RunUntilIdleAsync(CancellationToken ct = default)
    {
        var n = 0;
        while (await RunOnceAsync(ct)) n++;
        return n;
    }

    private async Task TranscribeAsync(SqlConnection conn, Claimed job, CancellationToken ct)
    {
        var chunks = (await conn.QueryAsync<byte[]>(new CommandDefinition(
            "SELECT content FROM notes.capture_chunk WHERE capture_id = @NoteId ORDER BY chunk_no",
            new { job.NoteId }, cancellationToken: ct))).ToList();
        var transcript = await _transcriber.TranscribeAsync(chunks, ct);

        await using var tx = conn.BeginTransaction();
        var now = _clock.GetUtcNow();
        if (!await Lease.FinishAsync(conn, tx, "job", job, "done", now, ct)) { await tx.RollbackAsync(ct); return; }

        var moved = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE notes.note SET transcript = @transcript, state = 'drafting', updated_at = @now
             WHERE note_id = @NoteId AND state = 'transcribing'
            """, new { job.NoteId, now, transcript = JsonSerializer.Serialize(transcript, ClinicalNoteDraft.Json) },
            tx, cancellationToken: ct));
        if (moved == 1)
        {
            await Jobs.EnqueueAsync(conn, tx, job.NoteId, "draft", now, ct);
            await Audit.WriteAsync(conn, tx, job.NoteId, now, "notes-service", "transcribed", $"{transcript.Count} lines", ct);
        }
        await tx.CommitAsync(ct);
    }

    private async Task DraftAsync(SqlConnection conn, Claimed job, CancellationToken ct)
    {
        var note = await conn.QuerySingleAsync<DraftInput>(new CommandDefinition(
            "SELECT state AS State, transcript AS Transcript FROM notes.note WHERE note_id = @NoteId",
            new { job.NoteId }, cancellationToken: ct));

        // A redraft for a note already signed has nothing left to improve.
        if (job.Kind == "redraft" && NoteState.IsSigned(note.State))
        {
            await FinishAloneAsync(conn, job, "abandoned", "note signed before the cloud answered", ct);
            return;
        }

        var transcript = JsonSerializer.Deserialize<List<TranscriptLine>>(note.Transcript ?? "[]", ClinicalNoteDraft.Json)!;
        var cloud = await _cloud.DraftAsync(transcript, ct);
        if (cloud.Draft is not null)
        {
            await RecordDraftAsync(conn, job, $"cloud:{_cloud.Source}", cloud.Draft, isCloud: true, ct);
            return;
        }

        if (job.Kind == "draft" && _local is not null)
        {
            var local = await _local.DraftAsync(transcript, ct);
            if (local.Draft is not null)
            {
                // The role is recorded, not the drafter's own name: the review screen and
                // the sign gate key on "local:", and a model named anything at all must not
                // slip past them by being configured as the fallback.
                await RecordDraftAsync(conn, job, $"local:{_local.Source}", local.Draft, isCloud: false, ct);
                return;
            }
            cloud = cloud with { Error = $"{cloud.Error}; local: {local.Error}" };
        }

        await Lease.RetryAsync(conn, "job", job, $"cloud: {cloud.Error}",
                               _clock.GetUtcNow() + _options.RetryDelay(job.Attempts), ct);
    }

    private async Task RecordDraftAsync(SqlConnection conn, Claimed job, string source, ClinicalNoteDraft draft,
                                        bool isCloud, CancellationToken ct)
    {
        await using var tx = conn.BeginTransaction();
        var now = _clock.GetUtcNow();
        if (!await Lease.FinishAsync(conn, tx, "job", job, "done", now, ct)) { await tx.RollbackAsync(ct); return; }

        var note = (await NotesService.LockNoteAsync(conn, tx, job.NoteId, ct))!;
        var currentSource = note.CurrentVersion is null ? null : await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT source FROM notes.note_version WHERE note_id = @NoteId AND version = @CurrentVersion",
            new { job.NoteId, note.CurrentVersion }, tx, cancellationToken: ct));

        // Shown only if nobody is looking at anything better: the note is still
        // waiting for its first draft, or a cloud draft replaces an untouched local one.
        var apply = note.State == NoteState.Drafting
                    || (isCloud && note.State == NoteState.Drafted && currentSource?.StartsWith("local:") == true);

        var version = await Versions.InsertAsync(conn, tx, job.NoteId, source, draft, job.Id, apply, now, ct);
        if (apply)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE notes.note SET current_version = @version, state = 'drafted', updated_at = @now WHERE note_id = @NoteId
                """, new { job.NoteId, version, now }, tx, cancellationToken: ct));
        }
        if (!isCloud) await Jobs.EnqueueAsync(conn, tx, job.NoteId, "redraft", now + _options.RetryDelay(1), ct);

        await Audit.WriteAsync(conn, tx, job.NoteId, now, "notes-service",
            apply ? (isCloud ? "drafted" : "drafted_locally") : "late_draft_stored", $"v{version} {source}", ct);
        await tx.CommitAsync(ct);
    }

    private async Task FinishAloneAsync(SqlConnection conn, Claimed job, string state, string reason, CancellationToken ct)
    {
        await using var tx = conn.BeginTransaction();
        var now = _clock.GetUtcNow();
        if (await Lease.FinishAsync(conn, tx, "job", job, state, now, ct))
            await Audit.WriteAsync(conn, tx, job.NoteId, now, "notes-service", $"job_{state}", reason, ct);
        await tx.CommitAsync(ct);
    }

    private sealed record DraftInput
    {
        public string State { get; init; } = "";
        public string? Transcript { get; init; }
    }
}
