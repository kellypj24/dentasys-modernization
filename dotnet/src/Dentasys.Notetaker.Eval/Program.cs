using System.Text.Json;
using Dentasys.Notetaker;

// notetaker-eval --model gemma3:4b [--visits DIR] [--only ID] [--out DIR]
//
// Drafts every synthetic visit with one model and scores it. Slow and
// model-dependent, so it is a tool to run deliberately, not part of `just check`.
// Visits run one at a time: Ollama serves one generation at a time on this
// hardware, and parallel requests would only queue and blur per-visit latency.

string Arg(string name, string? fallback = null)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1]
        : fallback ?? throw new ArgumentException($"{name} is required");
}

var model = Arg("--model");
var visitsDir = Arg("--visits", "notetaker/visits");
var outDir = Arg("--out", "notetaker/results");
var only = args.Contains("--only") ? Arg("--only") : null;
var ollama = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434/";

var visits = Visit.LoadAll(visitsDir).Where(v => only is null || v.Id == only).ToList();
if (visits.Count == 0) { Console.Error.WriteLine($"no visits in {visitsDir}"); return 1; }

// 4 minutes per visit: a cold model load plus a long transcript on a 16 GB
// laptop. A run that needs longer than that is a finding, not something to wait out.
using var http = new HttpClient { BaseAddress = new Uri(ollama), Timeout = TimeSpan.FromMinutes(4) };
var drafter = new OllamaNoteDrafter(http, model);

Console.WriteLine($"{drafter.Source}: {visits.Count} visits");
var rows = new List<object>();
var scores = new List<VisitScore>();
var failures = 0;
var elapsed = TimeSpan.Zero;

foreach (var visit in visits)
{
    var result = await drafter.DraftAsync(visit.Transcript);
    elapsed += result.Elapsed;

    if (result.Draft is null)
    {
        failures++;
        Console.WriteLine($"  {visit.Id,-10} FAILED {result.Elapsed.TotalSeconds,5:0}s  {result.Error}");
        rows.Add(new { visit = visit.Id, error = result.Error, seconds = result.Elapsed.TotalSeconds });
        continue;
    }

    var score = NoteScorer.Score(visit, result.Draft);
    scores.Add(score);
    Console.WriteLine($"  {visit.Id,-10} recall {score.Recall,5:P0}  tooth-err {score.ToothErrors}  " +
                      $"ungrounded {score.Ungrounded}  noise {score.NoiseHits}  extras {score.Extras}  " +
                      $"{result.Elapsed.TotalSeconds,4:0}s");
    rows.Add(new { visit = visit.Id, score, draft = result.Draft, seconds = result.Elapsed.TotalSeconds,
                   prompt_tokens = result.PromptTokens, output_tokens = result.OutputTokens });
}

var keyItems = scores.Sum(s => s.KeyItems);
var summary = new
{
    model,
    visits = visits.Count,
    parse_failures = failures,
    recall = keyItems == 0 ? 0 : (double)scores.Sum(s => s.Matched) / keyItems,
    tooth_errors = scores.Sum(s => s.ToothErrors),
    ungrounded = scores.Sum(s => s.Ungrounded),
    noise_hits = scores.Sum(s => s.NoiseHits),
    extras = scores.Sum(s => s.Extras),
    mean_seconds = elapsed.TotalSeconds / visits.Count,
};

Console.WriteLine();
Console.WriteLine($"  recall {summary.recall:P1}   tooth errors {summary.tooth_errors}   ungrounded {summary.ungrounded}   " +
                  $"noise {summary.noise_hits}   extras {summary.extras}   parse failures {failures}   " +
                  $"{summary.mean_seconds:0}s/visit");

Directory.CreateDirectory(outDir);
var path = Path.Combine(outDir, $"{model.Replace(':', '_')}-{DateTime.UtcNow:yyyyMMddTHHmmss}.json");
File.WriteAllText(path, JsonSerializer.Serialize(new { summary, rows }, ClinicalNoteDraft.Json));
Console.WriteLine($"  -> {path}");
return 0;
