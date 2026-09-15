namespace Dentasys.Domain;

/// <summary>
/// What the front desk can actually do to the appointment book.
///
/// Commands carry the practice's LOCAL WALL-CLOCK reading, not an instant. That
/// is deliberate: the receptionist is looking at a paper-shaped grid and saying
/// "half past two on Tuesday", and converting that to UTC at the edge would throw
/// away the thing they meant. The system stores both and keeps the wall clock
/// authoritative -- see LANDMINES #1.
/// </summary>
public abstract record AppointmentCommand
{
    public required string PracticeId { get; init; }

    /// <summary>Who is doing this. The legacy schema has CREATE_USER and nothing else.</summary>
    public required string ActorId { get; init; }
}

public sealed record BookAppointment : AppointmentCommand
{
    public required long PatientId { get; init; }
    public required string OperatoryCode { get; init; }
    public required string ProviderCode { get; init; }
    public required DateOnly LocalDate { get; init; }
    public required TimeOnly LocalTime { get; init; }
    public required int LengthUnits { get; init; }
    public string? ProcedureCode { get; init; }
    public string? Note { get; init; }
}

public sealed record RescheduleAppointment : AppointmentCommand
{
    public required long AppointmentId { get; init; }
    public required DateOnly LocalDate { get; init; }
    public required TimeOnly LocalTime { get; init; }
    public string? OperatoryCode { get; init; }
}

public sealed record CancelAppointment : AppointmentCommand
{
    public required long AppointmentId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Marking an appointment completed. Innocuous-looking, and the single most
/// consequential write in the system -- see <see cref="PatientLastVisitPolicy"/>.
/// </summary>
public sealed record CompleteAppointment : AppointmentCommand
{
    public required long AppointmentId { get; init; }
}

public sealed record RuleViolation(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

public sealed record CommandOutcome
{
    public required bool Accepted { get; init; }
    public IReadOnlyList<RuleViolation> Violations { get; init; } = Array.Empty<RuleViolation>();
    public IReadOnlyList<DomainEvent> Events { get; init; } = Array.Empty<DomainEvent>();
    public long? AppointmentId { get; init; }

    /// <summary>
    /// Checks the system could not perform because information it needs is not in
    /// the database. Not failures -- gaps, reported so the caller knows the
    /// validation was partial rather than clean.
    /// </summary>
    public IReadOnlyList<string> SkippedChecks { get; init; } = Array.Empty<string>();

    public static CommandOutcome Rejected(params RuleViolation[] violations) =>
        new() { Accepted = false, Violations = violations };
}
