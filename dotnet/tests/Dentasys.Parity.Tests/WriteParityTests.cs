using Dentasys.Data.Postgres;
using Dentasys.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Dentasys.Parity.Tests;

/// <summary>
/// Write parity, which is a different question from read parity.
///
/// A read that disagrees is embarrassing. A write that disagrees is permanent, and
/// the disagreement here is not a bug in the new code -- it is a deliberate
/// decision about a 22-year-old inconsistency, surfaced and counted so that it
/// gets made by a person rather than discovered by a practice.
/// </summary>
public sealed class WriteParityTests
{
    private readonly ITestOutputHelper _out;

    public WriteParityTests(ITestOutputHelper output) => _out = output;

    private static string LegacyConnection =>
        Environment.GetEnvironmentVariable("DENTASYS_LEGACY_CONNECTION")
        ?? "Server=localhost,11433;User Id=sa;Password=Dentasys!1997;TrustServerCertificate=true;Encrypt=false";

    private static PostgresOptions Target => new()
    {
        ConnectionString = Environment.GetEnvironmentVariable("DENTASYS_TARGET_CONNECTION")
            ?? "Host=localhost;Port=15432;Database=dentasys;Username=dentasys;Password=dentasys",
    };

    [Fact]
    public async Task Completing_an_appointment_diverges_exactly_where_the_trigger_is_missing()
    {
        DapperTypeHandlersShim.EnsureRegistered();

        var probe = new WriteParityProbe(LegacyConnection, Target);
        var results = new List<LastVisitProbe>();

        foreach (var practice in FleetProbe.Practices)
        {
            var r = await probe.ProbeCompleteAsync(practice);
            if (r is not null) results.Add(r);
        }

        var withTrigger = results.Where(r => r.LegacyHasTrigger).ToList();
        var without = results.Where(r => !r.LegacyHasTrigger).ToList();

        _out.WriteLine("");
        _out.WriteLine($"  completing an appointment, {results.Count} practices probed");
        _out.WriteLine("");
        _out.WriteLine($"    legacy updated LAST_VISIT      {results.Count(r => r.LegacyUpdated),3}");
        _out.WriteLine($"    modern updated last_visit      {results.Count(r => r.ModernUpdated),3}");
        _out.WriteLine($"    diverge                        {results.Count(r => r.Diverges),3}");
        _out.WriteLine("");
        _out.WriteLine($"    practices WITH TR_APPT_AUDIT    {withTrigger.Count,3}  -> both systems update, agree");
        _out.WriteLine($"    practices WITHOUT it            {without.Count,3}  -> only the modern system updates");
        _out.WriteLine("");
        _out.WriteLine("  Every divergence is a practice whose recall list is about to start");
        _out.WriteLine("  behaving like the other third of the fleet. That is the right outcome");
        _out.WriteLine("  and it is still a change somebody has to be told about.");
        _out.WriteLine("");

        // The fixtures put the trigger on a third of the fleet. If that stops being
        // true the probe is measuring something else.
        Assert.Equal(8, withTrigger.Count);
        Assert.Equal(16, without.Count);

        // Where the trigger exists, the port must match it exactly. This is the
        // assertion that says the policy is a faithful replacement and not merely
        // a plausible one.
        Assert.All(withTrigger, r => Assert.False(r.Diverges,
            $"{r.PracticeId} has TR_APPT_AUDIT but the two systems disagree -- " +
            $"legacy={r.LegacyLastVisitAfter} modern={r.ModernLastVisitAfter}"));

        // Where it does not, the modern system does MORE. Asserted rather than
        // merely reported, because a silent behavior change is the thing this whole
        // harness exists to prevent.
        Assert.All(without, r =>
        {
            Assert.False(r.LegacyUpdated, $"{r.PracticeId} has no trigger but legacy updated LAST_VISIT");
            Assert.True(r.ModernUpdated, $"{r.PracticeId} -- modern system failed to update last_visit");
        });
    }

    [Fact]
    public void Booking_into_the_spring_forward_gap_is_rejected()
    {
        // 02:30 on 8 March 2026 does not exist in New York. The legacy system cannot
        // refuse this -- it has no timezone anywhere in its schema, so the booking
        // goes in and the appointment simply never happens.
        var cmd = new BookAppointment
        {
            PracticeId = "001010", ActorId = "t", PatientId = 1,
            OperatoryCode = "OP1", ProviderCode = "DDS1",
            LocalDate = new DateOnly(2026, 3, 8), LocalTime = new TimeOnly(2, 30),
            LengthUnits = 6,
        };

        var outcome = BookingRules.Validate(cmd, "America/New_York", gridMinutes: 10,
            sameDayInOperatory: Array.Empty<OccupiedSlot>());

        Assert.False(outcome.Accepted);
        Assert.Contains(outcome.Violations, v => v.Code == "local_time_does_not_exist");
    }

    [Fact]
    public void The_same_booking_is_fine_in_Phoenix()
    {
        // Arizona does not observe DST, so 02:30 that morning is an ordinary time.
        // The identical command, valid or invalid depending on a column the legacy
        // schema does not have.
        var cmd = new BookAppointment
        {
            PracticeId = "000417", ActorId = "t", PatientId = 1,
            OperatoryCode = "OP1", ProviderCode = "DDS1",
            LocalDate = new DateOnly(2026, 3, 8), LocalTime = new TimeOnly(2, 30),
            LengthUnits = 6,
        };

        var outcome = BookingRules.Validate(cmd, "America/Phoenix", gridMinutes: 10,
            sameDayInOperatory: Array.Empty<OccupiedSlot>());

        Assert.True(outcome.Accepted);
    }

    [Fact]
    public void Overlap_cannot_be_checked_without_the_grid_and_says_so()
    {
        var existing = new[] { new OccupiedSlot(1, "OP1", new TimeOnly(9, 0), LengthUnits: 6) };

        var cmd = new BookAppointment
        {
            PracticeId = "000417", ActorId = "t", PatientId = 1,
            OperatoryCode = "OP1", ProviderCode = "DDS1",
            LocalDate = new DateOnly(2026, 3, 2), LocalTime = new TimeOnly(9, 30),
            LengthUnits = 3,
        };

        // With the grid: 09:00 + 60 minutes runs to 10:00, so 09:30 collides.
        var known = BookingRules.Validate(cmd, "America/Phoenix", gridMinutes: 10, existing);
        Assert.False(known.Accepted);
        Assert.Contains(known.Violations, v => v.Code == "operatory_double_booked");

        // Without it, the overlap is not merely unchecked -- it is UNCHECKABLE, and
        // the outcome reports that rather than quietly accepting the booking as
        // though it had been validated.
        var unknown = BookingRules.Validate(cmd, "America/Phoenix", gridMinutes: null, existing);
        Assert.True(unknown.Accepted);
        Assert.Contains(unknown.SkippedChecks, s => s.StartsWith("operatory_overlap_check"));
    }
}
