using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dentasys.Notetaker;

/// <summary>How one draft did against one visit's answer key.</summary>
public sealed record VisitScore
{
    public required string VisitId { get; init; }
    public int KeyItems { get; init; }
    public int Matched { get; init; }

    /// <summary>Items naming the right thing on the wrong tooth or surfaces. Scored apart from misses: it is a different patient record.</summary>
    public int ToothErrors { get; init; }

    /// <summary>Items whose evidence is not in the transcript: the note claims something the visit never said.</summary>
    public int Ungrounded { get; init; }

    /// <summary>Tooth-bearing items that match nothing in the key. Not necessarily wrong -- the key may be incomplete -- but worth reading.</summary>
    public int Extras { get; init; }

    /// <summary>Small talk from the visit that leaked into the note.</summary>
    public int NoiseHits { get; init; }

    public IReadOnlyList<string> Problems { get; init; } = [];

    public double Recall => KeyItems == 0 ? 1.0 : (double)Matched / KeyItems;
}

/// <summary>
/// Scores a draft against an answer key without a second model. Teeth and
/// surfaces must match exactly; descriptions match on any keyword, because the
/// wording is the drafter's to choose and the tooth number is not.
/// </summary>
public static class NoteScorer
{
    public static VisitScore Score(Visit visit, ClinicalNoteDraft draft)
    {
        var problems = new List<string>();
        var key = visit.Expected;
        int keyItems = 0, matched = 0, toothErrors = 0, extras = 0;

        if (key.ChiefComplaint.Count > 0)
        {
            keyItems++;
            if (draft.ChiefComplaint is { } cc && AnyKeyword(cc, key.ChiefComplaint)) matched++;
            else problems.Add($"chief complaint missing ({string.Join("/", key.ChiefComplaint)})");
        }

        void ScoreTeeth(string category, IReadOnlyList<KeyItem> expected, IReadOnlyList<ToothItem> drafted)
        {
            var used = new HashSet<int>();
            foreach (var k in expected)
            {
                keyItems++;
                var exact = Index(drafted, (i, d) => !used.Contains(i)
                                                     && SameTooth(k, d) && AnyKeyword(d.Description, k.Keywords));
                if (exact >= 0)
                {
                    used.Add(exact);
                    matched++;
                    continue;
                }

                var nearMiss = Index(drafted, (i, d) => !used.Contains(i) && AnyKeyword(d.Description, k.Keywords));
                if (nearMiss >= 0)
                {
                    // Either the wrong tooth, or a tooth where the visit named none:
                    // a denture adjustment charted on "tooth L" is a primary molar
                    // in someone's record, which is worse than leaving it blank.
                    used.Add(nearMiss);
                    toothErrors++;
                    var d = drafted[nearMiss];
                    problems.Add(k.Tooth is null
                        ? $"{category}: '{k.Keywords[0]}' needs no tooth, drafted {Fmt(d.Tooth, d.Surfaces)}"
                        : $"{category}: '{k.Keywords[0]}' on {Fmt(k.Tooth, k.Surfaces)}, drafted {Fmt(d.Tooth, d.Surfaces)}");
                }
                else
                {
                    problems.Add($"{category}: missing '{k.Keywords[0]}' {Fmt(k.Tooth, k.Surfaces)}");
                }
            }

            for (var i = 0; i < drafted.Count; i++)
            {
                if (used.Contains(i)) continue;
                extras++;
                problems.Add($"{category}: extra '{drafted[i].Description}' {Fmt(drafted[i].Tooth, drafted[i].Surfaces)}");
            }
        }

        ScoreTeeth("findings", key.Findings, draft.Findings);
        ScoreTeeth("procedures", key.ProceduresPerformed, draft.ProceduresPerformed);
        ScoreTeeth("plan", key.Plan, draft.Plan);

        foreach (var p in key.Perio)
        {
            keyItems++;
            if (draft.Perio.Any(d => NormalizeTooth(d.Tooth) == NormalizeTooth(p.Tooth) && d.DepthMm == p.DepthMm)) matched++;
            else problems.Add($"perio: missing {p.DepthMm} mm on #{p.Tooth}");
        }

        foreach (var h in key.MedicalHistory)
        {
            keyItems++;
            if (draft.MedicalHistory.Any(d => AnyKeyword(d.Description, h.Keywords))) matched++;
            else problems.Add($"history: missing '{h.Keywords[0]}'");
        }

        if (key.ReviewFlagRequired)
        {
            keyItems++;
            if (draft.ReviewFlags.Count > 0) matched++;
            else problems.Add("review flag required, none raised");
        }

        var transcript = visit.Transcript.Select(l => Normalize(l.Text)).ToList();
        var evidence = draft.Findings.Concat(draft.ProceduresPerformed).Concat(draft.Plan).Select(i => i.Evidence)
            .Concat(draft.Perio.Select(p => p.Evidence))
            .Concat(draft.MedicalHistory.Select(h => h.Evidence));
        var ungrounded = 0;
        foreach (var e in evidence)
        {
            if (IsGrounded(e, transcript)) continue;
            ungrounded++;
            problems.Add($"ungrounded evidence: \"{e}\"");
        }

        var serialized = JsonSerializer.Serialize(draft, ClinicalNoteDraft.Json).ToLowerInvariant();
        var noise = key.Forbidden.Where(f => serialized.Contains(f.ToLowerInvariant())).ToList();
        problems.AddRange(noise.Select(n => $"noise leaked: '{n}'"));

        return new VisitScore
        {
            VisitId = visit.Id, KeyItems = keyItems, Matched = matched, ToothErrors = toothErrors,
            Ungrounded = ungrounded, Extras = extras, NoiseHits = noise.Count, Problems = problems,
        };
    }

