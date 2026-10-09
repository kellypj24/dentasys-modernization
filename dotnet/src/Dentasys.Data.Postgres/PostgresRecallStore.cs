using Dapper;
using Dentasys.Domain;
using Npgsql;

namespace Dentasys.Data.Postgres;

/// <summary>
/// Reads recall facts for RecallPolicy, and runs the nightly recall: for each
/// practice, in one transaction, decide, stamp last_sent_on and write a RecallDue
/// event to the outbox. The stamp and the event commit together, so a crash
/// cannot leave a recall marked sent with no letter owed, or a letter owed twice.
///
/// Re-running the same night sends nothing: everything sent is now inside the
/// 30-day resend window. That is the idempotency the 2 AM job never had.
/// </summary>
public sealed class PostgresRecallStore
{
    private readonly PostgresOptions _options;

    public PostgresRecallStore(PostgresOptions options) => _options = options;

    public async Task<IReadOnlyList<RecallFacts>> LoadAsync(string practiceId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);
        return await LoadAsync(conn, null, practiceId, lockRows: false, ct);
    }

    /// <summary>Runs every practice, each in its own transaction. Returns recalls sent.</summary>
    public async Task<int> RunAsync(DateOnly runDate, string actorId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);

        var practices = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT practice_id FROM dentasys.practice ORDER BY practice_id", cancellationToken: ct));

        var sent = 0;
        foreach (var practiceId in practices)
        {
            // One practice per transaction: a failure at one practice does not hold
            // back the other 23, and the next run picks it up because nothing was stamped.
            await using var tx = await conn.BeginTransactionAsync(ct);
            sent += await RunPracticeAsync(conn, tx, practiceId, runDate, actorId, ct);
            await tx.CommitAsync(ct);
        }
        return sent;
    }

    /// <summary>
    /// One practice's run inside a transaction the CALLER owns, so tests can
    /// exercise the real statements and roll them back.
    /// </summary>
    public async Task<int> RunPracticeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string practiceId, DateOnly runDate,
        string actorId, CancellationToken ct = default)
    {
        var due = (await LoadAsync(conn, tx, practiceId, lockRows: true, ct))
            .Where(r => RecallPolicy.Decide(r, runDate) == RecallOutcome.Send)
            .ToList();

        foreach (var r in due)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE dentasys.recall SET last_sent_on = @RunDate
                 WHERE practice_id = @PracticeId AND recall_id = @RecallId
                """, new { RunDate = runDate, PracticeId = practiceId, r.RecallId },
                transaction: tx, cancellationToken: ct));

            await Outbox.EnqueueAsync(conn, tx, new RecallDue
            {
                PracticeId = practiceId, ActorId = actorId,
                RecallId = r.RecallId, PatientId = r.PatientId!.Value, RecallType = r.RecallType,
                DueMonth = r.DueMonth, RunDate = runDate,
            }, ct);
        }
        return due.Count;
    }

    private static async Task<IReadOnlyList<RecallFacts>> LoadAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string practiceId, bool lockRows, CancellationToken ct)
    {
        // SKIP LOCKED: two overlapping runs split the work instead of both
        // sending the same recall.
        var rows = await conn.QueryAsync<RecallRow>(new CommandDefinition($"""
            SELECT r.recall_id AS RecallId, r.patient_id AS PatientId, r.recall_type AS RecallType,
                   r.due_month AS DueMonth, r.last_sent_on AS LastSentOn, r.is_deleted AS IsDeleted,
                   (p.patient_id IS NOT NULL AND NOT p.is_deleted) AS PatientActive,
                   r.due_year_source = 'patient_dob' AS DueYearFromEvidence
              FROM dentasys.recall r
              LEFT JOIN dentasys.patient p
                     ON p.practice_id = r.practice_id AND p.patient_id = r.patient_id
             WHERE r.practice_id = @practiceId
             ORDER BY r.recall_id
             {(lockRows ? "FOR UPDATE OF r SKIP LOCKED" : "")}
            """, new { practiceId }, transaction: tx, cancellationToken: ct));

        return rows.Select(r => new RecallFacts(
            r.RecallId, r.PatientId, r.RecallType, DateOnly.FromDateTime(r.DueMonth),
            r.LastSentOn is null ? null : DateOnly.FromDateTime(r.LastSentOn.Value),
            r.IsDeleted, r.PatientActive, r.DueYearFromEvidence)).ToList();
    }

    // DateTime, not DateOnly: Dapper matches record constructors by exact type and
    // Npgsql surfaces `date` as DateTime on this path.
    private sealed record RecallRow(
        long RecallId, long? PatientId, string? RecallType, DateTime DueMonth,
        DateTime? LastSentOn, bool IsDeleted, bool PatientActive, bool DueYearFromEvidence);
}
