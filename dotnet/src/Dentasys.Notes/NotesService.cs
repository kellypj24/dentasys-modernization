using System.Text.Json;
using Dapper;
using Dentasys.Notetaker;
using Microsoft.Data.SqlClient;

namespace Dentasys.Notes;

public sealed record CaptureRegistration(
    Guid CaptureId, string PracticeId, int PatientId, int? ApptId, string ProviderCode, bool ConsentRecorded);

public sealed record UploadOutcome(bool Complete, IReadOnlyList<int> MissingChunks);

public enum EditResult { Saved, Conflict, NotEditable, NotFound }
public sealed record EditOutcome(EditResult Result, int? Version, string? State);

public sealed record NoteView(
    Guid NoteId, string PracticeId, string State, int? CurrentVersion, string? CurrentSource,
    ClinicalNoteDraft? Current, int Versions, int UnappliedVersions);

/// <summary>
/// What the capture agent and the review panel can do to a note. Each command is
/// one transaction. The ones the capture agent calls are idempotent, because it
/// retries after every disconnect; the ones a person calls are guarded by the
/// version they were looking at.
/// </summary>
public sealed class NotesService
{
    private readonly NotesOptions _options;
    private readonly TimeProvider _clock;

    public NotesService(NotesOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    /// <summary>Safe to repeat: a second registration of the same capture changes nothing.</summary>
    public async Task RegisterCaptureAsync(CaptureRegistration r, CancellationToken ct = default)
    {
        if (!r.ConsentRecorded)
            throw new InvalidOperationException("no consent recorded for this visit; nothing may be stored");

        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        var now = _clock.GetUtcNow();

        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM notes.capture WITH (UPDLOCK, HOLDLOCK) WHERE capture_id = @CaptureId",
            new { r.CaptureId }, tx, cancellationToken: ct));
        if (exists == 0)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO notes.capture (capture_id, practice_id, patient_id, appt_id, provider_cd, started_at)
                VALUES (@CaptureId, @PracticeId, @PatientId, @ApptId, @ProviderCode, @now);
                INSERT INTO notes.note (note_id, practice_id, patient_id, appt_id, provider_cd, state, updated_at)
                VALUES (@CaptureId, @PracticeId, @PatientId, @ApptId, @ProviderCode, 'awaiting_audio', @now);
                """, new { r.CaptureId, r.PracticeId, r.PatientId, r.ApptId, r.ProviderCode, now }, tx, cancellationToken: ct));
            await Audit.WriteAsync(conn, tx, r.CaptureId, now, "capture-agent", "capture_registered", null, ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Returns false when the chunk was already stored: a re-send, acknowledged and ignored.</summary>
    public async Task<bool> PutChunkAsync(Guid captureId, int chunkNo, byte[] content, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO notes.capture_chunk (capture_id, chunk_no, content, received_at)
                VALUES (@captureId, @chunkNo, @content, @now)
                """, new { captureId, chunkNo, content, now = _clock.GetUtcNow() }, cancellationToken: ct));
            return true;
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            return false;
        }
    }

    /// <summary>
    /// The agent says it has sent <paramref name="chunkCount"/> chunks. If any are
    /// missing the answer names them, so the agent re-sends exactly those. Once all
    /// are present the note moves to transcribing; repeating the call is harmless.
    /// </summary>
    public async Task<UploadOutcome> CompleteUploadAsync(Guid captureId, int chunkCount, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        var now = _clock.GetUtcNow();

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE notes.capture SET chunk_count = @chunkCount WHERE capture_id = @captureId",
            new { captureId, chunkCount }, tx, cancellationToken: ct));

        var have = (await conn.QueryAsync<int>(new CommandDefinition(
            "SELECT chunk_no FROM notes.capture_chunk WHERE capture_id = @captureId",
            new { captureId }, tx, cancellationToken: ct))).ToHashSet();
        var missing = Enumerable.Range(0, chunkCount).Where(n => !have.Contains(n)).ToList();
        if (missing.Count > 0)
        {
            await tx.CommitAsync(ct);
            return new UploadOutcome(false, missing);
        }

        var moved = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE notes.note SET state = 'transcribing', updated_at = @now
             WHERE note_id = @captureId AND state = 'awaiting_audio'
            """, new { captureId, now }, tx, cancellationToken: ct));
        if (moved == 1)
        {
            await Jobs.EnqueueAsync(conn, tx, captureId, "transcribe", now, ct);
            await Audit.WriteAsync(conn, tx, captureId, now, "capture-agent", "upload_complete", $"{chunkCount} chunks", ct);
        }
        await tx.CommitAsync(ct);
        return new UploadOutcome(true, []);
    }

    /// <summary>
    /// Saves the provider's edit as a new version, but only if the provider was
    /// editing the current one. A stale base means someone (or a late cloud draft)
    /// changed it in the meantime, and silently overwriting either is wrong.
    /// </summary>
    public async Task<EditOutcome> SaveEditAsync(Guid noteId, int baseVersion, ClinicalNoteDraft draft, string actor,
                                                 CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        var now = _clock.GetUtcNow();

        var note = await LockNoteAsync(conn, tx, noteId, ct);
        if (note is null) return new EditOutcome(EditResult.NotFound, null, null);
        if (!NoteState.IsEditable(note.State)) return new EditOutcome(EditResult.NotEditable, note.CurrentVersion, note.State);
        if (note.CurrentVersion != baseVersion) return new EditOutcome(EditResult.Conflict, note.CurrentVersion, note.State);

        var version = await Versions.InsertAsync(conn, tx, noteId, $"human:{actor}", draft, null, applied: true, now, ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE notes.note SET current_version = @version, state = 'in_review', updated_at = @now WHERE note_id = @noteId
            """, new { noteId, version, now }, tx, cancellationToken: ct));
        await Audit.WriteAsync(conn, tx, noteId, now, actor, "edited", $"v{version}", ct);
        await tx.CommitAsync(ct);
        return new EditOutcome(EditResult.Saved, version, NoteState.InReview);
    }

    /// <summary>
    /// Signs the version the provider reviewed. The signature and the pending
    /// chart write commit together: there is no moment when a note is signed but
    /// nothing is on its way to the chart.
    /// </summary>
    public async Task<EditOutcome> SignAsync(Guid noteId, int version, string actor, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        var now = _clock.GetUtcNow();

        var note = await LockNoteAsync(conn, tx, noteId, ct);
        if (note is null) return new EditOutcome(EditResult.NotFound, null, null);
        if (!NoteState.IsEditable(note.State)) return new EditOutcome(EditResult.NotEditable, note.CurrentVersion, note.State);
        if (note.CurrentVersion != version) return new EditOutcome(EditResult.Conflict, note.CurrentVersion, note.State);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO notes.signature (note_id, version, signed_by, signed_at) VALUES (@noteId, @version, @actor, @now);
            UPDATE notes.note SET state = 'signed', updated_at = @now WHERE note_id = @noteId;
            INSERT INTO notes.chart_write (item_id, note_id, practice_id, kind, state, attempts, run_after)
            VALUES (@noteId, @noteId, @practiceId, 'NOTE', 'pending', 0, @now);
            """, new { noteId, version, actor, now, practiceId = note.PracticeId }, tx, cancellationToken: ct));
        await Audit.WriteAsync(conn, tx, noteId, now, actor, "signed", $"v{version}", ct);
        await tx.CommitAsync(ct);
        return new EditOutcome(EditResult.Saved, version, NoteState.Signed);
    }

    /// <summary>The only way to change a signed note: a separately signed addendum, also sent to the chart.</summary>
    public async Task<Guid?> AddAddendumAsync(Guid noteId, string text, string actor, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        var now = _clock.GetUtcNow();

        var note = await LockNoteAsync(conn, tx, noteId, ct);
        if (note is null || !NoteState.IsSigned(note.State)) return null;

        var addendumId = Guid.NewGuid();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO notes.addendum (addendum_id, note_id, text, signed_by, signed_at) VALUES (@addendumId, @noteId, @text, @actor, @now);
            INSERT INTO notes.chart_write (item_id, note_id, practice_id, kind, state, attempts, run_after)
            VALUES (@addendumId, @noteId, @practiceId, 'ADDENDUM', 'pending', 0, @now);
            """, new { addendumId, noteId, text, actor, now, practiceId = note.PracticeId }, tx, cancellationToken: ct));
        await Audit.WriteAsync(conn, tx, noteId, now, actor, "addendum", null, ct);
        await tx.CommitAsync(ct);
        return addendumId;
    }

    public async Task<NoteView?> GetAsync(Guid noteId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ViewRow>(new CommandDefinition("""
            SELECT n.practice_id AS PracticeId, n.state AS State, n.current_version AS CurrentVersion,
                   v.source AS CurrentSource, v.content AS CurrentContent,
                   (SELECT COUNT(*) FROM notes.note_version x WHERE x.note_id = n.note_id) AS Versions,
                   (SELECT COUNT(*) FROM notes.note_version x WHERE x.note_id = n.note_id AND x.is_applied = 0) AS Unapplied
              FROM notes.note n
              LEFT JOIN notes.note_version v ON v.note_id = n.note_id AND v.version = n.current_version
             WHERE n.note_id = @noteId
            """, new { noteId }, cancellationToken: ct));
        if (row is null) return null;

        var current = row.CurrentContent is null ? null
            : JsonSerializer.Deserialize<ClinicalNoteDraft>(row.CurrentContent, ClinicalNoteDraft.Json);
        return new NoteView(noteId, row.PracticeId.Trim(), row.State, row.CurrentVersion, row.CurrentSource,
                            current, row.Versions, row.Unapplied);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_options.NotesConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    internal static Task<LockedNote?> LockNoteAsync(SqlConnection conn, SqlTransaction tx, Guid noteId, CancellationToken ct) =>
        conn.QuerySingleOrDefaultAsync<LockedNote>(new CommandDefinition("""
            SELECT note_id AS NoteId, practice_id AS PracticeId, state AS State, current_version AS CurrentVersion
              FROM notes.note WITH (UPDLOCK, ROWLOCK) WHERE note_id = @noteId
            """, new { noteId }, tx, cancellationToken: ct));

    // Dapper maps records by exact constructor signature; property-init records
    // avoid that trap for nullable columns.
    internal sealed record LockedNote
    {
        public Guid NoteId { get; init; }
        public string PracticeId { get; init; } = "";
        public string State { get; init; } = "";
        public int? CurrentVersion { get; init; }
    }

    private sealed record ViewRow
    {
        public string PracticeId { get; init; } = "";
        public string State { get; init; } = "";
        public int? CurrentVersion { get; init; }
        public string? CurrentSource { get; init; }
        public string? CurrentContent { get; init; }
        public int Versions { get; init; }
        public int Unapplied { get; init; }
    }
}

