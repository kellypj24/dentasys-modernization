using System.Text;

namespace Dentasys.Notetaker;

/// <summary>
/// A draft as plain text: what CLINICAL_NOTE.NOTE_TEXT holds, and what the review
/// screen shows. One renderer, so the provider signs exactly the text the chart gets.
/// </summary>
public static class NoteRenderer
{
    public static string Render(ClinicalNoteDraft d)
    {
        var sb = new StringBuilder();
        if (d.ChiefComplaint is { Length: > 0 } cc) sb.AppendLine($"Chief complaint: {cc}");
        Section("Medical history", d.MedicalHistory.Select(h => h.Description));
        Section("Findings", d.Findings.Select(Tooth));
        Section("Perio", d.Perio.Select(p => $"#{p.Tooth}{(p.Site is null ? "" : " " + p.Site)}: {p.DepthMm} mm"));
        Section("Procedures", d.ProceduresPerformed.Select(Tooth));
        Section("Plan", d.Plan.Select(Tooth));
        return sb.ToString().TrimEnd();

        void Section(string title, IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            sb.AppendLine($"{title}:");
            foreach (var i in list) sb.AppendLine($"  - {i}");
        }

        static string Tooth(ToothItem i) =>
            (i.Tooth is null ? "" : $"#{i.Tooth}{(string.IsNullOrEmpty(i.Surfaces) ? "" : " " + i.Surfaces)}: ")
            + i.Description + (i.CdtCode is null ? "" : $" ({i.CdtCode})");
    }
}
