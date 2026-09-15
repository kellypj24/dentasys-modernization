namespace Dentasys.Parity;

/// <summary>
/// A claim, made in advance, that a specific field may differ in a specific way
/// for a specific reason.
///
/// <see cref="Claims"/> is what keeps this from being a whitelist. A rule that
/// merely named a field would excuse ANY difference in it -- "money may differ"
/// would wave through a balance that is out by four hundred pounds. The predicate
/// bounds the claim, so a difference inside the bound is expected and a difference
/// outside it is a regression even though a rule for that field exists.
/// </summary>
public sealed record DivergenceRule
{
    public required string Name { get; init; }
    public required string Field { get; init; }
    public required Disposition Disposition { get; init; }

    /// <summary>Why this difference is acceptable. Shown in the report.</summary>
    public required string Rationale { get; init; }

    /// <summary>True when this rule explains this particular pair of values.</summary>
    public required Func<object?, object?, bool> Claims { get; init; }
}
