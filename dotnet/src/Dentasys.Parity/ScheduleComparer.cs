using Dentasys.Domain;

namespace Dentasys.Parity;

public sealed record FieldFinding
{
    public required string PracticeId { get; init; }
    public required DateOnly Date { get; init; }
    public required long AppointmentId { get; init; }
    public required string Field { get; init; }
    public object? Legacy { get; init; }
    public object? Modern { get; init; }
    public required Disposition Disposition { get; init; }
    public string? Rule { get; init; }

    public override string ToString() =>
        $"{PracticeId} {Date:yyyy-MM-dd} appt {AppointmentId} {Field}: " +
        $"legacy={Fmt(Legacy)} modern={Fmt(Modern)} -> {Disposition}" +
        (Rule is null ? "" : $" [{Rule}]");

    private static string Fmt(object? v) => v switch
    {
        null => "(null)",
        string s => $"\"{s}\"",
        _ => v.ToString() ?? "(null)",
    };
}

public sealed class ParityReport
{
    private readonly List<FieldFinding> _findings = new();

    public IReadOnlyList<FieldFinding> Findings => _findings;
    public int ScreensCompared { get; private set; }
    public int RowsCompared { get; private set; }

    public void Add(FieldFinding f) => _findings.Add(f);
    public void CountScreen() => ScreensCompared++;
    public void CountRows(int n) => RowsCompared += n;

    public IEnumerable<FieldFinding> Regressions =>
        _findings.Where(f => f.Disposition is Disposition.Regression
                                          or Disposition.MissingInTarget
                                          or Disposition.ExtraInTarget);

    public IReadOnlyDictionary<Disposition, int> ByDisposition =>
        _findings.GroupBy(f => f.Disposition).ToDictionary(g => g.Key, g => g.Count());

    public string Summary(RuleBook rules)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"  parity: {ScreensCompared} screens, {RowsCompared} appointments, {_findings.Count} field differences");
        sb.AppendLine();

        foreach (var (disposition, count) in ByDisposition.OrderBy(k => k.Key))
            sb.AppendLine($"    {disposition,-20} {count,5}");

        var claimed = _findings.Where(f => f.Rule is not null)
                               .GroupBy(f => f.Rule!)
                               .OrderByDescending(g => g.Count());
        if (claimed.Any())
        {
            sb.AppendLine();
            sb.AppendLine("  claimed by rule:");
            foreach (var g in claimed)
            {
                var rule = rules.Rules.First(r => r.Name == g.Key);
                sb.AppendLine($"    {g.Key} ({g.Count()})  -> {rule.Disposition}");
            }
        }

        var regressions = Regressions.ToList();
        sb.AppendLine();
        if (regressions.Count == 0)
        {
            sb.AppendLine("  no unexplained differences");
        }
        else
        {
            sb.AppendLine($"  {regressions.Count} UNEXPLAINED difference(s):");
            foreach (var r in regressions.Take(25)) sb.AppendLine($"    {r}");
        }
        sb.AppendLine();
        return sb.ToString();
    }
}

/// <summary>
/// Compares two renderings of the same schedule, field by field, and asks the
/// rule book what each difference means.
///
/// Rows are matched on appointment id rather than position, so a row that moved is
/// reported as a field difference in ordering rather than as a cascade of hundreds
/// of bogus mismatches. Ordering is then checked separately, because the sequence
/// the book paints in is itself behavior the front desk relies on.
/// </summary>
public sealed class ScheduleComparer
{
    private readonly RuleBook _rules;

    public ScheduleComparer(RuleBook rules) => _rules = rules;

