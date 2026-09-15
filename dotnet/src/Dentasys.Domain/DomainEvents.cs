namespace Dentasys.Domain;

/// <summary>
/// Facts, stated after the fact, in the language of the practice rather than the
/// language of the tables.
///
/// The legacy system had exactly one of these and it was invisible: a trigger on
/// APPT that wrote to PAT_MSTR as a side effect of an UPDATE, present on roughly a
/// third of the fleet, in no documentation and no upgrade script (LANDMINE #7).
/// Nobody currently employed knows which practices have it.
///
/// That is the argument for events being explicit and named. The behavior did not
/// go away -- it was always there -- but it was expressed as a side effect of a
/// row change, which meant it could only be discovered by reading DDL on 24
/// servers. Here it is a subscription you can find with Go To References.
/// </summary>
public abstract record DomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public required string PracticeId { get; init; }
    public required string ActorId { get; init; }

    /// <summary>
    /// When the system recorded this, in UTC. Distinct from the appointment's own
    /// wall-clock time, which is the practice's local reading and means something
    /// else entirely.
    /// </summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public string EventType => GetType().Name;
}

public sealed record AppointmentBooked : DomainEvent
{
    public required long AppointmentId { get; init; }
    public required long PatientId { get; init; }
    public required DateOnly LocalDate { get; init; }
    public required TimeOnly LocalTime { get; init; }
    public required string OperatoryCode { get; init; }
    public required string ProviderCode { get; init; }
    public int? LengthUnits { get; init; }
}

public sealed record AppointmentRescheduled : DomainEvent
{
    public required long AppointmentId { get; init; }
    public required DateOnly FromDate { get; init; }
    public required TimeOnly FromTime { get; init; }
    public required DateOnly ToDate { get; init; }
    public required TimeOnly ToTime { get; init; }
}

public sealed record AppointmentCancelled : DomainEvent
{
    public required long AppointmentId { get; init; }
    public string? Reason { get; init; }
}

public sealed record AppointmentCompleted : DomainEvent
{
    public required long AppointmentId { get; init; }
    public required long PatientId { get; init; }
    public required DateOnly LocalDate { get; init; }
}
