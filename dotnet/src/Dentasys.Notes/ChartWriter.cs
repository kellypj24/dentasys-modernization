using System.Text.Json;
using Dapper;
using Dentasys.Notetaker;
using Microsoft.Data.SqlClient;

namespace Dentasys.Notes;

/// <summary>
/// Delivers signed notes and addenda from the outbox to CLINICAL_NOTE in each
/// practice's own database: the chart.
///
/// Delivery is at least once. A worker can write the row and die before marking
/// it delivered, so the write is an insert-if-absent keyed by NOTE_ID: the second
/// delivery finds the row and does nothing. Signed content never changes, so
/// there is nothing to update.
///
/// A practice that has not installed 07.04.00 has no CLINICAL_NOTE. That is a
/// normal state for months of a rollout, so it holds the write for
/// <see cref="NotesOptions.ChartHold"/> and tries again, rather than failing.
/// </summary>
public sealed class ChartWriter
{
    private const int InvalidObjectName = 208;

    private readonly NotesOptions _options;
    private readonly TimeProvider _clock;

    public ChartWriter(NotesOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    public async Task<bool> RunOnceAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_options.NotesConnectionString);
        await conn.OpenAsync(ct);

        var item = await Lease.ClaimAsync(conn, "chart", _clock.GetUtcNow(), _options.Lease, ct);
        if (item is null) return false;

        var row = await LoadAsync(conn, item, ct);
        try
        {
            await WriteToChartAsync(row, ct);
        }
        catch (SqlException ex) when (ex.Number == InvalidObjectName)
        {
            await Lease.RetryAsync(conn, "chart", item,
                $"CLINICAL_NOTE not present: practice {row.PracticeId} has not installed 07.04.00",
                _clock.GetUtcNow() + _options.ChartHold, ct);
            return true;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            await Lease.RetryAsync(conn, "chart", item, $"{ex.GetType().Name}: {ex.Message}",
                                   _clock.GetUtcNow() + _options.RetryDelay(item.Attempts), ct);
            return true;
        }

        await MarkDeliveredAsync(conn, item, ct);
        return true;
    }

    public async Task<int> RunUntilIdleAsync(CancellationToken ct = default)
    {
        var n = 0;
        while (await RunOnceAsync(ct)) n++;
        return n;
    }

    /// <summary>
    /// The write itself, exposed so a test can deliver the same item twice and
    /// prove the second delivery is a no-op -- the crash between "written" and
    /// "marked delivered".
    /// </summary>
    public async Task WriteToChartAsync(ChartRow row, CancellationToken ct = default)
    {
        var practice = new SqlConnectionStringBuilder(_options.PracticeServerConnectionString)
        {
            InitialCatalog = $"DENTASYS_{row.PracticeId}",
        };
        await using var conn = new SqlConnection(practice.ConnectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            IF NOT EXISTS (SELECT 1 FROM CLINICAL_NOTE WITH (UPDLOCK, HOLDLOCK) WHERE NOTE_ID = @ItemId)
                INSERT INTO CLINICAL_NOTE (NOTE_ID, KIND, PARENT_NOTE_ID, PAT_ID, APPT_ID, PROV_CD,
                                           NOTE_TEXT, NOTE_JSON, SIGNED_BY, SIGNED_AT)
                VALUES (@ItemId, @Kind, @ParentNoteId, @PatientId, @ApptId, @ProviderCode,
                        @Text, @Json, @SignedBy, @SignedAt);
            """, row, cancellationToken: ct));
    }

    public async Task<ChartRow> LoadAsync(Guid itemId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_options.NotesConnectionString);
        await conn.OpenAsync(ct);
        var kind = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT kind FROM notes.chart_write WHERE item_id = @itemId", new { itemId }, cancellationToken: ct));
        var noteId = await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT note_id FROM notes.chart_write WHERE item_id = @itemId", new { itemId }, cancellationToken: ct));
        return await LoadAsync(conn, new Claimed(itemId, noteId, kind!, 0, Guid.Empty), ct);
    }

    private static async Task<ChartRow> LoadAsync(SqlConnection conn, Claimed item, CancellationToken ct)
    {
        var note = await conn.QuerySingleAsync<NoteRow>(new CommandDefinition("""
            SELECT n.practice_id AS PracticeId, n.patient_id AS PatientId, n.appt_id AS ApptId, n.provider_cd AS ProviderCode,
                   s.signed_by AS SignedBy, s.signed_at AS SignedAt, v.content AS Content
              FROM notes.note n
              JOIN notes.signature s ON s.note_id = n.note_id
              JOIN notes.note_version v ON v.note_id = s.note_id AND v.version = s.version
             WHERE n.note_id = @NoteId
            """, new { item.NoteId }, cancellationToken: ct));

        if (item.Kind == "NOTE")
        {
            var draft = JsonSerializer.Deserialize<ClinicalNoteDraft>(note.Content, ClinicalNoteDraft.Json)!;
            return new ChartRow(item.Id, "NOTE", null, note.PracticeId.Trim(), note.PatientId, note.ApptId,
                                note.ProviderCode, NoteRenderer.Render(draft), note.Content, note.SignedBy, note.SignedAt);
        }

        var add = await conn.QuerySingleAsync<AddendumRow>(new CommandDefinition(
            "SELECT text AS Text, signed_by AS SignedBy, signed_at AS SignedAt FROM notes.addendum WHERE addendum_id = @Id",
            new { item.Id }, cancellationToken: ct));
        return new ChartRow(item.Id, "ADDENDUM", item.NoteId, note.PracticeId.Trim(), note.PatientId, note.ApptId,
                            note.ProviderCode, add.Text, null, add.SignedBy, add.SignedAt);
    }

    private async Task MarkDeliveredAsync(SqlConnection conn, Claimed item, CancellationToken ct)
    {
        await using var tx = conn.BeginTransaction();
        var now = _clock.GetUtcNow();
        if (!await Lease.FinishAsync(conn, tx, "chart", item, "delivered", now, ct)) { await tx.RollbackAsync(ct); return; }
        if (item.Kind == "NOTE")
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE notes.note SET state = 'charted', updated_at = @now WHERE note_id = @NoteId AND state = 'signed'",
                new { item.NoteId, now }, tx, cancellationToken: ct));
        }
        await Audit.WriteAsync(conn, tx, item.NoteId, now, "notes-service", "charted", item.Kind, ct);
        await tx.CommitAsync(ct);
    }

    private sealed record NoteRow
    {
        public string PracticeId { get; init; } = "";
        public int PatientId { get; init; }
        public int? ApptId { get; init; }
        public string ProviderCode { get; init; } = "";
        public string SignedBy { get; init; } = "";
        public DateTimeOffset SignedAt { get; init; }
        public string Content { get; init; } = "";
    }

    private sealed record AddendumRow
    {
        public string Text { get; init; } = "";
        public string SignedBy { get; init; } = "";
        public DateTimeOffset SignedAt { get; init; }
    }
}

/// <summary>One CLINICAL_NOTE row, as it will be written.</summary>
public sealed record ChartRow(
    Guid ItemId, string Kind, Guid? ParentNoteId, string PracticeId, int PatientId, int? ApptId,
    string ProviderCode, string Text, string? Json, string SignedBy, DateTimeOffset SignedAt);
