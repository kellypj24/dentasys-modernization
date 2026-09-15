namespace Dentasys.Domain;

/// <summary>
/// The schedule screen's behavior, extracted from usp_GetScheduleForDay.
///
/// Everything the stored procedure did apart from fetching rows happens here:
/// the soft-delete predicate, the operatory/provider filters, the null-propagating
/// patient name, duration and end time, and the ordering the UI depends on.
///
/// It takes rows and returns rows. No connection, no SQL, no I/O -- so the rules
/// that 29 years of practice depend on can be tested in milliseconds without a
/// container, and the two database adapters cannot drift apart, because there is
/// only one copy of the logic for them to drift from.
/// </summary>
public sealed class ScheduleService
{
    public IReadOnlyList<ScheduleSlot> Build(IEnumerable<ScheduleRow> rows, ScheduleQuery query)
    {
        var slots = new List<ScheduleSlot>();

        foreach (var r in rows)
        {
            if (!query.IncludeDeleted && r.IsDeleted) continue;

            if (query.OperatoryCode is { } op &&
                !string.Equals(r.OperatoryCode?.Trim(), op.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            if (query.ProviderCode is { } prov &&
                !string.Equals(r.ProviderCode?.Trim(), prov.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            var duration = AppointmentDuration.Minutes(r.LengthUnits, query.GridMinutes);
            var end = AppointmentDuration.End(r.LocalTime, duration, out var endsNextDay);

            slots.Add(new ScheduleSlot
            {
                AppointmentId = r.AppointmentId,
                LocalDate = r.LocalDate,
                LocalTime = r.LocalTime,
                LengthUnits = r.LengthUnits,
                DurationMinutes = duration,
                EndTime = end,
                EndsNextDay = endsNextDay,
                PatientId = r.PatientId,
                PatientName = PatientNaming.Display(r.PatientLastName, r.PatientFirstName),
                HomePhone = r.HomePhone,
                Balance = r.Balance,
                ProviderCode = r.ProviderCode,
                ProviderName = r.ProviderName,
                OperatoryCode = r.OperatoryCode,
                OperatoryName = r.OperatoryName,
                ProcedureCode = r.ProcedureCode,
                ProcedureDescription = r.ProcedureDescription,
                StatusCode = r.StatusCode,
                Note = r.Note,
                IsDeleted = r.IsDeleted,
            });
        }

        // ORDER BY OPER_CD, APPT_TM -- the order the appointment book paints in.
        // Ordinal, not culture-aware: the legacy server sorts by code points and a
        // culture-sensitive comparison would reorder rows on some machines and not
        // others, which is the kind of bug that only shows up in one region.
        return slots
            .OrderBy(s => s.OperatoryCode?.Trim() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(s => s.LocalTime)
            .ThenBy(s => s.AppointmentId)
            .ToList();
    }
}
