namespace Dentasys.Api;

/// <summary>
/// The wire contract. Deliberately NOT the domain model: the API's shape is a
/// promise to callers and the domain model is free to change behind it. Times are
/// strings in the practice's own wall clock, because that is what the schedule
/// screen means and converting to the caller's zone would be a lie about a system
/// that books in local time.
/// </summary>
public sealed record ScheduleSlotDto
{
    public required long AppointmentId { get; init; }
    public required string Date { get; init; }
    public required string Time { get; init; }
    public int? LengthUnits { get; init; }
    public int? DurationMinutes { get; init; }
    public string? EndTime { get; init; }
    public bool EndsNextDay { get; init; }
    public long? PatientId { get; init; }
    public string? PatientName { get; init; }
    public string? HomePhone { get; init; }
    public decimal? Balance { get; init; }
    public string? ProviderCode { get; init; }
    public string? ProviderName { get; init; }
    public string? OperatoryCode { get; init; }
    public string? OperatoryName { get; init; }
    public string? ProcedureCode { get; init; }
    public string? ProcedureDescription { get; init; }
    public string? StatusCode { get; init; }
    public string? Note { get; init; }
}

public sealed record ScheduleResponseDto
{
    public required string PracticeId { get; init; }
    public required string Date { get; init; }

    /// <summary>
    /// Minutes per unit for this practice, or null if nobody has collected it yet.
    /// Exposed rather than hidden: a caller showing blank durations deserves to
    /// know it is because of a missing configuration, not a missing appointment.
    /// </summary>
    public int? GridMinutes { get; init; }

    public required IReadOnlyList<ScheduleSlotDto> Slots { get; init; }
    public bool Truncated { get; init; }
}
