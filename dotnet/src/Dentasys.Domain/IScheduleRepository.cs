namespace Dentasys.Domain;

/// <summary>
/// The only thing a database is allowed to do in this application: hand back rows.
///
/// No filtering of soft-deletes, no name formatting, no duration arithmetic, no
/// ordering. Every one of those used to live in usp_GetScheduleForDay and every
/// one of them now lives in <see cref="ScheduleService"/>, which is why the same
/// behavior can be guaranteed across two engines without writing it twice.
///
/// Implementations may push the practice/date restriction down to the server --
/// that is an index-usage concern, not business logic, and hauling a whole
/// practice's history over the wire to filter it in C# would be daft.
/// </summary>
public interface IScheduleRepository
{
    /// <summary>A human-readable name for the backing engine, for display and reports.</summary>
    string ProviderName { get; }

    Task<IReadOnlyList<ScheduleRow>> GetDayAsync(
        string practiceId, DateOnly date, CancellationToken ct = default);
}
