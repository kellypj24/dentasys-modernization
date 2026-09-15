using Dentasys.Client;
using Dentasys.Data.SqlServer;
using Dentasys.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Dentasys.Parity.Tests;

/// <summary>
/// Comparison two: does the whole modern stack agree with the whole legacy stack?
///
/// Legacy side  : this process opens a connection to SQL Server, the way a
///                workstation did, and runs the shared rules.
/// Modern side  : the API is hosted in-process, holds the only PostgreSQL
///                connection, and the client reaches it over HTTP with no driver
///                of its own.
///
/// Same rules on both sides, so a difference here is the DATA MIGRATION or the
/// plumbing -- never the logic. That is what makes the result worth anything:
/// combined with the procedure test, a green run says the behavior is faithful
/// AND the move is safe, and says which one broke when it is not.
/// </summary>
public sealed class StackParityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _out;

    public StackParityTests(WebApplicationFactory<Program> factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private static string LegacyConnection =>
        Environment.GetEnvironmentVariable("DENTASYS_LEGACY_CONNECTION")
        ?? "Server=localhost,11433;User Id=sa;Password=Dentasys!1997;TrustServerCertificate=true;Encrypt=false";

    [Fact]
    public async Task Modern_stack_agrees_with_legacy_stack_across_the_fleet()
    {
        DapperTypeHandlersShim.EnsureRegistered();

        var rules = RuleBook.ForSchedule();
        var comparer = new ScheduleComparer(rules);
        var report = new ParityReport();

        var modernSource = new ApiScheduleSource(_factory.CreateClient());
        var legacyRepo = new SqlServerScheduleRepository(LegacyConnection);
        var scheduler = new ScheduleService();

        foreach (var (practice, date) in FleetProbe.All())
        {
            var query = new ScheduleQuery { PracticeId = practice, Date = date };

            var modern = await modernSource.GetScheduleAsync(query);

            // Give the legacy side whatever grid the API used, so the comparison is
            // about the data rather than about one side being configured and the
            // other not.
            var legacy = await new LegacyDirectScheduleSource(legacyRepo, scheduler, modern.GridMinutes)
                .GetScheduleAsync(query);

            comparer.Compare(practice, date, legacy.Slots, modern.Slots, report);
        }

        _out.WriteLine(report.Summary(rules));

        Assert.True(report.RowsCompared > 0, "the probe matched no appointments at all");
        Assert.Empty(report.Regressions);

        // Stronger than "no regressions": the two stacks should agree on
        // EVERYTHING here. The rule book exists for procedure-vs-C# differences,
        // and if it is quietly absorbing differences between the two databases
        // then the migration is drifting behind a ruleset written for something
        // else entirely.
        Assert.Empty(report.Findings);
    }
}
