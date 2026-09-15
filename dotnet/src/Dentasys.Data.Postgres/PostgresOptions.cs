namespace Dentasys.Data.Postgres;

/// <summary>
/// The guard rails. These exist here rather than as advice in a wiki because the
/// legacy system's failure mode is precisely that nothing could enforce them:
/// every workstation had its own connection, so "don't run expensive queries" was
/// a request, not a constraint.
/// </summary>
public sealed class PostgresOptions
{
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Server-side statement timeout. A read that exceeds it is cancelled BY THE
    /// DATABASE, not merely abandoned by the client -- abandoning it would leave
    /// the query running and still holding resources.
    /// </summary>
    public int StatementTimeoutMs { get; init; } = 3_000;

    /// <summary>
    /// Hard cap on rows returned to a caller. Fetches one extra row to detect
    /// overflow, so a truncated response is reported as truncated rather than
    /// silently looking like a short day.
    /// </summary>
    public int MaxRows { get; init; } = 500;
}
