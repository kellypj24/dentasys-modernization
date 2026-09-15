namespace Dentasys.Domain;

/// <summary>
/// Appointment length (LANDMINE #6) and end time (LANDMINE #8).
///
/// LEN_UNITS is documented as 10-minute units. Practices on a 15-minute grid store
/// 15-minute units in the same column with nothing to distinguish them, and the
/// grid lives in a workstation .INI file rather than in the database.
///
/// So duration is a function of (LEN_UNITS, grid) and the grid is an input the
/// caller must supply. When it is unknown the answer is null -- not a default of
/// 10. Defaulting would be right for most of the fleet and quietly half-length for
/// the rest, and a plausible wrong number is worse than a null because a null
/// stops somebody.
///
/// End time is where the legacy proc is simply broken:
///
///   RIGHT('0000' + CAST(CAST(APPT_TM AS INT) + (LEN_UNITS * 10) AS VARCHAR(4)), 4)
///
/// That treats HHMM as an integer, so 0800 + 60 minutes is "0860" and 2350 + 30 is
/// "2380". Neither is a time. The VB client patches it up before painting; Crystal
/// Reports does not, which is why the schedule and the printed day sheet have
/// disagreed for as long as anyone remembers. Real time arithmetic here is a
/// deliberate divergence from the legacy output, and the parity harness is told to
/// expect it rather than flag it.
/// </summary>
public static class AppointmentDuration
{
    public static int? Minutes(int? lengthUnits, int? gridMinutes) =>
        lengthUnits is null || gridMinutes is null ? null : lengthUnits * gridMinutes;

    /// <summary>
    /// End time, or null when the duration is unknown. <paramref name="endsNextDay"/>
    /// is true when the appointment runs past midnight -- the case the legacy string
    /// arithmetic turns into "2380".
    /// </summary>
    public static TimeOnly? End(TimeOnly start, int? durationMinutes, out bool endsNextDay)
    {
        endsNextDay = false;
        if (durationMinutes is null) return null;

        var total = start.ToTimeSpan() + TimeSpan.FromMinutes(durationMinutes.Value);
        endsNextDay = total.TotalDays >= 1;
        return TimeOnly.FromTimeSpan(TimeSpan.FromTicks(total.Ticks % TimeSpan.TicksPerDay));
    }
}
