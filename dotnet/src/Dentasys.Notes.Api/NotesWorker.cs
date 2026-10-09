namespace Dentasys.Notes.Api;

/// <summary>
/// Drains the job queue and the chart outbox in the background. Several copies
/// of this process can run at once: claims are leased, so they share the work
/// rather than repeat it.
/// </summary>
public sealed class NotesWorker(JobRunner jobs, ChartWriter chart, ILogger<NotesWorker> log) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var done = await jobs.RunUntilIdleAsync(stoppingToken) + await chart.RunUntilIdleAsync(stoppingToken);
                if (done > 0) log.LogInformation("notes worker: {Count} item(s) processed", done);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Keep going: a worker that dies on one bad row stops delivering
                // notes and tells nobody, which is the 2 AM job all over again.
                log.LogError(ex, "notes worker cycle failed; continuing");
            }
            await Task.Delay(Idle, stoppingToken);
        }
    }
}
