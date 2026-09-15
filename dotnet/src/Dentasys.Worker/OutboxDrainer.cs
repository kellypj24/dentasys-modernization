using System.Text.Json;
using Dapper;
using Dentasys.Data.Postgres;
using Npgsql;

namespace Dentasys.Worker;

/// <summary>
/// Drains dentasys.outbox and hands each event to its consumers.
///
/// This replaces usp_NightlyRecallAndClaims -- a 2 AM SQL Agent job with no
/// retries, no backoff, no observability and no way to know it had failed except
/// a practice noticing their recall letters never went out.
///
/// Two details carry the weight:
///
/// FOR UPDATE SKIP LOCKED lets several workers drain the same table without
/// coordinating and without processing the same row twice. Without SKIP LOCKED
/// they would serialise behind each other; without FOR UPDATE they would double-
/// process. It is the whole reason a queue can live in a relational database
/// rather than needing a broker on day one.
///
/// Delivery is AT LEAST ONCE, not exactly once. A crash between handling an event
/// and marking it published replays it. That is not a defect to be engineered
/// away -- exactly-once delivery across two systems is not available at any price
/// -- so consumers must be idempotent, and saying so here is more honest than
/// pretending the guarantee is stronger.
/// </summary>
public sealed class OutboxDrainer : BackgroundService
{
    private readonly PostgresOptions _options;
    private readonly ILogger<OutboxDrainer> _log;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;

    public OutboxDrainer(PostgresOptions options, ILogger<OutboxDrainer> log,
                         TimeSpan? interval = null, int batchSize = 100)
    {
        _options = options;
        _log = log;
        _interval = interval ?? TimeSpan.FromSeconds(2);
        _batchSize = batchSize;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("outbox drainer started, polling every {Interval}", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var drained = await DrainOnceAsync(stoppingToken);
                if (drained > 0) _log.LogInformation("published {Count} event(s)", drained);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Keep polling. A drainer that dies on one bad batch is a drainer
                // that stops delivering and tells nobody -- which is exactly the
                // failure mode of the SQL Agent job it replaces.
                _log.LogError(ex, "drain cycle failed; continuing");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    public async Task<int> DrainOnceAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var batch = (await conn.QueryAsync<OutboxRow>(new CommandDefinition("""
            SELECT event_id AS EventId, practice_id AS PracticeId, event_type AS EventType,
                   occurred_at AS OccurredAt, payload::text AS Payload, attempts AS Attempts
              FROM dentasys.outbox
             WHERE published_at IS NULL
             ORDER BY occurred_at
             LIMIT @batchSize
               FOR UPDATE SKIP LOCKED
            """, new { batchSize = _batchSize }, transaction: tx, cancellationToken: ct))).ToList();

        if (batch.Count == 0) return 0;

        var published = new List<Guid>();

        foreach (var row in batch)
        {
            try
            {
                await HandleAsync(row, ct);
                published.Add(row.EventId);
            }
            catch (Exception ex)
            {
                // Record the failure against the row and leave it unpublished so the
                // next cycle retries it. attempts and last_error are what turn "the
                // recall letters did not go out" into something a dashboard can show.
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE dentasys.outbox
                       SET attempts = attempts + 1, last_error = @error
                     WHERE event_id = @eventId
                    """, new { eventId = row.EventId, error = ex.Message },
                    transaction: tx, cancellationToken: ct));

                _log.LogWarning(ex, "event {EventId} ({EventType}) failed, attempt {Attempt}",
                    row.EventId, row.EventType, row.Attempts + 1);
            }
        }

        if (published.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE dentasys.outbox SET published_at = now() WHERE event_id = ANY(@ids)",
                new { ids = published.ToArray() }, transaction: tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
        return published.Count;
    }

    /// <summary>
    /// Where a real broker publish would go. Logging stands in for it deliberately:
    /// the outbox pattern's value is that the broker is a delivery detail, so the
    /// repo can demonstrate the pattern honestly without pretending to run Kafka.
    /// </summary>
    private Task HandleAsync(OutboxRow row, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(row.Payload);

        _log.LogInformation("{EventType} practice={PracticeId} appointment={AppointmentId}",
            row.EventType, row.PracticeId,
            doc.RootElement.TryGetProperty("AppointmentId", out var id) ? id.ToString() : "-");

        return Task.CompletedTask;
    }

    // DateTime, not DateTimeOffset: Npgsql surfaces timestamptz as a UTC DateTime,
    // and Dapper matches record constructors by exact signature -- a mismatch here
    // fails materialization for the whole batch rather than one row.
    private sealed record OutboxRow(
        Guid EventId, string PracticeId, string EventType,
        DateTime OccurredAt, string Payload, int Attempts);
}
