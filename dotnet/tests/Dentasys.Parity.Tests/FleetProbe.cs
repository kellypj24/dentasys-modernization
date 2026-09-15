namespace Dentasys.Parity.Tests;

/// <summary>
/// What the harness runs against. Parity over one practice and one date proves
/// almost nothing -- the fleet is heterogeneous by construction, and the practices
/// that break are never the ones you would have picked.
/// </summary>
public static class FleetProbe
{
    /// <summary>All 24 practices in the roster.</summary>
    public static readonly string[] Practices =
    {
        "000417", "000418", "000512", "000513", "000604", "000605",
        "000701", "000702", "000803", "000804", "000905", "000906",
        "001102", "001103", "001505", "001506", "001010", "001011",
        "001204", "001205", "001403", "001404", "001301", "001302",
    };

    /// <summary>
    /// Every date the fixtures put appointments on, including both DST edges and
    /// the midnight-crossing row. Two dates deliberately have no appointments at
    /// all -- an empty schedule is a case the screen has to get right too.
    /// </summary>
    public static readonly DateOnly[] Dates =
    {
        new(2025, 11, 4),
        new(2025, 11, 6),
        new(2026, 3, 2),
        new(2026, 3, 4),
        new(2026, 3, 8),    // spring forward
        new(2026, 3, 10),
        new(2026, 3, 12),
        new(2026, 3, 13),
        new(2026, 6, 15),   // nothing booked
        new(2026, 11, 1),   // fall back
    };

    public static IEnumerable<(string Practice, DateOnly Date)> All() =>
        from p in Practices from d in Dates select (p, d);
}
