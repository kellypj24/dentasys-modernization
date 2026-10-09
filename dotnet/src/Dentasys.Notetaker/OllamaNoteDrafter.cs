using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dentasys.Notetaker;

/// <summary>
/// Drafts with a local model served by Ollama: the lab's drafter, and the shape of
/// the on-prem offline fallback. Temperature 0 and a schema-constrained decode, so
/// a failed run is a failed parse, not a creative one.
/// </summary>
public sealed class OllamaNoteDrafter : INoteDrafter
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _contextTokens;

    // 4096: the longest synthetic visit is ~1,200 tokens of prompt plus output.
    // Ollama allocates KV cache for the whole window up front, so an oversized
    // window is memory spent on nothing.
    public OllamaNoteDrafter(HttpClient http, string model, int contextTokens = 4096)
    {
        _http = http;
        _model = model;
        _contextTokens = contextTokens;
    }

    public string Source => $"ollama:{_model}";

    public async Task<DraftResult> DraftAsync(IReadOnlyList<TranscriptLine> transcript, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            // Unload 30 s after the last request, so the model's memory is returned
            // when a run ends instead of staying resident for Ollama's default 5 min.
            ["keep_alive"] = "30s",
            ["format"] = ClinicalNoteDraft.Schema(),
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_ctx"] = _contextTokens },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = DraftPrompt.System },
                new JsonObject { ["role"] = "user", ["content"] = DraftPrompt.User(transcript) }),
        };

        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await _http.PostAsJsonAsync("api/chat", request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct)
                       ?? throw new InvalidDataException("empty response");

            var content = body["message"]?["content"]?.GetValue<string>() ?? "";
            var draft = JsonSerializer.Deserialize<ClinicalNoteDraft>(content, ClinicalNoteDraft.Json);

            return new DraftResult
            {
                Draft = draft,
                Error = draft is null ? "model returned null" : null,
                Elapsed = clock.Elapsed,
                PromptTokens = body["prompt_eval_count"]?.GetValue<int>(),
                OutputTokens = body["eval_count"]?.GetValue<int>(),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or TaskCanceledException)
        {
            return new DraftResult { Error = $"{ex.GetType().Name}: {ex.Message}", Elapsed = clock.Elapsed };
        }
    }
}
