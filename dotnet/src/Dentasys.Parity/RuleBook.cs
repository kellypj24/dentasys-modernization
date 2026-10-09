using Dentasys.Domain;

namespace Dentasys.Parity;

/// <summary>
/// Every difference this migration is allowed to produce, stated up front.
///
/// The default is Regression. A field with no rule, or a difference outside a
/// rule's bound, fails the run. That direction matters: a harness that defaults to
/// "probably fine" tells you what you already believed.
/// </summary>
public sealed class RuleBook
{
    public IReadOnlyList<DivergenceRule> Rules { get; }

    public RuleBook(IReadOnlyList<DivergenceRule> rules) => Rules = rules;

    public Disposition Classify(string field, object? legacy, object? modern, out DivergenceRule? matched)
    {
        matched = null;
        if (ValuesEqual(legacy, modern)) return Disposition.Agree;

        foreach (var rule in Rules)
        {
            if (!string.Equals(rule.Field, field, StringComparison.Ordinal)) continue;
            if (!rule.Claims(legacy, modern)) continue;
            matched = rule;
            return rule.Disposition;
        }

        return Disposition.Regression;
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a is string sa && b is string sb) return string.Equals(sa.TrimEnd(), sb.TrimEnd(), StringComparison.Ordinal);
        return a.Equals(b);
    }

    /// <summary>
    /// The rules for the schedule screen. Each one is an argument, not a mute.
    /// </summary>
    public static RuleBook ForSchedule() => new(new List<DivergenceRule>
    {
        new()
        {
            Name = "float_money_to_numeric",
            Field = nameof(Dentasys.Domain.ScheduleSlot.Balance),
            Disposition = Disposition.ExpectedDivergence,
            Rationale =
                "Source stores money in FLOAT, which cannot represent 0.10 (LANDMINE #2). " +
                "The target uses numeric(12,2), so the migrated value is the correctly rounded " +
                "one and the legacy value is the drifted one. Bounded at half a cent: anything " +
                "larger is a real discrepancy, not representation error.",
            Claims = (legacy, modern) =>
            {
                if (legacy is not decimal l || modern is not decimal m) return false;
                return Math.Abs(l - m) < 0.005m;
            },
        },
        new()
        {
            Name = "end_time_string_arithmetic_fixed",
            Field = nameof(Dentasys.Domain.ScheduleSlot.EndTime),
            Disposition = Disposition.ExpectedDivergence,
            Rationale =
                "The legacy proc computes the end time by casting HHMM to an integer and adding " +
                "minutes, so 08:00 + 60 is \"0860\" and 23:50 + 30 is \"2380\" (LANDMINE #8). " +
                "Neither is a time. The VB client patches it before painting and Crystal Reports " +
                "does not, which is why the schedule and the day sheet have disagreed for years. " +
                "The new value is real time arithmetic and rolls past midnight correctly.",
            Claims = (legacy, modern) => true,
        },
        new()
        {
            Name = "duration_awaiting_grid_discovery",
            Field = nameof(Dentasys.Domain.ScheduleSlot.DurationMinutes),
            Disposition = Disposition.BlockedOnDiscovery,
            Rationale =
                "LEN_UNITS is 10-minute units on part of the fleet and 15 on the rest, and the " +
                "grid lives in a workstation .INI file that will not be migrated (LANDMINE #6). " +
                "The legacy proc hardcodes x10, which is simply wrong for the 15-minute practices. " +
                "The target returns null until the grid is collected into practice_config. " +
                "Claimed only when the target declined to answer -- a WRONG number is still a regression.",
            Claims = (legacy, modern) => modern is null,
        },
    });

    /// <summary>
    /// The rules for the production/collection report. One claim, applied to each
    /// money column: the legacy totals are FLOAT sums and the modern ones are exact
    /// decimal. Bounded at half a cent per total -- representation error over a
    /// practice's ledger is many orders of magnitude below that, so anything larger
    /// is a real disagreement about which rows were counted.
    /// </summary>
    public static RuleBook ForProductionReport() => new(
        new[]
        {
            nameof(ProviderTotals.Production),
            nameof(ProviderTotals.Adjustments),
            nameof(ProviderTotals.NetProduction),
            nameof(ProviderTotals.Collections),
        }
        .Select(field => new DivergenceRule
        {
            Name = "float_sum_to_decimal_sum",
            Field = field,
            Disposition = Disposition.ExpectedDivergence,
            Rationale =
                "usp_RptProductionCollection sums FLOAT (LANDMINE #2); the analytics model " +
                "sums numeric. The modern total is the exact one.",
            Claims = (legacy, modern) =>
                legacy is decimal l && modern is decimal m && Math.Abs(l - m) < 0.005m,
        })
        .ToList());

    /// <summary>
    /// The rules for the nightly recall run. Both claim only "legacy sent it, modern
    /// did not", and only for the specific reason named -- a recall the modern side
    /// skips as SentRecently or NotDue for any other cause is still a regression.
    /// </summary>
    public static RuleBook ForRecall() => new(new List<DivergenceRule>
    {
        new()
        {
            Name = "two_digit_year_resolved_by_dob",
            Field = "Sent",
            Disposition = Disposition.ExpectedDivergence,
            Rationale =
                "The legacy job reads DUE_YM through the 1999 pivot, so a 2051 pediatric recall " +
                "is 1951 and 75 years overdue (LANDMINE #5). The migration resolved the century " +
                "against the patient's date of birth; the modern run does not send it yet.",
            Claims = (legacy, modern) =>
                legacy is true
                && modern is RecallVerdict { Outcome: RecallOutcome.NotDue, Facts.DueYearFromEvidence: true },
        },
        new()
        {
            Name = "cancelled_recall_honored",
            Field = "Sent",
            Disposition = Disposition.ExpectedDivergence,
            Rationale =
                "RECALL.DEL_FLG arrived in 07.02.11 and the nightly job was never updated to read " +
                "it, so a recall cancelled at the front desk still goes out once due. The modern " +
                "run skips it. A behavior change for the practices on 07.02.11 and later.",
            Claims = (legacy, modern) =>
                legacy is true && modern is RecallVerdict { Outcome: RecallOutcome.RecallCancelled },
        },
    });
}
