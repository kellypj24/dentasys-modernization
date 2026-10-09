using DuckDB.NET.Data;

namespace Dentasys.Parity;

/// <summary>
/// The modern report: fct_production_collection_daily summed over the range.
/// Reads the DuckDB file dbt built, opened read-only. Nothing here can reach
/// PostgreSQL, which is the property the analytics split exists to provide.
/// </summary>
public sealed class AnalyticsProductionReportReader
{
    private readonly string _duckDbPath;

    public AnalyticsProductionReportReader(string duckDbPath) => _duckDbPath = duckDbPath;

    public async Task<IReadOnlyList<ProviderTotals>> RunAsync(
        string practiceId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var conn = new DuckDBConnection($"Data Source={_duckDbPath};ACCESS_MODE=READ_ONLY");
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT provider_code,
                   sum(production), sum(adjustments), sum(net_production), sum(collections)
              FROM fct_production_collection_daily
             WHERE practice_id = $practice
               AND entry_date BETWEEN $from::DATE AND $to::DATE
             GROUP BY provider_code
             ORDER BY provider_code
            """;
        cmd.Parameters.Add(new DuckDBParameter("practice", practiceId));
        cmd.Parameters.Add(new DuckDBParameter("from", from.ToString("yyyy-MM-dd")));
        cmd.Parameters.Add(new DuckDBParameter("to", to.ToString("yyyy-MM-dd")));

        var result = new List<ProviderTotals>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ProviderTotals(
                reader.IsDBNull(0) ? "" : reader.GetString(0),
                reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4)));
        }
        return result;
    }
}
