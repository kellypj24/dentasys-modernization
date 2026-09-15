namespace Dentasys.Domain;

/// <summary>
/// Patient display name (LANDMINE #9).
///
/// The legacy expression is <c>RTRIM(LAST_NM) + ', ' + RTRIM(FIRST_NM)</c>. Under
/// SQL Server's default settings a NULL on either side makes the WHOLE expression
/// NULL, so a patient with no first name paints as a blank block on the schedule.
/// Staff have worked around that for years and some of them use it deliberately.
///
/// The tempting cleanup is to emit "Kowalczyk, " instead. That is a behavior
/// change dressed as a tidy-up: rows that have been blank for two decades would
/// suddenly show, and the people who rely on blank-means-something would be the
/// last to be told. Preserving the null propagation is the correct port, and it
/// is preserved HERE, once, rather than in two dialects of SQL.
/// </summary>
public static class PatientNaming
{
    public static string? Display(string? lastName, string? firstName)
    {
        if (lastName is null || firstName is null) return null;
        return $"{lastName.TrimEnd()}, {firstName.TrimEnd()}";
    }
}