    /// <summary>"#14", "tooth 14", "No. 14" -> "14"; "k" -> "K". Null stays null.</summary>
    public static string? NormalizeTooth(string? tooth)
    {
        if (string.IsNullOrWhiteSpace(tooth)) return null;
        var t = Regex.Replace(tooth.Trim().ToUpperInvariant(), @"^(TOOTH|NUMBER|NO\.?|#)\s*", "").Trim('#', ' ', '.');
        return int.TryParse(t, out var n) ? n.ToString() : t;
    }

    private static readonly (string Pattern, char Letter)[] SurfaceWords =
    {
        ("mesi", 'M'), ("occlus", 'O'), ("dist", 'D'), ("bucc", 'B'),
        ("lingu", 'L'), ("incis", 'I'), ("faci", 'F'), ("palat", 'L'),
    };

    /// <summary>
    /// Surfaces as a set of letters, from either letters ("MOD") or words
    /// ("mesial occlusal", "distolingual"). F and B are the same surface (facial on
    /// anteriors, buccal on posteriors), so F is read as B; palatal is lingual.
    /// </summary>
    public static string NormalizeSurfaces(string? surfaces)
    {
        var s = (surfaces ?? "").Trim();
        var words = SurfaceWords.Where(w => s.Contains(w.Pattern, StringComparison.OrdinalIgnoreCase)).ToList();
        IEnumerable<char> letters = words.Count > 0
            ? words.Select(w => w.Letter)
            : s.ToUpperInvariant().Where(c => "MODBLIF".Contains(c));
        return new string(letters.Select(c => c == 'F' ? 'B' : c).Distinct().OrderBy(c => "MODBLI".IndexOf(c)).ToArray());
    }

    /// <summary>
    /// When the key names a tooth, the draft must name the same tooth and surfaces.
    /// When it names none, the draft may name none or a span ("18-20" for a bridge,
    /// "lower arch" for a denture) -- but not one specific tooth, which would put a
    /// finding on a tooth the visit never mentioned.
    /// </summary>
    private static bool SameTooth(KeyItem k, ToothItem d) =>
        k.Tooth is null
            ? !IsSingleTooth(d.Tooth) || (k.AcceptTooth is not null && NormalizeTooth(k.AcceptTooth) == NormalizeTooth(d.Tooth))
            : NormalizeTooth(k.Tooth) == NormalizeTooth(d.Tooth)
              && (k.Surfaces is null || NormalizeSurfaces(k.Surfaces) == NormalizeSurfaces(d.Surfaces));

    /// <summary>True for one Universal tooth ("14", "K"); false for null, spans, arches and quadrants.</summary>
    public static bool IsSingleTooth(string? tooth) =>
        NormalizeTooth(tooth) is { } t && Regex.IsMatch(t, "^([1-9]|[12][0-9]|3[0-2]|[A-T])$");

    private static bool AnyKeyword(string text, IReadOnlyList<string> keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Grounded when the evidence appears verbatim (after normalizing case and
    /// punctuation), or when at least 80% of its words appear in one transcript
    /// line. The tolerance absorbs a model trimming a quote; it does not absorb a
    /// quote stitched from words the visit never put together.
    /// </summary>
    public static bool IsGrounded(string evidence, IReadOnlyList<string> normalizedLines)
    {
        var e = Normalize(evidence);
        if (e.Length == 0) return false;
        if (normalizedLines.Any(l => l.Contains(e))) return true;
        if (string.Join(" ", normalizedLines).Contains(e)) return true;

        var words = e.Split(' ');
        return normalizedLines.Any(l =>
        {
            var lineWords = l.Split(' ').ToHashSet();
            return words.Count(lineWords.Contains) >= Math.Ceiling(words.Length * 0.8);
        });
    }

    public static string Normalize(string s) =>
        Regex.Replace(Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9#\s]", " "), @"\s+", " ").Trim();

    private static int Index(IReadOnlyList<ToothItem> items, Func<int, ToothItem, bool> predicate)
    {
        for (var i = 0; i < items.Count; i++) if (predicate(i, items[i])) return i;
        return -1;
    }

    private static string Fmt(string? tooth, string? surfaces) =>
        tooth is null ? "(no tooth)" : $"#{NormalizeTooth(tooth)}{(string.IsNullOrEmpty(surfaces) ? "" : " " + NormalizeSurfaces(surfaces))}";
}
