namespace Dentasys.Parity;

/// <summary>
/// What a difference MEANS. Producing the diff is the easy half; this enum is the
/// hard half, and getting it wrong in either direction is expensive.
///
/// Call a real defect "expected" and it ships. Call an intended improvement a
/// regression and you spend a week chasing your own fix, then start ignoring the
/// report -- which is worse, because a report nobody reads catches nothing.
/// </summary>
public enum Disposition
{
    /// <summary>Byte-for-byte the same.</summary>
    Agree,

    /// <summary>
    /// Different, and a named rule both predicted it and bounded it. The rule has
    /// to exist BEFORE the run; classifying after seeing the diff is just
    /// rationalising.
    /// </summary>
    ExpectedDivergence,

    /// <summary>
    /// The target cannot answer yet because the answer is not in the database and
    /// somebody has to go and find it. Not a defect -- a work item with a name.
    /// </summary>
    BlockedOnDiscovery,

    /// <summary>
    /// The target deliberately does not carry this, for compliance rather than
    /// technical reasons. Plaintext SSN is the case here.
    /// </summary>
    OmittedByPolicy,

    /// <summary>Different, and nothing claims it. The only disposition that fails a run.</summary>
    Regression,

    /// <summary>A row the legacy system returns and the new one does not.</summary>
    MissingInTarget,

    /// <summary>A row the new system returns and the legacy one does not.</summary>
    ExtraInTarget,
}
