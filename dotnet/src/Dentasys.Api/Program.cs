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

app.Run();

/// <summary>Exposed so the parity tests can host the API in-process.</summary>
public partial class Program;
