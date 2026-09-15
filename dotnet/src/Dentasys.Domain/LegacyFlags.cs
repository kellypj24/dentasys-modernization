namespace Dentasys.Domain;

/// <summary>
/// The soft-delete flag, which is 'Y', 'N', NULL or '' depending on which version
/// of the client last wrote the row (LANDMINE #10).
///
/// The legacy schedule proc tests <c>ISNULL(DEL_FLG,'N') &lt;&gt; 'Y'</c>, which
/// catches NULL but treats '' as not-deleted; the production report uses a
/// different predicate and catches both. The two have therefore disagreed since
/// 2011, and every practice has invented a folk explanation for it.
///
/// This is the one place that rule now lives. Both database adapters call it,
/// which means the behavior is identical on both engines by construction rather
/// than by two SQL predicates that have to be kept in sync by hand.
/// </summary>
public static class LegacyFlags
{
    /// <summary>Only an explicit 'Y' means deleted. Everything else is live.</summary>
    public static bool IsDeleted(string? delFlag) =>
        string.Equals(delFlag?.Trim(), "Y", StringComparison.OrdinalIgnoreCase);
}
