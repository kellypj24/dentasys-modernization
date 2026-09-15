

namespace Dentasys.Domain;

/// <summary>
/// Where the UI gets a schedule from. There are two, and they are not symmetric --
/// that asymmetry is the modernization, not an inconsistency.
///
///   Legacy   the workstation opens its own connection to SQL Server and queries
///            tables directly. Every desk in every practice is a database client.
///            Nothing stands between a mistyped date range and a table lock.
///
///   Modern   the workstation holds no connection, no credentials and no driver.
///            It asks an API. The API is the only process that can reach the
///            database, which is the only place timeouts, read-only transactions,
///            row caps and pooling can actually be ENFORCED rather than hoped for.
/// </summary>
public interface IScheduleSource
{
    string Description { get; }

    Task<ScheduleResult> GetScheduleAsync(ScheduleQuery query, CancellationToken ct = default);
}

/// <summary>
/// A schedule plus what it cost to get it. The legacy path can report timing but
/// nothing else; the modern path knows whether it hit a cap or a timeout, because
/// something was in a position to impose one.
/// </summary>
public sealed record ScheduleResult
{
    public required IReadOnlyList<ScheduleSlot> Slots { get; init; }
    public required string Source { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Truncated { get; init; }

    /// <summary>
    /// The appointment grid this result was rendered with, or null if the practice's
    /// grid has not been discovered. Surfaced so a caller comparing two stacks can
    /// give both the same value rather than accidentally comparing a configured
    /// render against an unconfigured one.
    /// </summary>
    public int? GridMinutes { get; init; }
}
