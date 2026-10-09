using Dapper;
using Microsoft.Data.SqlClient;

namespace Dentasys.Notes;

/// <summary>A queue row this worker holds until <see cref="Until"/>.</summary>
public sealed record Claimed(Guid Id, Guid NoteId, string Kind, int Attempts, Guid Token);

/// <summary>
/// Claim, finish and retry for the two queues (notes.job, notes.chart_write).
///
/// Claiming is one statement: UPDLOCK + READPAST is SQL Server's SKIP LOCKED, so
/// two workers never take the same row, and the claim commits at once. The slow
/// work then runs outside any transaction. Finishing is accepted only with the
/// claim's token, so a worker whose lease ran out -- and whose row another worker
/// has since taken -- cannot also record a result.
/// </summary>
internal static class Lease
{
    private static readonly Dictionary<string, string> Queues = new()
    {
        ["job"] = "notes.job",
        ["chart"] = "notes.chart_write",
    };

    public static async Task<Claimed?> ClaimAsync(SqlConnection conn, string queue, DateTimeOffset now,
                                                  TimeSpan lease, CancellationToken ct)
    {
        var (table, id) = queue == "job" ? (Queues[queue], "job_id") : (Queues[queue], "item_id");
        return await conn.QuerySingleOrDefaultAsync<Claimed>(new CommandDefinition($"""
            WITH next AS (
                SELECT TOP (1) * FROM {table} WITH (UPDLOCK, READPAST, ROWLOCK)
                 WHERE state = 'pending' AND run_after <= @now
                   AND (lease_until IS NULL OR lease_until < @now)
                 ORDER BY run_after)
            UPDATE next
               SET attempts = attempts + 1, lease_token = @token, lease_until = @until
            OUTPUT inserted.{id} AS Id, inserted.note_id AS NoteId, inserted.kind AS Kind,
                   inserted.attempts AS Attempts, inserted.lease_token AS Token
            """, new { now, token = Guid.NewGuid(), until = now + lease }, cancellationToken: ct));
    }

    /// <summary>Marks the row finished. False means the lease was lost; the caller must roll back.</summary>
    public static async Task<bool> FinishAsync(SqlConnection conn, SqlTransaction tx, string queue, Claimed c,
                                               string state, DateTimeOffset now, CancellationToken ct)
    {
        var sql = queue == "job"
            ? "UPDATE notes.job SET state = @state, lease_token = NULL, lease_until = NULL, last_error = NULL WHERE job_id = @Id AND lease_token = @Token"
            : "UPDATE notes.chart_write SET state = @state, lease_token = NULL, lease_until = NULL, last_error = NULL, delivered_at = @now WHERE item_id = @Id AND lease_token = @Token";
        return await conn.ExecuteAsync(new CommandDefinition(sql, new { state, c.Id, c.Token, now }, tx, cancellationToken: ct)) == 1;
    }

    /// <summary>Releases the row to run again after <paramref name="runAfter"/>, recording why.</summary>
    public static Task RetryAsync(SqlConnection conn, string queue, Claimed c, string error, DateTimeOffset runAfter,
                                  CancellationToken ct)
    {
        var (table, id) = queue == "job" ? ("notes.job", "job_id") : ("notes.chart_write", "item_id");
        return conn.ExecuteAsync(new CommandDefinition($"""
            UPDATE {table} SET lease_token = NULL, lease_until = NULL, run_after = @runAfter, last_error = @error
             WHERE {id} = @Id AND lease_token = @Token
            """, new { runAfter, error = error.Length > 400 ? error[..400] : error, c.Id, c.Token }, cancellationToken: ct));
    }
}
