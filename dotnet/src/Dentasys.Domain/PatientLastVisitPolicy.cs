namespace Dentasys.Domain;

/// <summary>
/// When an appointment is completed, the patient's last-visit date becomes that
/// appointment's date.
///
/// This is TR_APPT_AUDIT (LANDMINE #7), and everything about how it used to exist
/// was wrong:
///
///   - it was a trigger, so it fired as a side effect of an UPDATE and appeared in
///     no code path anyone could read
///   - it was added in 2004 and is in no documentation and no upgrade script
///   - it is present on 8 of the 24 practices and absent from the other 16
///
/// That last point is the one that matters. For 22 years, completing an
/// appointment has updated LAST_VISIT at a third of the fleet and done nothing at
/// the rest, and no report says which. Recall lists, "patients due" queries and
/// anything else keyed on LAST_VISIT have therefore meant two different things
/// depending on which server answered.
///
/// Making it explicit forces the question the trigger let everyone avoid: should
/// it now apply everywhere? This policy says yes -- a patient who was seen has
/// been seen, regardless of which database happens to hold them. But that is a
/// BEHAVIOR CHANGE for 16 practices, it will move their recall numbers, and the
/// parity harness reports it as a divergence rather than letting it slip through
/// as an improvement. Somebody has to tell those practices.
/// </summary>
public static class PatientLastVisitPolicy
{
    /// <summary>
    /// The patient whose last-visit date this event updates, and the date to set,
    /// or null when the event does not affect it.
    /// </summary>
    public static (long PatientId, DateOnly LastVisit)? Apply(DomainEvent e) => e switch
    {
        AppointmentCompleted c => (c.PatientId, c.LocalDate),
        _ => null,
    };
}
