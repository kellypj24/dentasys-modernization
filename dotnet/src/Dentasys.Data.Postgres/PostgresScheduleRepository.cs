using Dapper;
using Dentasys.Domain;
using Npgsql;

namespace Dentasys.Data.Postgres;

/// <summary>
/// Reads the target schema. One multi-tenant database, so the practice id is a
/// predicate rather than a database name -- the single biggest structural change
/// between the two sides, and the app above does not notice it.
///
/// This adapter is shorter than its SQL Server counterpart for one reason: the
/// target schema stores things as what they are. No CHAR(8) date parsing, no
/// FLOAT-to-decimal rounding, no four-valued flag to interpret. The translation
/// work disappeared because the migration did it once, up front, instead of on
/// every read forever.
/// </summary>
public sealed class PostgresScheduleRepository : IScheduleRepository
{
    private readonly PostgresOptions _options;

    public PostgresScheduleRepository(PostgresOptions options) => _options = options;

    public string ProviderName => "PostgreSQL (target)";

    /// <summary>True when the last read hit <see cref="PostgresOptions.MaxRows"/>.</summary>
    public bool LastReadTruncated { get; private set; }

    // procedure_code is citext, so this join is case-insensitive exactly as the
    // legacy server's CI collation made it (LANDMINE #3). Under plain text it
    // would silently drop the lowercase 'd1110' rows and render a NULL
    // description -- no error, no log. The parity harness catches that if the
    // column type is ever changed.
    private const string Sql = """
        SELECT  a.appointment_id        AS AppointmentId,
                a.local_wall::date      AS LocalDate,
                a.local_wall::time      AS LocalTime,
                a.length_units          AS LengthUnits,
                a.is_deleted            AS IsDeleted,
                a.patient_id            AS PatientId,
                pt.last_name            AS PatientLastName,
                pt.first_name           AS PatientFirstName,
                pt.home_phone           AS HomePhone,
                pt.balance              AS Balance,
                a.provider_code::text   AS ProviderCode,
                pv.provider_name        AS ProviderName,
                a.operatory_code::text  AS OperatoryCode,
                op.operatory_name       AS OperatoryName,
                a.procedure_code::text  AS ProcedureCode,
                pc.description          AS ProcedureDescription,
                a.status_code           AS StatusCode,
                a.note                  AS Note
          FROM  dentasys.appointment a
          LEFT JOIN dentasys.patient        pt ON pt.practice_id = a.practice_id AND pt.patient_id      = a.patient_id
          LEFT JOIN dentasys.provider       pv ON pv.practice_id = a.practice_id AND pv.provider_code   = a.provider_code
          LEFT JOIN dentasys.operatory      op ON op.practice_id = a.practice_id AND op.operatory_code  = a.operatory_code
          LEFT JOIN dentasys.procedure_code pc ON pc.practice_id = a.practice_id AND pc.procedure_code  = a.procedure_code
         WHERE  a.practice_id = @PracticeId
           AND  a.local_wall::date = @LocalDate
         LIMIT  @RowLimit;
        """;

    public async Task<IReadOnlyList<ScheduleRow>> GetDayAsync(
        string practiceId, DateOnly date, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);

        // Enforced server-side, per connection. A client-side timeout would give
        // up on the query while the server kept running it.
        await using (var timeout = new NpgsqlCommand(
            $"SET statement_timeout = {_options.StatementTimeoutMs}", conn))
        {
            await timeout.ExecuteNonQueryAsync(ct);
        }

        // A read-only transaction. The schedule screen has no business writing,
        // and saying so lets the server reject a write rather than trusting that
        // this code path never attempts one.
        await using var tx = await conn.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted, ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx))
        {
            await readOnly.ExecuteNonQueryAsync(ct);
        }

        // Ask for one more than the cap so overflow is detectable rather than
        // indistinguishable from a genuinely short day.
        var raw = (await conn.QueryAsync<TargetRow>(new CommandDefinition(
            Sql,
            new { PracticeId = practiceId, LocalDate = date, RowLimit = _options.MaxRows + 1 },
            transaction: tx, cancellationToken: ct))).ToList();

        await tx.CommitAsync(ct);

        LastReadTruncated = raw.Count > _options.MaxRows;
        if (LastReadTruncated) raw = raw.Take(_options.MaxRows).ToList();

        return raw.Select(r => r.ToDomain(practiceId)).ToList();
    }

    private sealed record TargetRow
    {
        public long AppointmentId { get; init; }
        public DateOnly LocalDate { get; init; }
        public TimeOnly LocalTime { get; init; }
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

        public ScheduleRow ToDomain(string practiceId) => new()
        {
            PracticeId = practiceId,
            AppointmentId = AppointmentId,
            LocalDate = LocalDate,
            LocalTime = LocalTime,
            LengthUnits = LengthUnits,
            IsDeleted = IsDeleted,
            PatientId = PatientId,
            PatientLastName = PatientLastName,
            PatientFirstName = PatientFirstName,
            HomePhone = HomePhone,
            Balance = Balance,
            ProviderCode = ProviderCode,
            ProviderName = ProviderName,
            OperatoryCode = OperatoryCode,
            OperatoryName = OperatoryName,
            ProcedureCode = ProcedureCode,
            ProcedureDescription = ProcedureDescription,
            StatusCode = StatusCode,
            Note = Note,
        };
    }
}
