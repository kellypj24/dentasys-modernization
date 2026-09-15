using Dapper;
using Dentasys.Domain;
using Microsoft.Data.SqlClient;

namespace Dentasys.Data.SqlServer;

/// <summary>
/// Reads the 1997 schema directly. Notably it does NOT call usp_GetScheduleForDay:
/// the point of the migration is that the procedure stops being the thing that
/// knows how a schedule works, so the app reads tables and applies its own rules.
///
/// The procedure still exists on the legacy server and the parity harness still
/// runs it -- as the oracle to check the new behavior against, not as a dependency.
///
/// Everything this class does is translation. CHAR(8) 'YYYYMMDD' and CHAR(4) 'HHMM'
/// become DateOnly and TimeOnly; a FLOAT balance becomes decimal; the four-valued
/// delete flag goes through the shared domain rule rather than a SQL predicate.
/// One database per practice, so the practice id selects the database, not a column.
/// </summary>
public sealed class SqlServerScheduleRepository : IScheduleRepository
{
    private readonly string _serverConnectionString;

    public SqlServerScheduleRepository(string serverConnectionString) =>
        _serverConnectionString = serverConnectionString;

    public string ProviderName => "SQL Server (legacy)";

    // NOLOCK is deliberately absent. The legacy proc has it on every table because
    // a consultant added it in 2004 instead of fixing indexes (LANDMINE #4). It
    // buys dirty reads and nothing else here, and read-committed is the correct
    // default for a screen someone books appointments from.
    private const string Sql = """
        SELECT  a.APPT_ID     AS AppointmentId,
                a.APPT_DT     AS ApptDate,
                a.APPT_TM     AS ApptTime,
                a.LEN_UNITS   AS LengthUnits,
                a.DEL_FLG     AS DeleteFlag,
                a.PAT_ID      AS PatientId,
                p.LAST_NM     AS PatientLastName,
                p.FIRST_NM    AS PatientFirstName,
                p.HOME_PHONE  AS HomePhone,
                p.BAL_AMT     AS Balance,
                a.PROV_CD     AS ProviderCode,
                v.PROV_NM     AS ProviderName,
                a.OPER_CD     AS OperatoryCode,
                o.OPER_NM     AS OperatoryName,
                a.PROC_CD     AS ProcedureCode,
                c.PROC_DESC   AS ProcedureDescription,
                a.APPT_STAT   AS StatusCode,
                a.NOTE_TXT    AS Note
          FROM  APPT a
          LEFT JOIN PAT_MSTR  p ON p.PAT_ID  = a.PAT_ID
          LEFT JOIN PROV      v ON v.PROV_CD = a.PROV_CD
          LEFT JOIN OPER      o ON o.OPER_CD = a.OPER_CD
          LEFT JOIN PROC_CODE c ON c.PROC_CD = a.PROC_CD
         WHERE  a.APPT_DT = @ApptDate;
        """;

    public async Task<IReadOnlyList<ScheduleRow>> GetDayAsync(
        string practiceId, DateOnly date, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = $"DENTASYS_{practiceId}"
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        var raw = await conn.QueryAsync<LegacyRow>(
            new CommandDefinition(Sql, new { ApptDate = date.ToString("yyyyMMdd") }, cancellationToken: ct));

        return raw.Select(r => r.ToDomain(practiceId)).ToList();
    }

    private sealed record LegacyRow
    {
        public int AppointmentId { get; init; }
        public string ApptDate { get; init; } = "";
        public string ApptTime { get; init; } = "";
        public short? LengthUnits { get; init; }
        public string? DeleteFlag { get; init; }
        public int? PatientId { get; init; }
        public string? PatientLastName { get; init; }
        public string? PatientFirstName { get; init; }
        public string? HomePhone { get; init; }
        public double? Balance { get; init; }
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
            LocalDate = DateOnly.ParseExact(ApptDate.Trim(), "yyyyMMdd"),
            LocalTime = TimeOnly.ParseExact(ApptTime.Trim().PadLeft(4, '0'), "HHmm"),
            LengthUnits = LengthUnits,
            IsDeleted = LegacyFlags.IsDeleted(DeleteFlag),
            PatientId = PatientId,
            PatientLastName = PatientLastName,
            PatientFirstName = PatientFirstName,
            HomePhone = HomePhone?.TrimEnd(),
            // FLOAT -> decimal, rounded to the cent. The source cannot represent
            // 0.10 exactly (LANDMINE #2), so this rounding is where the legacy
            // value and the migrated value part company -- by design, and by less
            // than half a cent. The parity harness is told to expect exactly that.
            Balance = Balance is null ? null : Math.Round((decimal)Balance.Value, 2, MidpointRounding.ToEven),
            ProviderCode = ProviderCode?.TrimEnd(),
            ProviderName = ProviderName,
            OperatoryCode = OperatoryCode?.TrimEnd(),
            OperatoryName = OperatoryName,
            ProcedureCode = ProcedureCode?.TrimEnd(),
            ProcedureDescription = ProcedureDescription,
            StatusCode = StatusCode?.TrimEnd(),
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note,
        };
    }
}
