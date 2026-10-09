using Anthropic;
using Dentasys.Notetaker;
using Dentasys.Notetaker.Claude;

namespace Dentasys.Notes.Api;

/// <summary>
/// The cloud and local drafters, chosen by configuration:
///   Notes:CloudDrafter  claude[:model] | ollama:model | none
///   Notes:LocalDrafter  ollama:model   | none
/// "none" for the cloud means every draft goes to the local model or waits.
/// </summary>
public sealed record DrafterSet(INoteDrafter Cloud, INoteDrafter? Local)
{
    public static DrafterSet FromConfig(IConfiguration config)
    {
        var ollama = new HttpClient
        {
            BaseAddress = new Uri(config["Notes:OllamaUrl"] ?? "http://localhost:11434/"),
            Timeout = TimeSpan.FromMinutes(4),
        };

        INoteDrafter? Make(string? spec) => spec?.Split(':', 2) switch
        {
            null or ["none"] => null,
            ["claude"] => new ClaudeNoteDrafter(new AnthropicClient()),
            ["claude", var model] => new ClaudeNoteDrafter(new AnthropicClient(), model),
            ["ollama", var model] => new OllamaNoteDrafter(ollama, model),
            _ => throw new InvalidOperationException($"unknown drafter '{spec}'"),
        };

        return new DrafterSet(
            Make(config["Notes:CloudDrafter"]) ?? new UnavailableDrafter(),
            Make(config["Notes:LocalDrafter"]));
    }
}

/// <summary>No cloud configured: every call fails, so drafting falls back or waits.</summary>
public sealed class UnavailableDrafter : INoteDrafter
{
    public string Source => "cloud:none";
    public Task<DraftResult> DraftAsync(IReadOnlyList<TranscriptLine> transcript, CancellationToken ct = default) =>
        Task.FromResult(new DraftResult { Error = "no cloud drafter configured" });
}
