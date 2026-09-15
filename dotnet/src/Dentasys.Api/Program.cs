using Dentasys.Api;
using Dentasys.Data.Postgres;
using Dentasys.Domain;

// Dapper's type-handler table is global; register ours here, at the one place
// that owns process-wide configuration.
DapperTypeHandlers.Register();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(new PostgresOptions
{
    ConnectionString = builder.Configuration.GetConnectionString("Target")
        ?? Environment.GetEnvironmentVariable("DENTASYS_TARGET_CONNECTION")
        ?? "Host=localhost;Port=15432;Database=dentasys;Username=dentasys;Password=dentasys",
    StatementTimeoutMs = builder.Configuration.GetValue("Guards:StatementTimeoutMs", 3_000),
    MaxRows = builder.Configuration.GetValue("Guards:MaxRows", 500),
});

builder.Services.AddScoped<PostgresScheduleRepository>();
builder.Services.AddScoped<PostgresAppointmentWriter>();
builder.Services.AddScoped<PracticeConfigReader>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

/*  The one door to the database.

    Everything the legacy fat client did for itself -- open a connection, hold it
    all day, run whatever SQL it liked -- happens here instead, once, in a process
    that can be given a timeout, a row cap, a connection pool and a read-only
    transaction. The workstation gets a JSON document and no ability to do
    anything else.                                                              */
app.MapGet("/practices/{practiceId}/schedule", async (
    string practiceId,
    DateOnly date,
    string? operatory,
    string? provider,
    bool? includeDeleted,
    PostgresScheduleRepository repo,
    PracticeConfigReader config,
    ScheduleService scheduler,
    CancellationToken ct) =>
{
    var grid = await config.GetGridMinutesAsync(practiceId, ct);

    var rows = await repo.GetDayAsync(practiceId, date, ct);

    var slots = scheduler.Build(rows, new ScheduleQuery
    {
        PracticeId = practiceId,
        Date = date,
        OperatoryCode = operatory,
        ProviderCode = provider,
        IncludeDeleted = includeDeleted ?? false,
        GridMinutes = grid,
    });

    return Results.Ok(new ScheduleResponseDto
    {
        PracticeId = practiceId,
        Date = date.ToString("yyyy-MM-dd"),
        GridMinutes = grid,
        Truncated = repo.LastReadTruncated,
        Slots = slots.Select(s => new ScheduleSlotDto
        {
            AppointmentId = s.AppointmentId,
            Date = s.LocalDate.ToString("yyyy-MM-dd"),
            Time = s.LocalTime.ToString("HH:mm"),
            LengthUnits = s.LengthUnits,
            DurationMinutes = s.DurationMinutes,
            EndTime = s.EndTime?.ToString("HH:mm"),
            EndsNextDay = s.EndsNextDay,
            PatientId = s.PatientId,
            PatientName = s.PatientName,
            HomePhone = s.HomePhone,
            Balance = s.Balance,
            ProviderCode = s.ProviderCode,
            ProviderName = s.ProviderName,
            OperatoryCode = s.OperatoryCode,
            OperatoryName = s.OperatoryName,
            ProcedureCode = s.ProcedureCode,
            ProcedureDescription = s.ProcedureDescription,
            StatusCode = s.StatusCode,
            Note = s.Note,
        }).ToList(),
    });
});

/*  Writes.

    The legacy fat client did these with an INSERT and an UPDATE it composed
    itself, against a schema with no foreign keys, no check constraints and no
    unique indexes. Whatever it sent, the database took.

    Here every command goes through the domain rules first and a rejection is a
    400 with the specific violations, not a constraint error surfacing three
    layers up as a 500.                                                        */

app.MapPost("/practices/{practiceId}/appointments", async (
    string practiceId, BookRequest body, PostgresAppointmentWriter writer, CancellationToken ct) =>
{
    var outcome = await writer.BookAsync(new BookAppointment
    {
        PracticeId = practiceId,
        ActorId = body.ActorId,
        PatientId = body.PatientId,
        OperatoryCode = body.OperatoryCode,
        ProviderCode = body.ProviderCode,
        LocalDate = DateOnly.Parse(body.Date),
        LocalTime = TimeOnly.Parse(body.Time),
        LengthUnits = body.LengthUnits,
        ProcedureCode = body.ProcedureCode,
        Note = body.Note,
    }, ct);

    return Respond(outcome, created: true, practiceId);
});

app.MapPost("/practices/{practiceId}/appointments/{appointmentId:long}/complete", async (
    string practiceId, long appointmentId, CompleteRequest body,
    PostgresAppointmentWriter writer, CancellationToken ct) =>
{
    var outcome = await writer.CompleteAsync(new CompleteAppointment
    {
        PracticeId = practiceId, AppointmentId = appointmentId, ActorId = body.ActorId,
    }, ct);
    return Respond(outcome, created: false, practiceId);
});

app.MapDelete("/practices/{practiceId}/appointments/{appointmentId:long}", async (
    string practiceId, long appointmentId, string actorId, string? reason,
    PostgresAppointmentWriter writer, CancellationToken ct) =>
{
    var outcome = await writer.CancelAsync(new CancelAppointment
    {
        PracticeId = practiceId, AppointmentId = appointmentId, ActorId = actorId, Reason = reason,
    }, ct);
    return Respond(outcome, created: false, practiceId);
});

static IResult Respond(CommandOutcome outcome, bool created, string practiceId)
{
    if (!outcome.Accepted)
        return Results.ValidationProblem(
            outcome.Violations.GroupBy(v => v.Code)
                   .ToDictionary(g => g.Key, g => g.Select(v => v.Message).ToArray()),
            title: "The command was rejected by a business rule.");

    var body = new CommandAcceptedDto
    {
        AppointmentId = outcome.AppointmentId,
        Events = outcome.Events.Select(e => e.EventType).ToList(),
        // Surfaced rather than swallowed: the caller is entitled to know the
        // validation was partial, and WHY, so "we checked" does not get assumed.
        SkippedChecks = outcome.SkippedChecks,
    };

    return created
        ? Results.Created($"/practices/{practiceId}/appointments/{outcome.AppointmentId}", body)
        : Results.Ok(body);
}

app.Run();

/// <summary>Exposed so the parity tests can host the API in-process.</summary>
public partial class Program;
