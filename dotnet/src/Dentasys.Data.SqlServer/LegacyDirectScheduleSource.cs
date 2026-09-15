using System.Diagnostics;
using Dentasys.Domain;


namespace Dentasys.Data.SqlServer;

/// <summary>
/// The legacy path, modelled honestly: the workstation opens its own connection
/// to SQL Server and queries tables directly.
///
/// This lives in the App rather than behind the API on purpose. It is what we are
/// migrating AWAY from, and the structure should say so -- the fat client holds a
/// connection string, and there is nowhere to put a timeout or a row cap that a
/// workstation could not simply decline to use.
///
/// The business rules are still the shared ones from Dentasys.Domain. That keeps
/// the comparison honest: when the two paths differ, it is the DATA or the
/// PLUMBING that differs, never the rules.
/// </summary>
public sealed class LegacyDirectScheduleSource : IScheduleSource
{
    private readonly IScheduleRepository _repo;
    private readonly ScheduleService _scheduler;
    private readonly int? _gridMinutes;

    public LegacyDirectScheduleSource(IScheduleRepository repo, ScheduleService scheduler, int? gridMinutes)
    {
        _repo = repo;
        _scheduler = scheduler;
        _gridMinutes = gridMinutes;
    }

    public string Description => "direct connection -> SQL Server (no gate)";

    public async Task<ScheduleResult> GetScheduleAsync(ScheduleQuery query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var rows = await _repo.GetDayAsync(query.PracticeId, query.Date, ct);
        var slots = _scheduler.Build(rows, query with { GridMinutes = _gridMinutes });
        sw.Stop();

        return new ScheduleResult
        {
            Slots = slots,
            Source = "fat client -> SQL Server",
            Elapsed = sw.Elapsed,
            // Nothing capped it, so nothing can say whether it should have been.
            Truncated = false,
            GridMinutes = _gridMinutes,
        };
    }
}
