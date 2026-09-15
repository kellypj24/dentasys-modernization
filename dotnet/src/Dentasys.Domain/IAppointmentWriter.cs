namespace Dentasys.Domain;

/// <summary>
/// The write side of the appointment book.
///
/// Implementations are responsible for atomicity and for recording the events a
/// command produced. They are NOT responsible for deciding whether a command is
/// allowed -- that is <see cref="BookingRules"/>, which needs no database and is
/// tested without one.
/// </summary>
public interface IAppointmentWriter
{
    Task<CommandOutcome> BookAsync(BookAppointment cmd, CancellationToken ct = default);
    Task<CommandOutcome> CompleteAsync(CompleteAppointment cmd, CancellationToken ct = default);
    Task<CommandOutcome> CancelAsync(CancelAppointment cmd, CancellationToken ct = default);
}