internal static class Versions
{
    public static async Task<int> InsertAsync(SqlConnection conn, SqlTransaction tx, Guid noteId, string source,
                                             ClinicalNoteDraft draft, Guid? jobId, bool applied, DateTimeOffset now,
                                             CancellationToken ct)
    {
        var version = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT ISNULL(MAX(version), 0) + 1 FROM notes.note_version WHERE note_id = @noteId",
            new { noteId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO notes.note_version (note_id, version, source, content, job_id, is_applied, created_at)
            VALUES (@noteId, @version, @source, @content, @jobId, @applied, @now)
            """, new { noteId, version, source, content = JsonSerializer.Serialize(draft, ClinicalNoteDraft.Json),
                       jobId, applied, now }, tx, cancellationToken: ct));
        return version;
    }
}

internal static class Audit
{
    public static Task WriteAsync(SqlConnection conn, SqlTransaction tx, Guid noteId, DateTimeOffset at,
                                  string actor, string action, string? detail, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO notes.audit (note_id, at, actor, action, detail) VALUES (@noteId, @at, @actor, @action, @detail)
            """, new { noteId, at, actor, action, detail }, tx, cancellationToken: ct));
}

internal static class Jobs
{
    public static Task EnqueueAsync(SqlConnection conn, SqlTransaction tx, Guid noteId, string kind,
                                    DateTimeOffset runAfter, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO notes.job (job_id, note_id, kind, state, attempts, run_after)
            VALUES (NEWID(), @noteId, @kind, 'pending', 0, @runAfter)
            """, new { noteId, kind, runAfter }, tx, cancellationToken: ct));
}
