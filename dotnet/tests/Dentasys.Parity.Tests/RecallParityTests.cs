using Dentasys.Data.Postgres;
using Dentasys.Domain;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Dentasys.Parity.Tests;

/// <summary>
/// The nightly recall run: does RecallPolicy send what usp_NightlyRecallAndClaims
/// sends, across the fleet, on nights chosen to cross each rule?
/// </summary>
public sealed class RecallParityTests
{
    private readonly ITestOutputHelper _out;

    public RecallParityTests(ITestOutputHelper output) => _out = output;

    private static string LegacyConnection =>
        Environment.GetEnvironmentVariable("DENTASYS_LEGACY_CONNECTION")
        ?? "Server=localhost,11433;User Id=sa;Password=Dentasys!1997;TrustServerCertificate=true;Encrypt=false";

    private static PostgresOptions Target => new()
    {
        ConnectionString = Environment.GetEnvironmentVariable("DENTASYS_TARGET_CONNECTION")
            ?? "Host=localhost;Port=15432;Database=dentasys;Username=dentasys;Password=dentasys",
    };

    /// <summary>
    ///   2026-03-15  inside the 30-day window of every last-sent date: nothing goes
    ///   2026-10-05  PROP and PERI fall due; the 1951-pivoted recall goes from legacy
    ///   2027-04-05  the cancelled XRAY falls due; legacy sends it where it was cancelled
    ///   2051-04-05  the pediatric recall is genuinely due: both send it
    /// </summary>
    private static readonly DateOnly[] RunDates =
    {
        new(2026, 3, 15), new(2026, 10, 5), new(2027, 4, 5), new(2051, 4, 5),
    };

    [Fact]
    public async Task Recall_policy_sends_what_the_nightly_job_sends_across_the_fleet()
    {
        DapperTypeHandlersShim.EnsureRegistered();

        var comparer = new RecallComparer(RuleBook.ForRecall());
        var oracle = new LegacyRecallReader(LegacyConnection);
        var store = new PostgresRecallStore(Target);

        foreach (var practice in FleetProbe.Practices)
        {
            var facts = await store.LoadAsync(practice);
            foreach (var runDate in RunDates)
                comparer.Compare(practice, runDate, await oracle.RunAsync(practice, runDate), facts);
        }

        _out.WriteLine(comparer.Summary());

        Assert.Equal(FleetProbe.Practices.Length * RunDates.Length, comparer.RunsCompared);
        Assert.True(comparer.SentByBoth > 0, "no recall was sent by both systems -- fixtures or dates wrong?");
        Assert.Empty(comparer.Regressions);
    }

    [Fact]
    public async Task A_second_run_the_same_night_sends_nothing_and_every_send_is_in_the_outbox()
    {
        DapperTypeHandlersShim.EnsureRegistered();

        var store = new PostgresRecallStore(Target);
        var runDate = new DateOnly(2026, 10, 5);

        await using var conn = new NpgsqlConnection(Target.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var expected = (await store.LoadAsync("000417"))
                .Count(r => RecallPolicy.Decide(r, runDate) == RecallOutcome.Send);

            var first = await store.RunPracticeAsync(conn, tx, "000417", runDate, "test");
            var second = await store.RunPracticeAsync(conn, tx, "000417", runDate, "test");

            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM dentasys.outbox WHERE event_type = 'RecallDue' AND practice_id = '000417'",
                conn, tx);
            var enqueued = (long)(await count.ExecuteScalarAsync())!;

            Assert.True(expected > 0, "nothing due on the probe date -- fixtures changed?");
            Assert.Equal(expected, first);
            Assert.Equal(0, second);
            Assert.Equal(first, enqueued);
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }
}
