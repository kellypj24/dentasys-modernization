using System.Text;
using Dentasys.Domain;

namespace Dentasys.App;

/// <summary>Paints the appointment book. Presentation only -- no rules.</summary>
public static class ScheduleRenderer
{
    public static string Render(ScheduleResult result, string practiceId, DateOnly date)
    {
        var sb = new StringBuilder();
        var slots = result.Slots;

        sb.AppendLine();
        sb.AppendLine($"  Practice {practiceId}   {date:dddd, d MMMM yyyy}");
        sb.AppendLine($"  via {result.Source}   {slots.Count} appointment(s)   {result.Elapsed.TotalMilliseconds:F0} ms"
                      + (result.Truncated ? "   [TRUNCATED]" : ""));
        sb.AppendLine();

        if (slots.Count == 0)
        {
            sb.AppendLine("  (nothing booked)");
            return sb.ToString();
        }

        const string rule = "  ---------------------------------------------------------------------------------------";
        sb.AppendLine("  OP     TIME    END     PATIENT                 PROVIDER          PROCEDURE                ST");
        sb.AppendLine(rule);

        string? currentOperatory = null;
        foreach (var s in slots)
        {
            if (currentOperatory is not null && s.OperatoryCode != currentOperatory) sb.AppendLine();
            currentOperatory = s.OperatoryCode;

            // A null end time is shown as "--", never guessed at. It means the
            // practice's appointment grid has not been collected yet, so the
            // duration genuinely is not known (LANDMINE #6).
            var end = s.EndTime is null ? "--" : s.EndTime.Value.ToString("HH:mm") + (s.EndsNextDay ? "+" : "");

            // A null patient name is a real state, not a missing value: the legacy
            // expression collapses the whole name when either part is null
            // (LANDMINE #9), and rows have painted blank for years because of it.
            var name = s.PatientName ?? "(blank - null name part)";

            sb.AppendLine(string.Format(
                "  {0,-6} {1,-7} {2,-7} {3,-23} {4,-17} {5,-24} {6}",
                Trunc(s.OperatoryCode, 6),
                s.LocalTime.ToString("HH:mm"),
                Trunc(end, 7),
                Trunc(name, 23),
                Trunc(s.ProviderName, 17),
                Trunc(s.ProcedureDescription ?? s.ProcedureCode, 24),
                s.StatusCode ?? "-"));
        }

        sb.AppendLine(rule);

        if (slots.Any(s => s.DurationMinutes is null))
            sb.AppendLine("  note: end times unavailable -- appointment grid not configured for this practice");

        return sb.ToString();
    }

    private static string Trunc(string? value, int width)
    {
        var v = value?.Trim() ?? "";
        return v.Length <= width ? v : v[..(width - 1)] + "…";
    }
}