    /// <param name="legacyRawEndTimes">
    /// The end-time text the legacy side actually emitted, when the caller has it.
    /// Supplied for stored-procedure comparisons so that "0860" is compared AS
    /// "0860" rather than as the null it parses to -- otherwise it silently
    /// matches the target's honest null and the defect disappears from the report.
    /// </param>
    public void Compare(
        string practiceId, DateOnly date,
        IReadOnlyList<ScheduleSlot> legacy,
        IReadOnlyList<ScheduleSlot> modern,
        ParityReport report,
        IReadOnlyDictionary<long, string?>? legacyRawEndTimes = null)
    {
        report.CountScreen();
        report.CountRows(legacy.Count);

        var legacyById = legacy.ToDictionary(s => s.AppointmentId);
        var modernById = modern.ToDictionary(s => s.AppointmentId);

        foreach (var id in legacyById.Keys.Where(k => !modernById.ContainsKey(k)))
            report.Add(new FieldFinding
            {
                PracticeId = practiceId, Date = date, AppointmentId = id,
                Field = "(row)", Legacy = "present", Modern = null,
                Disposition = Disposition.MissingInTarget,
            });

        foreach (var id in modernById.Keys.Where(k => !legacyById.ContainsKey(k)))
            report.Add(new FieldFinding
            {
                PracticeId = practiceId, Date = date, AppointmentId = id,
                Field = "(row)", Legacy = null, Modern = "present",
                Disposition = Disposition.ExtraInTarget,
            });

        foreach (var id in legacyById.Keys.Intersect(modernById.Keys))
        {
            var l = legacyById[id];
            var m = modernById[id];

            Check(nameof(l.LocalDate), l.LocalDate, m.LocalDate);
            Check(nameof(l.LocalTime), l.LocalTime, m.LocalTime);
            Check(nameof(l.LengthUnits), l.LengthUnits, m.LengthUnits);
            Check(nameof(l.DurationMinutes), l.DurationMinutes, m.DurationMinutes);
            if (legacyRawEndTimes is not null)
            {
                // Compare the text the procedure emitted against the text the new
                // screen would render. "0860" vs "(none)" is a real difference and
                // has to be classified, not parsed away.
                legacyRawEndTimes.TryGetValue(id, out var rawEnd);
                Check(nameof(l.EndTime),
                      string.IsNullOrWhiteSpace(rawEnd) ? null : rawEnd,
                      m.EndTime?.ToString("HHmm"));
            }
            else
            {
                Check(nameof(l.EndTime), l.EndTime, m.EndTime);
            }
            Check(nameof(l.PatientId), l.PatientId, m.PatientId);
            Check(nameof(l.PatientName), l.PatientName, m.PatientName);
            Check(nameof(l.HomePhone), l.HomePhone, m.HomePhone);
            Check(nameof(l.Balance), l.Balance, m.Balance);
            Check(nameof(l.ProviderCode), l.ProviderCode, m.ProviderCode);
            Check(nameof(l.ProviderName), l.ProviderName, m.ProviderName);
            Check(nameof(l.OperatoryCode), l.OperatoryCode, m.OperatoryCode);
            Check(nameof(l.OperatoryName), l.OperatoryName, m.OperatoryName);
            Check(nameof(l.ProcedureCode), l.ProcedureCode, m.ProcedureCode);
            Check(nameof(l.ProcedureDescription), l.ProcedureDescription, m.ProcedureDescription);
            Check(nameof(l.StatusCode), l.StatusCode, m.StatusCode);
            Check(nameof(l.Note), l.Note, m.Note);

            void Check(string field, object? lv, object? mv)
            {
                var disposition = _rules.Classify(field, lv, mv, out var rule);
                if (disposition == Disposition.Agree) return;
                report.Add(new FieldFinding
                {
                    PracticeId = practiceId, Date = date, AppointmentId = id,
                    Field = field, Legacy = lv, Modern = mv,
                    Disposition = disposition, Rule = rule?.Name,
                });
            }
        }

        // The paint order the appointment book depends on.
        var sharedLegacy = legacy.Where(s => modernById.ContainsKey(s.AppointmentId))
                                 .Select(s => s.AppointmentId).ToList();
        var sharedModern = modern.Where(s => legacyById.ContainsKey(s.AppointmentId))
                                 .Select(s => s.AppointmentId).ToList();
        if (!sharedLegacy.SequenceEqual(sharedModern))
            report.Add(new FieldFinding
            {
                PracticeId = practiceId, Date = date, AppointmentId = 0,
                Field = "(row order)",
                Legacy = string.Join(",", sharedLegacy),
                Modern = string.Join(",", sharedModern),
                Disposition = Disposition.Regression,
            });
    }
}
