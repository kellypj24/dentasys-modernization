namespace Dentasys.Domain;

/// <summary>
/// One appointment as a data adapter hands it over: storage already decoded into
/// domain primitives, but no business rules applied yet.
///
/// The adapters differ wildly in what they read from -- CHAR(8)+CHAR(4) columns on
/// SQL Server, a real timestamp on PostgreSQL -- and that difference stops here.
/// Everything past this type is shared, which is the entire point: if the same
/// rules run over both databases, a difference in output is a difference in DATA,
/// never a difference in logic.
/// </summary>
public sealed record ScheduleRow
{
    public required string PracticeId { get; init; }
    public required long AppointmentId { get; init; }
    public required DateOnly LocalDate { get; init; }
    public required TimeOnly LocalTime { get; init; }
    public int? LengthUnits { get; init; }
    public bool IsDeleted { get; init; }

    public long? PatientId { get; init; }
    public string? PatientLastName { get; init; }
    public string? PatientFirstName { get; init; }
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

/// <summary>A row as the schedule screen shows it, after the rules have run.</summary>
public sealed record ScheduleSlot
{
    public required long AppointmentId { get; init; }
    public required DateOnly LocalDate { get; init; }
    public required TimeOnly LocalTime { get; init; }
    public int? LengthUnits { get; init; }

    /// <summary>Null when the practice's appointment grid is unknown.</summary>
    public int? DurationMinutes { get; init; }

    /// <summary>Null when the duration is unknown. Rolls past midnight correctly.</summary>
    public TimeOnly? EndTime { get; init; }

    /// <summary>True when this slot ends on the following day.</summary>
    public bool EndsNextDay { get; init; }

    public long? PatientId { get; init; }

    /// <summary>
    /// Null when either name part is null -- see <see cref="PatientNaming"/>.
    /// </summary>
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
    public bool IsDeleted { get; init; }
}

public sealed record ScheduleQuery
{
    public required string PracticeId { get; init; }
    public required DateOnly Date { get; init; }
    public string? OperatoryCode { get; init; }
    public string? ProviderCode { get; init; }
    public bool IncludeDeleted { get; init; }

    /// <summary>
    /// Minutes per LEN_UNITS for this practice. Null when unknown.
    ///
    /// This is not a database value and never was. The legacy VB client reads it
    /// from a .INI file on the workstation, so the modern client reading it from
    /// its own configuration is a faithful port of the existing behavior, not a
    /// workaround. What it is NOT is something the migration can discover from
    /// the data -- see <see cref="AppointmentDuration"/>.
    /// </summary>
    public int? GridMinutes { get; init; }
}
