using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Dentasys.Parity;

/// <summary>
/// Runs usp_RptProductionCollection: the oracle for the analytics report.
///
/// FLOAT totals are carried as the exact decimal of their 17-significant-digit
/// form -- 866.72000000000003, not 866.72. Converting through (decimal)double or
/// ToString("R") rounds to the shortest round-tripping value and would erase the
/// representation error before the rule book ever saw it; the same laundering
/// LegacyProcedureReader's history warns about.
/// </summary>
public sealed class LegacyProductionReportReader
{
    private readonly string _serverConnectionString;

    public LegacyProductionReportReader(string serverConnectionString) =>
        _serverConnectionString = serverConnectionString;

    public async Task<IReadOnlyList<ProviderTotals>> RunAsync(
        string practiceId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = $"DENTASYS_{practiceId}"
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        var rows = await conn.QueryAsync<ProcRow>(new CommandDefinition(
            "usp_RptProductionCollection",
            new { FROM_DT = from.ToString("yyyyMMdd"), TO_DT = to.ToString("yyyyMMdd") },
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));

        return rows.Select(r => new ProviderTotals(
            r.PROV_CD?.TrimEnd() ?? "",
            Exact(r.PRODUCTION), Exact(r.ADJUSTMENTS), Exact(r.NET_PRODUCTION), Exact(r.COLLECTIONS)))
            .ToList();
    }

    private static decimal Exact(double? value) =>
        value is null ? 0m
            : decimal.Parse(value.Value.ToString("G17", CultureInfo.InvariantCulture),
                            NumberStyles.Float, CultureInfo.InvariantCulture);

    private sealed record ProcRow
    {
        public string? PROV_CD { get; init; }
        public double? PRODUCTION { get; init; }
        public double? ADJUSTMENTS { get; init; }
        public double? NET_PRODUCTION { get; init; }
        public double? COLLECTIONS { get; init; }
    }
}
