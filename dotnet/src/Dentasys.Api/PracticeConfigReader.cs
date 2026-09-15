using Dapper;
using Dentasys.Data.Postgres;
using Npgsql;

namespace Dentasys.Api;

/// <summary>
/// Reads the appointment grid a practice has been configured with.
///
/// Returns null when nobody has collected it, and null is the correct answer --
/// not a default of 10. The legacy client read this from a workstation .INI file
/// (LANDMINE #6); the modern one reads it from a table that a human fills in after
/// asking the practice. Until that happens the schedule shows unit counts and no
/// minutes, which is honest, and visibly incomplete, which is the point.
/// </summary>
public sealed class PracticeConfigReader
{
    private readonly PostgresOptions _options;

    public PracticeConfigReader(PostgresOptions options) => _options = options;

    public async Task<int?> GetGridMinutesAsync(string practiceId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT appointment_grid_minutes FROM dentasys.practice_config WHERE practice_id = @practiceId",
            new { practiceId }, cancellationToken: ct));
    }
}
