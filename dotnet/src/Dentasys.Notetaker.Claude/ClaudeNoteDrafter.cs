using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Dentasys.Notetaker.Claude;

/// <summary>
/// Drafts with Claude. In the lab it runs against the Anthropic API on synthetic
/// visits only. For PHI the same class takes a client pointed at a BAA-covered
/// endpoint (the SDK's Bedrock or Foundry client); nothing else changes.
///
/// Output is constrained by the draft's JSON Schema (structured outputs), so a
/// response either parses or is reported as a failure -- never "mostly JSON".
/// </summary>
public sealed class ClaudeNoteDrafter : INoteDrafter
{
    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly Effort _effort;

    public ClaudeNoteDrafter(AnthropicClient client, string model = "claude-opus-5-5", Effort effort = Effort.Medium)
    {
        _client = client;
        _model = model;
        _effort = effort;
    }

    public string Source => $"claude:{_model}";

    public async Task<DraftResult> DraftAsync(IReadOnlyList<TranscriptLine> transcript, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var response = await _client.Beta.Messages.Create(new Anthropic.Models.Beta.Messages.MessageCreateParams
            {
                Model = _model,
                MaxTokens = 16000,
                System = DraftPrompt.System,
                Messages = [new() { Role = Anthropic.Models.Beta.Messages.Role.User, Content = DraftPrompt.User(transcript) }],
                OutputConfig = new BetaOutputConfig
                {
                    Effort = _effort,
                    Format = new BetaJsonOutputFormat { Schema = SchemaElements() },
                },
                // A policy decline is re-served inside the same call by a model the
                // server picks for the refusal category.
                Fallbacks = new BetaFallbacksParam(new Default()),
                Betas = ["server-side-fallback-2026-07-01"],
            }, ct);

            if (response.StopReason == "refusal")
                return Failed($"refused ({response.StopDetails?.Category})");
            if (response.StopReason == "max_tokens")
                return Failed("hit max_tokens before the draft was complete");

            var json = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
            var draft = JsonSerializer.Deserialize<ClinicalNoteDraft>(json, ClinicalNoteDraft.Json);

            return new DraftResult
            {
                Draft = draft,
                Error = draft is null ? "model returned null" : null,
                Elapsed = clock.Elapsed,
                PromptTokens = (int)response.Usage.InputTokens,
                OutputTokens = (int)response.Usage.OutputTokens,
            };
        }
        // Bad key, no access, wrong model name: configuration, not a property of the
        // visit. They propagate so a run stops at the first visit instead of
        // recording thirty identical failures.
        catch (AnthropicUnauthorizedException) { throw; }
        catch (AnthropicForbiddenException) { throw; }
        catch (AnthropicNotFoundException) { throw; }
        catch (AnthropicRateLimitException ex) { return Failed($"rate limited: {ex.Message}"); }
        catch (AnthropicIOException ex) { return Failed($"connection: {ex.Message}"); }
        catch (AnthropicApiException ex) { return Failed($"API error: {ex.Message}"); }
        catch (JsonException ex) { return Failed($"unparseable draft: {ex.Message}"); }

        DraftResult Failed(string error) => new() { Error = error, Elapsed = clock.Elapsed };
    }

    private static Dictionary<string, JsonElement> SchemaElements() =>
        ClinicalNoteDraft.Schema().ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
}
