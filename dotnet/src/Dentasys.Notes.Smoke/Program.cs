using Dapper;
using Dentasys.Notes;
using Dentasys.Notetaker;
using Microsoft.Data.SqlClient;

// notes-smoke --model gemma3:4b [--visits v001,v006,v016] [--pause 10]
//
// Runs a few synthetic visits through the whole notes service with a real
// local model, the cloud unreachable on purpose: capture and chunk upload,
// transcription, the cloud call failing with a refused connection, the local
// fallback, signing, and the write to CLINICAL_NOTE. A plumbing check, not an
// eval -- a handful of drafts, paced, so a laptop stays cool.

string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var model = Arg("--model", "gemma3:4b");
var ids = Arg("--visits", "v001,v006,v016").Split(',');
var pause = TimeSpan.FromSeconds(double.Parse(Arg("--pause", "10")));
const string practice = "001204";   // has installed 07.04.00

var server = Environment.GetEnvironmentVariable("DENTASYS_LEGACY_CONNECTION")
    ?? "Server=localhost,11433;User Id=sa;Password=Dentasys!1997;TrustServerCertificate=true;Encrypt=false";
var options = new NotesOptions
{
    NotesConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = "DENTASYS_NOTES" }.ConnectionString,
    PracticeServerConnectionString = server,
};

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "justfile"))) root = root.Parent;
var visits = Visit.LoadAll(Path.Combine(root!.FullName, "notetaker", "visits"))
    .Where(v => ids.Contains(v.Id)).ToList();

// Port 9 (discard) has no listener: the "cloud" call fails with a real refused
// connection, which is what the data center sees when its link to the cloud drops.
using var deadLink = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/"), Timeout = TimeSpan.FromSeconds(5) };
using var ollama = new HttpClient
{
    BaseAddress = new Uri(Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434/"),
    Timeout = TimeSpan.FromMinutes(4),
};
var cloud = new OllamaNoteDrafter(deadLink, "cloud-unreachable");
var local = new OllamaNoteDrafter(ollama, model);

var clock = TimeProvider.System;
var service = new NotesService(options, clock);
var runner = new JobRunner(options, new FixtureTranscriber(), cloud, local, clock);
var chart = new ChartWriter(options, clock);

Console.WriteLine($"notes smoke: {visits.Count} visits, cloud unreachable, local {local.Source}, practice {practice}");
var failures = 0;
foreach (var visit in visits)
{
    if (visit != visits[0]) await Task.Delay(pause);

    var id = Guid.NewGuid();
    await service.RegisterCaptureAsync(new CaptureRegistration(id, practice, 120400 + visits.IndexOf(visit), null, "DDS1", true));
    var chunks = FixtureTranscriber.ToChunks(visit.Transcript);
    for (var i = 0; i < chunks.Count; i++) await service.PutChunkAsync(id, i, chunks[i]);
    await service.CompleteUploadAsync(id, chunks.Count);

    var clockStart = DateTime.UtcNow;
    await runner.RunUntilIdleAsync();
    var note = await service.GetAsync(id);
    var seconds = (DateTime.UtcNow - clockStart).TotalSeconds;

    if (note?.State != NoteState.Drafted || note.Current is null)
    {
        failures++;
        var error = Scalar<string?>("SELECT TOP 1 last_error FROM notes.job WHERE note_id = @id AND last_error IS NOT NULL", new { id });
        Console.WriteLine($"  {visit.Id}  NOT DRAFTED  state={note?.State}  {error}");
        continue;
    }

    var score = NoteScorer.Score(visit, note.Current);
    await service.SignAsync(id, note.CurrentVersion!.Value, "smoke");
    await chart.RunUntilIdleAsync();

    var charted = (await service.GetAsync(id))!.State == NoteState.Charted;
    var text = PracticeScalar<string?>("SELECT NOTE_TEXT FROM CLINICAL_NOTE WHERE NOTE_ID = @id", new { id });
    var cloudError = Scalar<string?>("SELECT detail FROM notes.audit WHERE note_id = @id AND action = 'drafted_locally'", new { id });
    if (!charted || text is null) failures++;

    Console.WriteLine($"  {visit.Id}  {note.CurrentSource,-22} {seconds,4:0}s  recall {score.Recall,4:P0}  " +
                      $"signed -> {(charted ? "CLINICAL_NOTE" : "NOT CHARTED")}  ({cloudError})");
    Console.WriteLine("      " + (text ?? "(none)").Split('\n').FirstOrDefault());
}

var pending = Scalar<int>("SELECT COUNT(*) FROM notes.job WHERE state = 'pending' AND kind = 'redraft'", new { });
Console.WriteLine($"\n  {pending} redraft job(s) queued for when the cloud returns (each note is signed, so they will be abandoned)");
Console.WriteLine(failures == 0 ? "  plumbing: OK" : $"  plumbing: {failures} FAILED");
return failures == 0 ? 0 : 1;

T Scalar<T>(string sql, object args)
{
    using var c = new SqlConnection(options.NotesConnectionString);
    return c.ExecuteScalar<T>(sql, args)!;
}

T PracticeScalar<T>(string sql, object args)
{
    using var c = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = $"DENTASYS_{practice}" }.ConnectionString);
    return c.ExecuteScalar<T>(sql, args)!;
}
