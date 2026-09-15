using System.Text.Json;
using Dapper;
using Dentasys.Domain;
using Npgsql;

namespace Dentasys.Data.Postgres;

/// <summary>
/// Writes, transactionally, with the events they produced.
///
/// Every command follows the same shape, and the shape is the design:
///
///   1. open a transaction
///   2. read what the rules need
///   3. ask the DOMAIN whether the command is allowed -- no rule logic lives here
///   4. apply the change
///   5. apply in-transaction reactions (the LAST_VISIT policy)
///   6. write the events to the outbox
///   7. commit, or roll all of it back
///
/// Steps 5 and 6 are deliberately different. The LAST_VISIT update is a
/// consistency requirement inside one practice's own data -- a completed
/// appointment and a stale last-visit date must never both be visible -- so it
/// commits with the change. The outbox is for consumers OUTSIDE this boundary,
/// which can tolerate arriving a second later. Making everything async because
/// events are fashionable would turn an invariant into a race.
/// </summary>
public sealed class PostgresAppointmentWriter : IAppointmentWriter
{
    private readonly PostgresOptions _options;

    public PostgresAppointmentWriter(PostgresOptions options) => _options = options;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task<CommandOutcome> BookAsync(BookAppointment cmd, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var (tz, grid) = await PracticeFactsAsync(conn, tx, cmd.PracticeId, ct);

        var occupied = (await conn.QueryAsync<OccupiedRow>(
            new CommandDefinition("""
                SELECT appointment_id AS AppointmentId, operatory_code::text AS OperatoryCode,
                       local_wall::time AS Start, length_units AS LengthUnits
                  FROM dentasys.appointment
                 WHERE practice_id = @PracticeId
                   AND local_wall::date = @LocalDate
                   AND NOT is_deleted
                """, new { cmd.PracticeId, cmd.LocalDate }, transaction: tx, cancellationToken: ct)))
            .Select(r => new OccupiedSlot(r.AppointmentId, r.OperatoryCode, r.Start, r.LengthUnits))
            .ToList();

        var verdict = BookingRules.Validate(cmd, tz, grid, occupied);
        if (!verdict.Accepted) return verdict;

        var appointmentId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT nextval('dentasys.appointment_id_seq')", transaction: tx, cancellationToken: ct));

        var local = cmd.LocalDate.ToDateTime(cmd.LocalTime);

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO dentasys.appointment
                (practice_id, appointment_id, patient_id, provider_code, operatory_code,
                 procedure_code, local_wall, practice_tz, start_utc, length_units,
                 local_time_nonexistent, local_time_ambiguous, status_code, note, is_deleted)
            VALUES
                (@PracticeId, @AppointmentId, @PatientId, @ProviderCode, @OperatoryCode,
                 @ProcedureCode, @LocalWall, @Tz,
                 CASE WHEN @Tz IS NULL THEN NULL ELSE @LocalWall AT TIME ZONE @Tz END,
                 @LengthUnits, false, false, 'B', @Note, false)
            """,
            new
            {
                cmd.PracticeId, AppointmentId = appointmentId, cmd.PatientId,
                cmd.ProviderCode, cmd.OperatoryCode, cmd.ProcedureCode,
                LocalWall = local, Tz = tz, cmd.LengthUnits, cmd.Note,
            }, transaction: tx, cancellationToken: ct));

        var booked = new AppointmentBooked
        {
            PracticeId = cmd.PracticeId, ActorId = cmd.ActorId,
            AppointmentId = appointmentId, PatientId = cmd.PatientId,
            LocalDate = cmd.LocalDate, LocalTime = cmd.LocalTime,
            OperatoryCode = cmd.OperatoryCode, ProviderCode = cmd.ProviderCode,
            LengthUnits = cmd.LengthUnits,
        };

        await AuditAsync(conn, tx, cmd.PracticeId, appointmentId, "booked", cmd.ActorId, booked, ct);
        await EnqueueAsync(conn, tx, booked, ct);
        await tx.CommitAsync(ct);

        return new CommandOutcome
        {
            Accepted = true, AppointmentId = appointmentId,
            Events = new DomainEvent[] { booked },
            SkippedChecks = verdict.SkippedChecks,
        };
    }

    public async Task<CommandOutcome> CompleteAsync(CompleteAppointment cmd, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var outcome = await CompleteAsync(conn, tx, cmd, ct);
        if (outcome.Accepted) await tx.CommitAsync(ct);
        return outcome;
    }

    /// <summary>
    /// Completes an appointment inside a transaction the CALLER owns and decides
    /// the fate of.
    ///
    /// This exists so the write-parity harness can exercise the real code path and
    /// then roll it back, leaving the fixtures pristine. Probing with a
    /// reimplementation of these statements would test the reimplementation, and
    /// the two would drift the first time either changed.
    /// </summary>
    public async Task<CommandOutcome> CompleteAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CompleteAppointment cmd, CancellationToken ct = default)
    {
        var appt = await conn.QuerySingleOrDefaultAsync<CompletionRow>(
            new CommandDefinition("""
                SELECT patient_id AS PatientId, local_wall::date AS LocalDate
                  FROM dentasys.appointment
                 WHERE practice_id = @PracticeId AND appointment_id = @AppointmentId
                """, new { cmd.PracticeId, cmd.AppointmentId }, transaction: tx, cancellationToken: ct));

        if (appt is null)
            return CommandOutcome.Rejected(new RuleViolation("appointment_not_found",
                $"No appointment {cmd.AppointmentId} at practice {cmd.PracticeId}."));

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dentasys.appointment SET status_code = 'C' WHERE practice_id = @PracticeId AND appointment_id = @AppointmentId",
            new { cmd.PracticeId, cmd.AppointmentId }, transaction: tx, cancellationToken: ct));

        var completed = new AppointmentCompleted
        {
            PracticeId = cmd.PracticeId, ActorId = cmd.ActorId,
            AppointmentId = cmd.AppointmentId,
            PatientId = appt.PatientId, LocalDate = appt.LocalDate,
        };

        // TR_APPT_AUDIT, as an explicit policy rather than a trigger nobody can
        // find. Applied for EVERY practice -- unlike the trigger, which exists on
        // 8 of 24 and silently does nothing at the other 16 (LANDMINE #7).
        if (PatientLastVisitPolicy.Apply(completed) is { } effect)
        {
            var (patientId, lastVisit) = effect;
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE dentasys.patient SET last_visit = @LastVisit
                 WHERE practice_id = @PracticeId AND patient_id = @PatientId
                """, new { cmd.PracticeId, PatientId = patientId, LastVisit = lastVisit },
                transaction: tx, cancellationToken: ct));
        }

        await AuditAsync(conn, tx, cmd.PracticeId, cmd.AppointmentId, "completed", cmd.ActorId, completed, ct);
        await EnqueueAsync(conn, tx, completed, ct);

        return new CommandOutcome
        {
            Accepted = true, AppointmentId = cmd.AppointmentId,
            Events = new DomainEvent[] { completed },
        };
    }

    public async Task<CommandOutcome> CancelAsync(CancelAppointment cmd, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var affected = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dentasys.appointment SET is_deleted = true
             WHERE practice_id = @PracticeId AND appointment_id = @AppointmentId AND NOT is_deleted
            """, new { cmd.PracticeId, cmd.AppointmentId }, transaction: tx, cancellationToken: ct));

        if (affected == 0)
            return CommandOutcome.Rejected(new RuleViolation("appointment_not_cancellable",
                $"Appointment {cmd.AppointmentId} does not exist or is already cancelled."));

        var cancelled = new AppointmentCancelled
        {
            PracticeId = cmd.PracticeId, ActorId = cmd.ActorId,
            AppointmentId = cmd.AppointmentId, Reason = cmd.Reason,
        };

        await AuditAsync(conn, tx, cmd.PracticeId, cmd.AppointmentId, "cancelled", cmd.ActorId, cancelled, ct);
        await EnqueueAsync(conn, tx, cancelled, ct);
        await tx.CommitAsync(ct);

        return new CommandOutcome
        {
            Accepted = true, AppointmentId = cmd.AppointmentId,
            Events = new DomainEvent[] { cancelled },
        };
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_options.ConnectionString);
        await conn.OpenAsync(ct);
        await using var timeout = new NpgsqlCommand($"SET statement_timeout = {_options.StatementTimeoutMs}", conn);
        await timeout.ExecuteNonQueryAsync(ct);
        return conn;
    }

    private static async Task<(string? Tz, int? Grid)> PracticeFactsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string practiceId, CancellationToken ct)
    {
        var row = await conn.QuerySingleOrDefaultAsync<PracticeFactsRow>(new CommandDefinition("""
            SELECT p.practice_tz AS Tz, c.appointment_grid_minutes AS Grid
              FROM dentasys.practice p
              LEFT JOIN dentasys.practice_config c ON c.practice_id = p.practice_id
             WHERE p.practice_id = @practiceId
            """, new { practiceId }, transaction: tx, cancellationToken: ct));
        return (row?.Tz, row?.Grid);
    }

    // Dapper does not map ValueTuples -- it silently returns nothing, which turns a
    // failed read into a plausible-looking "not found". Small records instead.
    private sealed record OccupiedRow(long AppointmentId, string OperatoryCode, TimeOnly Start, int? LengthUnits);
    private sealed record CompletionRow(long PatientId, DateOnly LocalDate);
    private sealed record PracticeFactsRow(string? Tz, int? Grid);

    private static Task EnqueueAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, DomainEvent e, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO dentasys.outbox (event_id, practice_id, event_type, occurred_at, actor_id, payload)
            VALUES (@EventId, @PracticeId, @EventType, @OccurredAt, @ActorId, @Payload::jsonb)
            """,
            new
            {
                e.EventId, e.PracticeId, e.EventType, e.OccurredAt, e.ActorId,
                Payload = JsonSerializer.Serialize(e, e.GetType(), Json),
            }, transaction: tx, cancellationToken: ct));

    private static Task AuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string practiceId, long appointmentId,
        string action, string actorId, DomainEvent e, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO dentasys.appointment_audit (practice_id, appointment_id, action, actor_id, detail)
            VALUES (@practiceId, @appointmentId, @action, @actorId, @detail::jsonb)
            """,
            new
            {
                practiceId, appointmentId, action, actorId,
                detail = JsonSerializer.Serialize(e, e.GetType(), Json),
            }, transaction: tx, cancellationToken: ct));
}
