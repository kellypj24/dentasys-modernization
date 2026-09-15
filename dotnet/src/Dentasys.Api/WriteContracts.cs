namespace Dentasys.Api;

public sealed record BookRequest
{
    public required string ActorId { get; init; }
    public required long PatientId { get; init; }
    public required string OperatoryCode { get; init; }
    public required string ProviderCode { get; init; }

    /// <summary>yyyy-MM-dd, the practice's own wall clock.</summary>
    public required string Date { get; init; }

    /// <summary>HH:mm, the practice's own wall clock. Not an instant, on purpose.</summary>
    public required string Time { get; init; }

    public required int LengthUnits { get; init; }
    public string? ProcedureCode { get; init; }
    public string? Note { get; init; }
}

public sealed record CompleteRequest
{
    public required string ActorId { get; init; }
}

public sealed record CommandAcceptedDto
{
    public long? AppointmentId { get; init; }
    public IReadOnlyList<string> Events { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Validations the system could not perform because the information is not in
    /// the database. An empty list means everything was checked; a non-empty one
    /// means the acceptance is narrower than it looks.
    /// </summary>
    public IReadOnlyList<string> SkippedChecks { get; init; } = Array.Empty<string>();
}
