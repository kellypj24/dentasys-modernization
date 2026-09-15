using Dentasys.Data.SqlServer;
using Dentasys.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Dentasys.Parity.Tests;

/// <summary>
/// Comparison one: does the new C# reproduce the stored procedure?
///
/// Both sides read the SAME SQL Server database, so the data is identical by
/// construction and every difference is attributable to the logic. This is the
/// test that says the rules were ported faithfully -- before anything is asked
/// about whether the data moved correctly.
/// </summary>
public sealed class LegacyProcedureParityTests
{
    private readonly ITestOutputHelper _out;

    public LegacyProcedureParityTests(ITestOutputHelper output) => _out = output;

    private static string LegacyConnection =>
        Environment.GetEnvironmentVariable("DENTASYS_LEGACY_CONNECTION")
        ?? "Server=localhost,11433;User Id=sa;Password=Dentasys!1997;TrustServerCertificate=true;Encrypt=false";

    [Fact]
    public async Task Csharp_reproduces_the_stored_procedure_across_the_fleet()
    {
        DapperTypeHandlersShim.EnsureRegistered();

        var rules = RuleBook.ForSchedule();
        var comparer = new ScheduleComparer(rules);
        var report = new ParityReport();

        var oracle = new LegacyProcedureReader(LegacyConnection);
        var app = new LegacyDirectScheduleSource(
            new SqlServerScheduleRepository(LegacyConnection),
            new ScheduleService(),
            // Null: nobody has collected the .INI grids yet, which is the fleet's
            // actual state. The proc will still emit its hardcoded units x 10 and
            // the rule book classifies that as blocked-on-discovery, not agreement.
            gridMinutes: null);

        foreach (var (practice, date) in FleetProbe.All())
        {
            var legacy = await oracle.RunAsync(practice, date);
            var modern = await app.GetScheduleAsync(new ScheduleQuery { PracticeId = practice, Date = date });
            comparer.Compare(practice, date, legacy.Slots, modern.Slots, report, legacy.RawEndTimes);
        }

        _out.WriteLine(report.Summary(rules));

        Assert.True(report.ScreensCompared == FleetProbe.Practices.Length * FleetProbe.Dates.Length,
            $"expected {FleetProbe.Practices.Length * FleetProbe.Dates.Length} screens, compared {report.ScreensCompared}");
        Assert.True(report.RowsCompared > 0, "the probe matched no appointments at all -- fixtures missing?");
        Assert.Empty(report.Regressions);
    }
}

/// <summary>Registers Dapper's DateOnly/TimeOnly handlers for tests that need them.</summary>
internal static class DapperTypeHandlersShim
{
    internal static void EnsureRegistered() => Dentasys.Data.Postgres.DapperTypeHandlers.Register();
}
