using Dapper;
using Dentasys.CaptureAgent;
using Dentasys.Client;
using Dentasys.Notes.Api;
using Dentasys.Notetaker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dentasys.Notes.Api.Tests;

public sealed class SwitchableDrafter(string source, string complaint) : INoteDrafter
{
    public bool Down { get; set; }
    public string Source => source;
    public Task<DraftResult> DraftAsync(IReadOnlyList<TranscriptLine> transcript, CancellationToken ct = default) =>
        Task.FromResult(Down
            ? new DraftResult { Error = "HttpRequestException: no route to host" }
            : new DraftResult { Draft = new ClinicalNoteDraft { ChiefComplaint = complaint } });
}

/// <summary>Lets a fixed number of requests through, then drops the connection: an agent killed mid-upload.</summary>
public sealed class CutAfter(int requests) : DelegatingHandler
{
    private int _seen;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Interlocked.Increment(ref _seen) > requests
            ? throw new HttpRequestException("connection reset")
            : base.SendAsync(request, ct);
}

[CollectionDefinition("api", DisableParallelization = true)]
public sealed class ApiCollection : ICollectionFixture<NotesApiHost>;

/// <summary>
/// The notes service hosted in-process with its background workers off, so each
/// test runs the job and chart steps itself, at the moment it means to.
///
/// Uses its own database, DENTASYS_NOTES_API, built from the same script as
/// DENTASYS_NOTES: the other notes test assembly may run at the same time, and
/// two suites draining one queue would take each other's jobs.
/// </summary>
public sealed class NotesApiHost : IDisposable
{
    public const string Practice = "001204";
    public readonly SwitchableDrafter Cloud = new("test", "cloud draft");
    public readonly SwitchableDrafter Local = new("test", "local draft");
    public readonly WebApplicationFactory<Program> Factory;
    public readonly NotesOptions Options;

    private static string Server =>
        Environment.GetEnvironmentVariable("DENTASYS_LEGACY_CONNECTION")
        ?? "Server=localhost,11433;User Id=sa;Password=Dentasys!1997;TrustServerCertificate=true;Encrypt=false";

    public NotesApiHost()
    {
        CreateDatabase("DENTASYS_NOTES_API");
        Options = new NotesOptions
        {
            NotesConnectionString = new SqlConnectionStringBuilder(Server) { InitialCatalog = "DENTASYS_NOTES_API" }.ConnectionString,
            PracticeServerConnectionString = Server,
        };
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Notes:Workers", "false")
            .ConfigureServices(s =>
            {
                s.AddSingleton(Options);
                s.AddSingleton(new DrafterSet(Cloud, Local));
            }));
    }

    public HttpClient Http(params DelegatingHandler[] handlers) => Factory.CreateDefaultClient(handlers);
    public NotesApiClient Review() => new(Http());
    public T Service<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

    public async Task ProcessAsync()
    {
        await Service<JobRunner>().RunUntilIdleAsync();
        await Service<ChartWriter>().RunUntilIdleAsync();
    }

    public int NotesScalar(string sql, object args)
    {
        using var c = new SqlConnection(Options.NotesConnectionString);
        return c.ExecuteScalar<int>(sql, args);
    }

    public int ChartCount(Guid noteId)
    {
        using var c = new SqlConnection(new SqlConnectionStringBuilder(Server) { InitialCatalog = $"DENTASYS_{Practice}" }.ConnectionString);
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM CLINICAL_NOTE WHERE NOTE_ID = @noteId", new { noteId });
    }

    /// <summary>A fresh spool in a temp directory, with its own key.</summary>
    public static (Spool Spool, string Dir) NewSpool()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dentasys-spool-" + Guid.NewGuid().ToString("N"));
        return (new Spool(dir, Spool.LoadOrCreateKey(Path.Combine(dir, ".key"))), dir);
    }

    /// <summary>Records a synthetic visit into the spool, as the agent would from the operatory microphone.</summary>
    public static Guid Record(Spool spool, IReadOnlyList<TranscriptLine> transcript, int patientId = 41701)
    {
        var id = Guid.NewGuid();
        spool.Start(new CaptureManifest(id, Practice, patientId, null, "DDS1", ConsentRecorded: true, ChunkCount: null));
        var chunks = FixtureTranscriber.ToChunks(transcript, chunkBytes: 64);
        for (var i = 0; i < chunks.Count; i++) spool.AppendChunk(id, i, chunks[i]);
        spool.Finish(id, chunks.Count);
        return id;
    }

    public static readonly IReadOnlyList<TranscriptLine> Visit =
    [
        new("DENTIST", "Decay on nineteen is mesial and occlusal."),
        new("DENTIST", "Placing a mesial occlusal composite on nineteen."),
        new("PATIENT", "We took the boat out to Lake Travis."),
    ];

    private static void CreateDatabase(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "justfile"))) root = root.Parent;
        var script = File.ReadAllText(Path.Combine(root!.FullName, "legacy", "notes", "01_notes_store.sql"))
                         .Replace("DENTASYS_NOTES", name);

        using var c = new SqlConnection(Server);
        c.Open();
        foreach (var batch in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries))
            if (!string.IsNullOrWhiteSpace(batch)) c.Execute(batch);
    }

    public void Dispose() => Factory.Dispose();
}
