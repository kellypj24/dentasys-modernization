namespace Dentasys.Domain;

/// <summary>What the recall run decided for one recall, and why.</summary>
public enum RecallOutcome
{
    Send,
    NotDue,
    SentRecently,
    RecallCancelled,
    PatientInactive,
}

/// <summary>Everything the policy needs to know about one recall.</summary>
public sealed record RecallFacts(
    long RecallId,
    long? PatientId,
    string? RecallType,
    DateOnly DueMonth,
    DateOnly? LastSentOn,
    bool IsDeleted,
    bool PatientActive,
    bool DueYearFromEvidence);

/// <summary>
/// Which recalls the nightly run sends. This is usp_NightlyRecallAndClaims's
/// WHERE clause, moved to where it can be unit tested, with two deliberate
/// differences the parity harness names rather than hides:
///
///   - DueMonth is the migrated value, whose century the patient's date of birth
///     can overrule. The proc's 1999 pivot reads a 2051 pediatric recall as 1951
///     and mails it every 30 days (LANDMINE #5).
///   - A cancelled recall is not sent. The proc never learned to read
///     RECALL.DEL_FLG, which arrived in 07.02.11, years after it was written.
///
/// Everything else matches the proc: due once the run month reaches the due
/// month, re-sent every 30 days while overdue, never to a deleted patient.
/// </summary>
public static class RecallPolicy
{
    public const int ResendAfterDays = 30;

    public static RecallOutcome Decide(RecallFacts r, DateOnly runDate)
    {
        if (!r.PatientActive) return RecallOutcome.PatientInactive;
        if (r.IsDeleted) return RecallOutcome.RecallCancelled;
        if (r.DueMonth > new DateOnly(runDate.Year, runDate.Month, 1)) return RecallOutcome.NotDue;
        if (r.LastSentOn is { } sent && sent >= runDate.AddDays(-ResendAfterDays)) return RecallOutcome.SentRecently;
        return RecallOutcome.Send;
    }
}
