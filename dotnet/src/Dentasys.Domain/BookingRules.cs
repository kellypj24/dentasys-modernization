namespace Dentasys.Domain;

/// <summary>An appointment already on the book, as the rules need to see it.</summary>
public sealed record OccupiedSlot(long AppointmentId, string OperatoryCode, TimeOnly Start, int? LengthUnits);

/// <summary>
/// What the system will and will not accept onto the book.
///
/// The legacy system enforced none of this. The fat client had a connection and
/// an INSERT, and whatever it sent, the database took -- there are no foreign
/// keys, no check constraints and no unique indexes anywhere in the 1997 schema.
/// Every rule below is therefore a BEHAVIOR CHANGE, and each one has to be a
/// decision rather than a tidy-up, because staff have spent 29 years adapting to
/// their absence.
/// </summary>
public static class BookingRules
{
    /// <summary>
    /// Validates a booking. <paramref name="gridMinutes"/> may be null, and what
    /// happens then is the interesting part.
    /// </summary>
    public static CommandOutcome Validate(
        BookAppointment cmd,
        string? practiceTimeZone,
        int? gridMinutes,
        IReadOnlyList<OccupiedSlot> sameDayInOperatory)
    {
        var violations = new List<RuleViolation>();
        var skipped = new List<string>();

        if (cmd.LengthUnits <= 0)
            violations.Add(new RuleViolation("length_not_positive",
                $"LEN_UNITS must be positive, got {cmd.LengthUnits}."));

        var local = cmd.LocalDate.ToDateTime(cmd.LocalTime);

        // The spring-forward gap. The legacy system CANNOT check this -- it has no
        // timezone anywhere in the schema, so 02:30 on the changeover date looks
        // like any other reading and gets booked. The appointment then has no
        // instant, and whatever the practice does at 02:30 that morning, it is not
        // keeping this appointment.
        if (practiceTimeZone is { } tz)
        {
            if (LocalTimeFacts.DoesNotExist(local, tz))
                violations.Add(new RuleViolation("local_time_does_not_exist",
                    $"{local:yyyy-MM-dd HH:mm} does not exist in {tz} -- the clocks skip it."));
            else if (LocalTimeFacts.IsAmbiguous(local, tz))
                violations.Add(new RuleViolation("local_time_is_ambiguous",
                    $"{local:yyyy-MM-dd HH:mm} occurs twice in {tz} on the fall-back date. " +
                    "Pick the earlier or later occurrence explicitly."));
        }
        else
        {
            skipped.Add("dst_edge_check (practice timezone unresolved)");
        }

        // Double-booking an operatory. A chair holds one patient at a time, which
        // is about as solid as a domain invariant gets -- and it is UNCHECKABLE
        // without the grid, because overlap needs durations and a duration is
        // LEN_UNITS x a number that lives in a workstation .INI file (LANDMINE #6).
        //
        // So this check is not skipped because it is hard. It is skipped because
        // the information required to perform it is not in the database and cannot
        // be derived from it. The front desk has been the only overlap detector in
        // this system for 29 years, and it stays that way until somebody collects
        // the .INI files.
        if (gridMinutes is { } grid)
        {
            var start = cmd.LocalTime;
            var end = start.ToTimeSpan() + TimeSpan.FromMinutes(cmd.LengthUnits * grid);

            foreach (var slot in sameDayInOperatory)
            {
                if (!string.Equals(slot.OperatoryCode?.Trim(), cmd.OperatoryCode.Trim(),
                                   StringComparison.OrdinalIgnoreCase)) continue;
                if (slot.LengthUnits is not { } units) continue;

                var slotStart = slot.Start;
                var slotEnd = slotStart.ToTimeSpan() + TimeSpan.FromMinutes(units * grid);

                if (start.ToTimeSpan() < slotEnd && slotStart.ToTimeSpan() < end)
                    violations.Add(new RuleViolation("operatory_double_booked",
                        $"Operatory {cmd.OperatoryCode.Trim()} is already booked " +
                        $"{slotStart:HH\\:mm}-{TimeOnly.FromTimeSpan(slotEnd):HH\\:mm} " +
                        $"by appointment {slot.AppointmentId}."));
            }
        }
        else
        {
            skipped.Add("operatory_overlap_check (appointment grid not discovered)");
        }

        return violations.Count > 0
            ? new CommandOutcome { Accepted = false, Violations = violations, SkippedChecks = skipped }
            : new CommandOutcome { Accepted = true, SkippedChecks = skipped };
    }
}

/// <summary>
/// The two DST questions, asked of a wall-clock reading and a zone.
///
/// Both probe by round-tripping rather than trusting the conversion: .NET resolves
/// an invalid time by shifting it and an ambiguous one by picking standard time,
/// and neither raises. Asking "did I get back what I put in" is the only reliable
/// way to tell.
/// </summary>
public static class LocalTimeFacts
{
    public static bool DoesNotExist(DateTime local, string ianaTimeZone)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZone);
        return tz.IsInvalidTime(DateTime.SpecifyKind(local, DateTimeKind.Unspecified));
    }

    public static bool IsAmbiguous(DateTime local, string ianaTimeZone)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZone);
        return tz.IsAmbiguousTime(DateTime.SpecifyKind(local, DateTimeKind.Unspecified));
    }
}
