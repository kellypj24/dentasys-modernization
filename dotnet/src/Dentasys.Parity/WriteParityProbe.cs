using Dapper;
using Dentasys.Data.Postgres;
using Dentasys.Domain;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Dentasys.Parity;

/// <summary>
/// What each system does to a patient's last-visit date when an appointment is
/// marked completed.
///
/// Both sides run inside a transaction that is rolled back, so the probe observes
/// the real code path and leaves the fixtures exactly as it found them. A harness
/// that mutates the data it measures produces a different answer on every run.
/// </summary>
public sealed record LastVisitProbe(
    string PracticeId,
    long AppointmentId,
    long PatientId,
    DateOnly AppointmentDate,
    DateOnly? LegacyLastVisitAfter,
    DateOnly? ModernLastVisitAfter,
    bool LegacyHasTrigger)
{
    public bool LegacyUpdated => LegacyLastVisitAfter == AppointmentDate;
    public bool ModernUpdated => ModernLastVisitAfter == AppointmentDate;
    public bool Diverges => LegacyUpdated != ModernUpdated;
}

public sealed class WriteParityProbe
{
    private readonly string _legacyConnection;
    private readonly PostgresOptions _target;

    public WriteParityProbe(string legacyConnection, PostgresOptions target)
    {
        _legacyConnection = legacyConnection;
        _target = target;
    }

    public async Task<LastVisitProbe?> ProbeCompleteAsync(string practiceId, CancellationToken ct = default)
    {
        var legacyDb = new SqlConnectionStringBuilder(_legacyConnection)
        {
            InitialCatalog = $"DENTASYS_{practiceId}"
        }.ConnectionString;

        await using var sql = new SqlConnection(legacyDb);
        await sql.OpenAsync(ct);

        // Pick a live, not-yet-completed appointment deterministically so the probe
        // is comparable between runs and between practices.
        var target = await sql.QuerySingleOrDefaultAsync<CandidateRow>(
            new CommandDefinition("""
                SELECT TOP 1 a.APPT_ID AS ApptId, a.PAT_ID AS PatId, a.APPT_DT AS ApptDt
                  FROM APPT a
                 WHERE ISNULL(a.DEL_FLG,'N') <> 'Y' AND a.APPT_STAT <> 'C' AND a.PAT_ID IS NOT NULL
                 ORDER BY a.APPT_ID
                """, cancellationToken: ct));

        if (target is null) return null;

        var (apptId, patId, apptDt) = (target.ApptId, target.PatId, target.ApptDt);
        var apptDate = DateOnly.ParseExact(apptDt.Trim(), "yyyyMMdd");

        var hasTrigger = await sql.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM sys.triggers WHERE name = 'TR_APPT_AUDIT'", cancellationToken: ct)) > 0;

        // ---- legacy: the fat client's UPDATE, and whatever the database does next
        DateOnly? legacyAfter;
        await using (var tx = (SqlTransaction)await sql.BeginTransactionAsync(ct))
        {
            await sql.ExecuteAsync(new CommandDefinition(
                "UPDATE APPT SET APPT_STAT = 'C' WHERE APPT_ID = @apptId",
                new { apptId }, transaction: tx, cancellationToken: ct));

            var raw = await sql.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT LAST_VISIT FROM PAT_MSTR WHERE PAT_ID = @patId",
                new { patId }, transaction: tx, cancellationToken: ct));

            legacyAfter = string.IsNullOrWhiteSpace(raw)
                ? null : DateOnly.ParseExact(raw.Trim(), "yyyyMMdd");

            await tx.RollbackAsync(ct);
        }

        // ---- modern: the same command through the real writer
        DateOnly? modernAfter;
        await using (var pg = new NpgsqlConnection(_target.ConnectionString))
        {
            await pg.OpenAsync(ct);
            await using var tx = await pg.BeginTransactionAsync(ct);

            var writer = new PostgresAppointmentWriter(_target);
            var outcome = await writer.CompleteAsync(pg, tx, new CompleteAppointment
            {
                PracticeId = practiceId,
                AppointmentId = apptId,
                ActorId = "parity-probe",
            }, ct);

            // Throw rather than record a null. An earlier version ignored this, so a
            // REJECTED command read back exactly like an accepted one that changed
            // nothing -- the probe reported "modern updated 0 of 24" and looked like
            // a policy bug when it was actually a failed lookup. A probe that cannot
            // tell "it declined" from "it did nothing" measures nothing.
            if (!outcome.Accepted)
                throw new InvalidOperationException(
                    $"probe could not complete appointment {apptId} at {practiceId}: " +
                    string.Join("; ", outcome.Violations));

            modernAfter = await pg.ExecuteScalarAsync<DateOnly?>(new CommandDefinition(
                "SELECT last_visit FROM dentasys.patient WHERE practice_id = @practiceId AND patient_id = @patId",
                new { practiceId, patId = (long)patId }, transaction: tx, cancellationToken: ct));

            await tx.RollbackAsync(ct);
        }

        return new LastVisitProbe(practiceId, apptId, patId, apptDate, legacyAfter, modernAfter, hasTrigger);
    }

    private sealed record CandidateRow(int ApptId, int PatId, string ApptDt);
}
