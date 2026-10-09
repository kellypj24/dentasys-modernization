using System.Text.Json;
using Dapper;
using Dentasys.Domain;
using Npgsql;

namespace Dentasys.Data.Postgres;

/// <summary>
/// Writes a domain event to dentasys.outbox inside the caller's transaction, so
/// the event commits or rolls back with the data change that produced it.
/// Shared by every writer; there is one outbox and one way into it.
/// </summary>
internal static class Outbox
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static Task EnqueueAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, DomainEvent e, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO dentasys.outbox (event_id, practice_id, event_type, occurred_at, actor_id, payload)
            VALUES (@EventId, @PracticeId, @EventType, @OccurredAt, @ActorId, @Payload::jsonb)
            """,
            new
            {
                e.EventId, e.PracticeId, e.EventType, e.OccurredAt, e.ActorId,
                Payload = JsonSerializer.Serialize(e, e.GetType(), Json),
            }, transaction: tx, cancellationToken: ct));
}
