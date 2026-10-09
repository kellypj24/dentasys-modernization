namespace Dentasys.Parity;

/// <summary>One provider's line on the production/collection report.</summary>
public sealed record ProviderTotals(
    string ProviderCode,
    decimal Production,
    decimal Adjustments,
    decimal NetProduction,
    decimal Collections);

public sealed record ReportFinding
{
    public required string PracticeId { get; init; }
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    public required string ProviderCode { get; init; }
    public required string Field { get; init; }
    public object? Legacy { get; init; }
    public object? Modern { get; init; }
    public required Disposition Disposition { get; init; }
    public string? Rule { get; init; }

    public override string ToString() =>
        $"{PracticeId} {From:yyyy-MM-dd}..{To:yyyy-MM-dd} {ProviderCode} {Field}: " +
        $"legacy={Legacy ?? "(null)"} modern={Modern ?? "(null)"} -> {Disposition}" +
        (Rule is null ? "" : $" [{Rule}]");
}

/// <summary>
/// Compares the legacy report and the analytics report for one practice and one
/// date range, provider by provider. Provider codes are matched case-insensitively
/// because the legacy GROUP BY ran under a case-insensitive collation.
/// </summary>
public sealed class ProductionReportComparer
{
    private readonly RuleBook _rules;

    public ProductionReportComparer(RuleBook rules) => _rules = rules;

    public List<ReportFinding> Findings { get; } = new();
    public int ReportsCompared { get; private set; }
    public int ProviderLinesCompared { get; private set; }

    public IEnumerable<ReportFinding> Regressions =>
        Findings.Where(f => f.Disposition is Disposition.Regression
                                          or Disposition.MissingInTarget
                                          or Disposition.ExtraInTarget);

    public void Compare(string practiceId, DateOnly from, DateOnly to,
                        IReadOnlyList<ProviderTotals> legacy, IReadOnlyList<ProviderTotals> modern)
    {
        ReportsCompared++;
        var modernByCode = modern.ToDictionary(m => m.ProviderCode.ToUpperInvariant());
        var legacyCodes = legacy.Select(l => l.ProviderCode.ToUpperInvariant()).ToHashSet();

        ReportFinding Finding(string provider, string field, object? l, object? m, Disposition d, string? rule = null) =>
            new() { PracticeId = practiceId, From = from, To = to, ProviderCode = provider,
                    Field = field, Legacy = l, Modern = m, Disposition = d, Rule = rule };

        foreach (var l in legacy)
        {
            var code = l.ProviderCode.ToUpperInvariant();
            if (!modernByCode.TryGetValue(code, out var m))
            {
                Findings.Add(Finding(code, "(line)", "present", null, Disposition.MissingInTarget));
                continue;
            }

            ProviderLinesCompared++;
            Check(code, nameof(ProviderTotals.Production), l.Production, m.Production);
            Check(code, nameof(ProviderTotals.Adjustments), l.Adjustments, m.Adjustments);
            Check(code, nameof(ProviderTotals.NetProduction), l.NetProduction, m.NetProduction);
            Check(code, nameof(ProviderTotals.Collections), l.Collections, m.Collections);
        }

        foreach (var code in modernByCode.Keys.Where(c => !legacyCodes.Contains(c)))
            Findings.Add(Finding(code, "(line)", null, "present", Disposition.ExtraInTarget));

        void Check(string provider, string field, decimal l, decimal m)
        {
            var disposition = _rules.Classify(field, l, m, out var rule);
            if (disposition != Disposition.Agree)
                Findings.Add(Finding(provider, field, l, m, disposition, rule?.Name));
        }
    }

    public string Summary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"  report parity: {ReportsCompared} reports, {ProviderLinesCompared} provider lines, {Findings.Count} differences");
        foreach (var g in Findings.GroupBy(f => (f.Disposition, f.Rule)).OrderBy(g => g.Key.Disposition))
            sb.AppendLine($"    {g.Key.Disposition,-20} {g.Count(),5}" + (g.Key.Rule is null ? "" : $"  [{g.Key.Rule}]"));

        var regressions = Regressions.ToList();
        sb.AppendLine(regressions.Count == 0 ? "  no unexplained differences" : "  UNEXPLAINED:");
        foreach (var r in regressions.Take(20)) sb.AppendLine($"    {r}");
        return sb.ToString();
    }
}
