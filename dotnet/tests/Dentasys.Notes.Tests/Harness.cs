using Dapper;
using Dentasys.Notetaker;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Dentasys.Notes.Tests;

/// <summary>A clock the test moves, so backoff, leases and holds are checked without waiting.</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A drafter whose network can be cut. Stands in for the cloud or the local model.</summary>
public sealed class SwitchableDrafter(string source, string complaint) : INoteDrafter
{
    public bool Down { get; set; }
    public int Calls { get; private set; }
    public string Source => source;

    public Task<DraftResult> DraftAsync(IReadOnlyList<TranscriptLine> transcript, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Down
            ? new DraftResult { Error = "HttpRequestException: no route to host" }
            : new DraftResult { Draft = new ClinicalNoteDraft { ChiefComplaint = complaint } });
    }
}

/// <summary>A drafter that hangs until released: a model call still in flight when its lease runs out.</summary>
public sealed class BlockingDrafter(string source) : INoteDrafter
{
    private readonly TaskCompletionSource _release = new();
    public TaskCompletionSource Entered { get; } = new();
    public string Source => source;
    public void Release() => _release.SetResult();

    public async Task<DraftResult> DraftAsync(IReadOnlyList<TranscriptLine> transcript, CancellationToken ct = default)
    {
        Entered.SetResult();
        await _release.Task;
        return new DraftResult { Draft = new ClinicalNoteDraft { ChiefComplaint = "slow draft" } };
    }
}

[CollectionDefinition("notes", DisableParallelization = true)]
public sealed class NotesCollection : ICollectionFixture<NotesDb>;

/// <summary>
/// The lab's DENTASYS_NOTES, emptied once per run. Tests share the queues, so
/// they run one at a time and each drains what it creates.
/// </summary>
public sealed class NotesDb
{
    public const string Upgraded = "001204";     // has installed 07.04.00
    public const string NotUpgraded = "000417";  // has not

    public static string Server =>
        Environment.GetEnvironmentVariable("DENTASYS_LEGACY_CONNECTION")
        ?? "Server=localhost,11433;User Id=sa;Password=Dentasys!1997;TrustServerCertificate=true;Encrypt=false";

    public NotesOptions Options { get; } = new()
    {
        NotesConnectionString = new SqlConnectionStringBuilder(Server) { InitialCatalog = Database }.ConnectionString,
        PracticeServerConnectionString = Server,
    };

    /// <summary>
    /// Its own database, rebuilt from the store script on every run, so the
    /// queues start empty and nothing this suite leaves behind shows up in the
    /// DENTASYS_NOTES a running notes service uses.
    /// </summary>
    private const string Database = "DENTASYS_NOTES_TEST";

    public NotesDb()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "justfile"))) root = root.Parent;
        var script = File.ReadAllText(Path.Combine(root!.FullName, "legacy", "notes", "01_notes_store.sql"))
                         .Replace("DENTASYS_NOTES", Database);

        using var conn = new SqlConnection(Server);
        conn.Open();
        foreach (var batch in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries))
            if (!string.IsNullOrWhiteSpace(batch)) conn.Execute(batch);
    }

    public static IReadOnlyList<TranscriptLine> Transcript =>
    [
        new("DENTIST", "Decay on nineteen is mesial and occlusal."),
        new("DENTIST", "Placing a mesial occlusal composite on nineteen."),
    ];

    /// <summary>Registers a capture and uploads every chunk, as the capture agent would.</summary>
    public async Task<Guid> CaptureAsync(NotesService service, string practiceId)
    {
        var id = Guid.NewGuid();
        await service.RegisterCaptureAsync(new CaptureRegistration(id, practiceId, 41701, 417001, "DDS1", ConsentRecorded: true));
        var chunks = FixtureTranscriber.ToChunks(Transcript, chunkBytes: 64);
        for (var i = 0; i < chunks.Count; i++) await service.PutChunkAsync(id, i, chunks[i]);
        var done = await service.CompleteUploadAsync(id, chunks.Count);
        Assert.True(done.Complete);
        return id;
    }

    public T Scalar<T>(string sql, object args)
    {
        using var conn = new SqlConnection(Options.NotesConnectionString);
        return conn.ExecuteScalar<T>(sql, args)!;
    }

    public T PracticeScalar<T>(string practiceId, string sql, object args)
    {
        using var conn = new SqlConnection(new SqlConnectionStringBuilder(Server) { InitialCatalog = $"DENTASYS_{practiceId}" }.ConnectionString);
        return conn.ExecuteScalar<T>(sql, args)!;
    }

    public void PracticeExecute(string practiceId, string sql, object? args = null)
    {
        using var conn = new SqlConnection(new SqlConnectionStringBuilder(Server) { InitialCatalog = $"DENTASYS_{practiceId}" }.ConnectionString);
        conn.Execute(sql, args);
    }
}
