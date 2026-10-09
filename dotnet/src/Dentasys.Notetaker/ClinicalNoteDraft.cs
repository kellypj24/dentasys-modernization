using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dentasys.Notetaker;

/// <summary>
/// What a drafter produces: a proposed clinical note, never the note itself. It
/// becomes part of the chart only after a provider edits and signs it.
///
/// Every item carries <c>Evidence</c>, a quote from the transcript. That is what
/// lets the harness flag a claim the conversation never made without asking a
/// second model to judge the first.
///
/// Teeth use Universal numbering: "1"-"32" permanent, "A"-"T" primary.
/// Surfaces are letters from M O D B L I F, e.g. "MOD".
/// </summary>
public sealed record ClinicalNoteDraft
{
    public string? ChiefComplaint { get; init; }
    public IReadOnlyList<ToothItem> Findings { get; init; } = [];
    public IReadOnlyList<ToothItem> ProceduresPerformed { get; init; } = [];
    public IReadOnlyList<PerioReading> Perio { get; init; } = [];
    public IReadOnlyList<HistoryItem> MedicalHistory { get; init; } = [];
    public IReadOnlyList<ToothItem> Plan { get; init; } = [];

    /// <summary>Things the drafter was unsure of, for the provider to check before signing.</summary>
    public IReadOnlyList<string> ReviewFlags { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    /// <summary>
    /// JSON Schema for the draft. Passed to the model runtime as a decoding
    /// constraint, so the output parses or the call fails -- it is never "mostly JSON".
    /// </summary>
    public static JsonObject Schema()
    {
        static JsonObject Str(bool nullable = false) =>
            new() { ["type"] = nullable ? new JsonArray("string", "null") : "string" };

        static JsonObject Obj(params (string Name, JsonObject Type)[] props)
        {
            var p = new JsonObject();
            foreach (var (name, type) in props) p[name] = type;
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = p,
                ["required"] = new JsonArray(props.Select(x => (JsonNode)x.Name).ToArray()),
            };
        }

        static JsonObject Arr(JsonObject items) => new() { ["type"] = "array", ["items"] = items };

        JsonObject ToothItemSchema() => Obj(
            ("tooth", Str(nullable: true)), ("surfaces", Str(nullable: true)),
            ("description", Str()), ("cdt_code", Str(nullable: true)), ("evidence", Str()));

        return Obj(
            ("chief_complaint", Str(nullable: true)),
            ("findings", Arr(ToothItemSchema())),
            ("procedures_performed", Arr(ToothItemSchema())),
            ("perio", Arr(Obj(("tooth", Str()), ("site", Str(nullable: true)),
                              ("depth_mm", new JsonObject { ["type"] = "integer" }), ("evidence", Str())))),
            ("medical_history", Arr(Obj(("description", Str()), ("evidence", Str())))),
            ("plan", Arr(ToothItemSchema())),
            ("review_flags", Arr(Str())));
    }
}

public sealed record ToothItem
{
    public string? Tooth { get; init; }
    public string? Surfaces { get; init; }
    public string Description { get; init; } = "";
    public string? CdtCode { get; init; }
    public string Evidence { get; init; } = "";
}

public sealed record PerioReading
{
    public string Tooth { get; init; } = "";
    public string? Site { get; init; }
    public int DepthMm { get; init; }
    public string Evidence { get; init; } = "";
}

public sealed record HistoryItem
{
    public string Description { get; init; } = "";
    public string Evidence { get; init; } = "";
}
