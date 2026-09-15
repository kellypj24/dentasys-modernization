using Dapper;
using Dentasys.Domain;
using Microsoft.Data.SqlClient;

namespace Dentasys.Parity;

/// <summary>
/// Runs usp_GetScheduleForDay and shapes its output like a ScheduleSlot.
///
/// This is the oracle. It is the only thing in the solution that executes the
/// stored procedure, and it exists so the new C# can be checked against 29 years
/// of actual behavior rather than against what the procedure was supposed to do.
///
/// Values come back exactly as the proc emits them, artefacts included -- the
/// "0860" end times stay "0860". Cleaning them up here would quietly delete the
/// evidence the harness is supposed to weigh.
///
/// That is not a hypothetical. An earlier version of this class parsed the end
/// time and stored null when it failed, which is what "0860" does. The target
/// also returns null when the grid is unknown, so null met null and the harness
/// cheerfully reported AGREEMENT on every row where the legacy system emits a
/// value that is not a time. A harness that launders the defect it exists to find
/// is worse than no harness. The raw text is carried through unparsed.
/// </summary>
public sealed class LegacyProcedureReader
{
    private readonly string _serverConnectionString;

    public LegacyProcedureReader(string serverConnectionString) =>
        _serverConnectionString = serverConnectionString;

    /// <summary>
    /// The procedure's output, plus the raw APPT_END_TM text exactly as emitted.
    /// </summary>
    public sealed record Result(
        IReadOnlyList<ScheduleSlot> Slots,
        IReadOnlyDictionary<long, string?> RawEndTimes);

    public async Task<Result> RunAsync(
        string practiceId, DateOnly date, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(_serverConnectionString)
        {
            InitialCatalog = $"DENTASYS_{practiceId}"
        };

        await using var conn = new SqlConnection(builder.ConnectionString);
        var rows = await conn.QueryAsync<ProcRow>(new CommandDefinition(
            "usp_GetScheduleForDay",
            new { APPT_DT = date.ToString("yyyyMMdd") },
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));

        var list = rows.ToList();
        return new Result(
            list.Select(Map).ToList(),
            list.ToDictionary(r => (long)r.APPT_ID, r => r.APPT_END_TM?.Trim()));
    }

    private static ScheduleSlot Map(ProcRow r)
    {
        // "0860" is not parseable as a time, and that is the finding, not an error
        // to swallow. Unparseable values become null and the comparison records the
        // legacy text alongside, so the report can show what the proc actually said.
        TimeOnly? end = TimeOnly.TryParseExact(r.APPT_END_TM?.Trim() ?? "", "HHmm", out var parsed)
            ? parsed : null;

        return new ScheduleSlot
        {
            AppointmentId = r.APPT_ID,
            LocalDate = DateOnly.ParseExact(r.APPT_DT.Trim(), "yyyyMMdd"),
            LocalTime = TimeOnly.ParseExact(r.APPT_TM.Trim().PadLeft(4, '0'), "HHmm"),
            LengthUnits = r.LEN_UNITS,
            DurationMinutes = r.APPT_MINUTES,
            EndTime = end,
            PatientId = r.PAT_ID,
            PatientName = r.PAT_NM,
            HomePhone = r.HOME_PHONE?.TrimEnd(),
            Balance = r.BAL_AMT is null ? null : Math.Round((decimal)r.BAL_AMT.Value, 2, MidpointRounding.ToEven),
            ProviderCode = r.PROV_CD?.TrimEnd(),
            ProviderName = r.PROV_NM,
            OperatoryCode = r.OPER_CD?.TrimEnd(),
            OperatoryName = r.OPER_NM,
            ProcedureCode = r.PROC_CD?.TrimEnd(),
            ProcedureDescription = r.PROC_DESC,
            StatusCode = r.APPT_STAT?.TrimEnd(),
            Note = string.IsNullOrWhiteSpace(r.NOTE_TXT) ? null : r.NOTE_TXT,
        };
    }

    private sealed record ProcRow
    {
        public int APPT_ID { get; init; }
        public string APPT_DT { get; init; } = "";
        public string APPT_TM { get; init; } = "";
        public short? LEN_UNITS { get; init; }
        public int? APPT_MINUTES { get; init; }
        public string? APPT_END_TM { get; init; }
        public int? PAT_ID { get; init; }
        public string? PAT_NM { get; init; }
        public string? HOME_PHONE { get; init; }
        public double? BAL_AMT { get; init; }
        public string? PROV_CD { get; init; }
        public string? PROV_NM { get; init; }
        public string? OPER_CD { get; init; }
        public string? OPER_NM { get; init; }
        public string? PROC_CD { get; init; }
        public string? PROC_DESC { get; init; }
        public string? APPT_STAT { get; init; }
        public string? NOTE_TXT { get; init; }
    }
}
