using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Dentasys.Notetaker;
using Dentasys.Notetaker.Claude;

// notetaker-eval --drafter claude [--model claude-opus-5-5] [--effort medium]
// notetaker-eval --drafter ollama --model llama3.1:8b
//                [--ctx 4096] [--pause SECONDS]
//                [--visits DIR] [--only ID] [--out DIR]
//
// Drafts every synthetic visit with one drafter and scores it. Slow, and for
// claude it costs money, so it is a tool to run deliberately, not part of
// `just check`. Visits run one at a time so per-visit latency is real.

string Arg(string name, string? fallback = null)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1]
        : fallback ?? throw new ArgumentException($"{name} is required");
}

var kind = Arg("--drafter", "ollama");
var model = Arg("--model", kind == "claude" ? "claude-opus-5-5" : null);
var visitsDir = Arg("--visits", "notetaker/visits");
var outDir = Arg("--out", "notetaker/results");
var only = args.Contains("--only") ? Arg("--only") : null;
// Seconds to idle between visits. Caps the duty cycle of a local model so a
// laptop is not at full GPU load for the whole run.
var pause = TimeSpan.FromSeconds(double.Parse(Arg("--pause", "0")));
var ollama = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434/";

var visits = Visit.LoadAll(visitsDir).Where(v => only is null || v.Id == only).ToList();
if (visits.Count == 0) { Console.Error.WriteLine($"no visits in {visitsDir}"); return 1; }

// 4 minutes per visit: a cold model load plus a long transcript. A run that
// needs longer than that is a finding, not something to wait out.
using var http = new HttpClient { BaseAddress = new Uri(ollama), Timeout = TimeSpan.FromMinutes(4) };
INoteDrafter drafter = kind switch
{
    "ollama" => new OllamaNoteDrafter(http, model, int.Parse(Arg("--ctx", "4096"))),
    // Synthetic visits only. The client reads ANTHROPIC_API_KEY.
    "claude" => new ClaudeNoteDrafter(new AnthropicClient(), model,
                    Enum.Parse<Effort>(Arg("--effort", "medium"), ignoreCase: true)),
    _ => throw new ArgumentException($"--drafter must be ollama or claude, not '{kind}'"),
};

Console.WriteLine($"{drafter.Source}: {visits.Count} visits");
var rows = new List<object>();
var scores = new List<VisitScore>();
var failures = 0;
var elapsed = TimeSpan.Zero;
long inputTokens = 0, outputTokens = 0;

foreach (var visit in visits)
{
    if (pause > TimeSpan.Zero && visit != visits[0]) await Task.Delay(pause);
    var result = await drafter.DraftAsync(visit.Transcript);
    elapsed += result.Elapsed;
    inputTokens += result.PromptTokens ?? 0;
    outputTokens += result.OutputTokens ?? 0;

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
    input_tokens = inputTokens,
    output_tokens = outputTokens,
};

Console.WriteLine();
Console.WriteLine($"  recall {summary.recall:P1}   tooth errors {summary.tooth_errors}   ungrounded {summary.ungrounded}   " +
                  $"noise {summary.noise_hits}   extras {summary.extras}   parse failures {failures}   " +
                  $"{summary.mean_seconds:0}s/visit   tokens in {summary.input_tokens:N0} / out {summary.output_tokens:N0}");

Directory.CreateDirectory(outDir);
var path = Path.Combine(outDir, $"{kind}-{model.Replace(':', '_')}-{DateTime.UtcNow:yyyyMMddTHHmmss}.json");
File.WriteAllText(path, JsonSerializer.Serialize(new { summary, rows }, ClinicalNoteDraft.Json));
Console.WriteLine($"  -> {path}");
return 0;
