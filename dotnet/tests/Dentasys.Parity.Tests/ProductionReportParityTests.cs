using Xunit;
using Xunit.Abstractions;

namespace Dentasys.Parity.Tests;

/// <summary>
/// Comparison three: does the analytics report agree with the legacy report?
///
/// Unlike the schedule comparisons this one crosses engines AND stacks at once --
/// SQL Server proc on one side, dbt on DuckDB on the other, with the migration in
/// between -- so a difference could be the export, the transform or the model.
/// The rule book has exactly one claim (FLOAT vs decimal, bounded); anything else
/// fails, and the finding names the practice, range, provider and column.
/// </summary>
public sealed class ProductionReportParityTests
{
    private readonly ITestOutputHelper _out;

    public ProductionReportParityTests(ITestOutputHelper output) => _out = output;

    private static string LegacyConnection =>
        Environment.GetEnvironmentVariable("DENTASYS_LEGACY_CONNECTION")
        ?? "Server=localhost,11433;User Id=sa;Password=Dentasys!1997;TrustServerCertificate=true;Encrypt=false";

    /// <summary>
    /// Ranges an owner would actually run: everything, a month with activity, a
    /// prior-year month, one day, and a month with no ledger rows at all.
    /// </summary>
    private static readonly (DateOnly From, DateOnly To)[] Ranges =
    {
        (new(2025, 1, 1),  new(2026, 12, 31)),
        (new(2026, 3, 1),  new(2026, 3, 31)),
        (new(2025, 11, 1), new(2025, 11, 30)),
        (new(2026, 3, 2),  new(2026, 3, 2)),
        (new(2026, 6, 1),  new(2026, 6, 30)),
    };

    [Fact]
    public async Task Analytics_report_agrees_with_the_legacy_report_across_the_fleet()
    {
        var rules = RuleBook.ForProductionReport();
        var comparer = new ProductionReportComparer(rules);
        var oracle = new LegacyProductionReportReader(LegacyConnection);
        var analytics = new AnalyticsProductionReportReader(DuckDbPath());

        foreach (var practice in FleetProbe.Practices)
        foreach (var (from, to) in Ranges)
        {
            comparer.Compare(practice, from, to,
                await oracle.RunAsync(practice, from, to),
                await analytics.RunAsync(practice, from, to));
        }

        _out.WriteLine(comparer.Summary());

        Assert.Equal(FleetProbe.Practices.Length * Ranges.Length, comparer.ReportsCompared);
        Assert.True(comparer.ProviderLinesCompared > 0, "no provider lines matched -- is the ledger exported and dbt built?");
        Assert.Empty(comparer.Regressions);
    }

    private static string DuckDbPath()
    {
        var path = Environment.GetEnvironmentVariable("DENTASYS_DUCKDB");
        if (path is null)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "justfile")))
                dir = dir.Parent;
            path = dir is null ? "dentasys.duckdb" : Path.Combine(dir.FullName, "analytics", "dentasys.duckdb");
        }

        Assert.True(File.Exists(path), $"{path} not found -- run `just analytics` first");
        return path;
    }
}
