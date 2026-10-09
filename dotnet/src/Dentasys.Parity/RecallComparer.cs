using Dentasys.Domain;

namespace Dentasys.Parity;

/// <summary>The modern side of one recall comparison: what was decided, and from what.</summary>
public sealed record RecallVerdict(RecallOutcome Outcome, RecallFacts Facts);

public sealed record RecallFinding
{
    public required string PracticeId { get; init; }
    public required DateOnly RunDate { get; init; }
    public required long RecallId { get; init; }
    public required bool LegacySent { get; init; }
    public RecallOutcome? ModernOutcome { get; init; }
    public required Disposition Disposition { get; init; }
    public string? Rule { get; init; }

    public override string ToString() =>
        $"{PracticeId} {RunDate:yyyy-MM-dd} recall {RecallId}: legacy={(LegacySent ? "sent" : "not sent")} " +
        $"modern={ModernOutcome?.ToString() ?? "(absent)"} -> {Disposition}" + (Rule is null ? "" : $" [{Rule}]");
}

/// <summary>
/// Compares, recall by recall, whether the legacy job and RecallPolicy would send
/// it on a given night. Agreement is "both send" or "both don't"; the modern
/// side's reason for not sending is what the rule book weighs when they differ.
/// </summary>
public sealed class RecallComparer
{
    private readonly RuleBook _rules;

    public RecallComparer(RuleBook rules) => _rules = rules;

    public List<RecallFinding> Findings { get; } = new();
    public int RunsCompared { get; private set; }
    public int RecallsCompared { get; private set; }
    public int SentByBoth { get; private set; }

    public IEnumerable<RecallFinding> Regressions =>
        Findings.Where(f => f.Disposition is Disposition.Regression
                                          or Disposition.MissingInTarget
                                          or Disposition.ExtraInTarget);

    public void Compare(string practiceId, DateOnly runDate,
                        IReadOnlySet<long> legacySent, IReadOnlyList<RecallFacts> modern)
    {
        RunsCompared++;
        var known = modern.Select(r => r.RecallId).ToHashSet();

        foreach (var facts in modern)
        {
            RecallsCompared++;
            var outcome = RecallPolicy.Decide(facts, runDate);
            var l = legacySent.Contains(facts.RecallId);
            var m = outcome == RecallOutcome.Send;

            if (l == m)
            {
                if (l) SentByBoth++;
                continue;
            }

            var disposition = _rules.Classify("Sent", l, new RecallVerdict(outcome, facts), out var rule);
            Findings.Add(new RecallFinding
            {
                PracticeId = practiceId, RunDate = runDate, RecallId = facts.RecallId,
                LegacySent = l, ModernOutcome = outcome, Disposition = disposition, Rule = rule?.Name,
            });
        }

        foreach (var id in legacySent.Where(id => !known.Contains(id)))
        {
            Findings.Add(new RecallFinding
            {
                PracticeId = practiceId, RunDate = runDate, RecallId = id,
                LegacySent = true, Disposition = Disposition.MissingInTarget,
            });
        }
    }

    public string Summary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"  recall parity: {RunsCompared} runs, {RecallsCompared} recalls, {SentByBoth} sent by both, {Findings.Count} differences");
        foreach (var g in Findings.GroupBy(f => (f.Disposition, f.Rule)).OrderBy(g => g.Key.Disposition))
            sb.AppendLine($"    {g.Key.Disposition,-20} {g.Count(),5}" + (g.Key.Rule is null ? "" : $"  [{g.Key.Rule}]"));

        var regressions = Regressions.ToList();
        sb.AppendLine(regressions.Count == 0 ? "  no unexplained differences" : "  UNEXPLAINED:");
        foreach (var r in regressions.Take(20)) sb.AppendLine($"    {r}");
        return sb.ToString();
    }
}
