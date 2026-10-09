namespace Dentasys.Notes;

public sealed class NotesOptions
{
    /// <summary>DENTASYS_NOTES.</summary>
    public required string NotesConnectionString { get; init; }

    /// <summary>Server-level connection; the catalog is swapped per practice (DENTASYS_{practice}).</summary>
    public required string PracticeServerConnectionString { get; init; }

    /// <summary>How long a claimed job or chart write belongs to one worker before another may take it.</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>First retry delay; doubles per attempt up to <see cref="RetryCap"/>.</summary>
    public TimeSpan RetryBase { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryCap { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a chart write waits when the practice has not installed 07.04.00.
    /// Long on purpose: an upgrade window is days away, not seconds, and retrying
    /// every 30 s against 22 practices would be noise in their server logs.
    /// </summary>
    public TimeSpan ChartHold { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Audio formats captures may declare: the transcriber's, set where the service is composed.</summary>
    public IReadOnlyCollection<string> AudioFormats { get; init; } = [FixtureTranscriber.Format];

    public TimeSpan RetryDelay(int attempts) =>
        TimeSpan.FromTicks(Math.Min(RetryCap.Ticks, RetryBase.Ticks * (1L << Math.Clamp(attempts - 1, 0, 20))));
}
