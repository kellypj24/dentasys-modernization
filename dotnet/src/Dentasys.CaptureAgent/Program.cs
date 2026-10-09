using System.Text;
using System.Text.Json;
using Dentasys.CaptureAgent;
using Dentasys.Notetaker;

// capture-agent record <visitId> --practice ID --patient N [--spool DIR]
// capture-agent drain [--spool DIR] [--url http://localhost:5181/]
//
// The lab's operatory microphone is a synthetic visit: `record` writes its
// transcript into the encrypted spool as audio chunks, exactly as the real agent
// would write audio. `drain` uploads whatever is waiting and can be re-run after
// any failure.

string Opt(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var spoolDir = Opt("--spool", Path.Combine(Path.GetTempPath(), "dentasys-capture-spool"));
var spool = new Spool(spoolDir, Spool.LoadOrCreateKey(Path.Combine(spoolDir, ".key")));

switch (args.FirstOrDefault())
{
    case "record" when args.Length >= 2:
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "justfile"))) root = root.Parent;
        var visit = Visit.LoadAll(Path.Combine(root!.FullName, "notetaker", "visits")).Single(v => v.Id == args[1]);

        var id = Guid.NewGuid();
        spool.Start(new CaptureManifest(id, Opt("--practice", "001204"), int.Parse(Opt("--patient", "41701")),
                                        null, Opt("--provider", "DDS1"), ConsentRecorded: true,
                                        AudioFormat: "application/x-dentasys-transcript+json", ChunkCount: null));
        var audio = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(visit.Transcript, ClinicalNoteDraft.Json)).Chunk(256).ToList();
        for (var i = 0; i < audio.Count; i++) spool.AppendChunk(id, i, audio[i]);
        spool.Finish(id, audio.Count);
        Console.WriteLine($"recorded {visit.Id} as capture {id}: {audio.Count} encrypted chunks in {spoolDir}");
        return 0;
    }

    case "drain":
    {
        using var http = new HttpClient { BaseAddress = new Uri(Opt("--url", "http://localhost:5181/")), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new("Bearer",
            Environment.GetEnvironmentVariable("DENTASYS_AGENT_TOKEN") ?? throw new InvalidOperationException("set DENTASYS_AGENT_TOKEN"));
        var r = await new Uploader(spool, http).DrainAsync();
        Console.WriteLine($"sent {r.ChunksSent} chunk(s), completed {r.CapturesCompleted} capture(s)" +
                          (r.Offline ? "; service unreachable, will retry" : ""));
        foreach (var p in r.Problems) Console.WriteLine($"  problem: {p}");
        Console.WriteLine($"{spool.Captures().Count} capture(s) still in the spool");
        return r.Problems.Count == 0 ? 0 : 1;
    }

    default:
        Console.Error.WriteLine("usage: capture-agent record <visitId> --practice ID --patient N | drain [--url URL]");
        return 2;
}
