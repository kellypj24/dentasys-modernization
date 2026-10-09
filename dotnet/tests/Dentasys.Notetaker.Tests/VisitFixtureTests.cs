using System.Text.RegularExpressions;
using Dentasys.Notetaker;
using Xunit;

namespace Dentasys.Notetaker.Tests;

/// <summary>
/// The visits are the measuring instrument. A malformed answer key scores every
/// model wrong in the same way and nobody notices, so the keys are tested too.
/// </summary>
public sealed class VisitFixtureTests
{
    internal static IReadOnlyList<Visit> Visits => Visit.LoadAll(VisitsDirectory());

    internal static string VisitsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "justfile"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? ".", "notetaker", "visits");
    }

    [Fact]
    public void There_are_enough_visits_for_the_scores_to_mean_something() =>
        Assert.True(Visits.Count >= 30, $"{Visits.Count} visits; fewer than 30 and one visit swings recall by several points");

    [Fact]
    public void Visit_ids_are_unique() =>
        Assert.Equal(Visits.Count, Visits.Select(v => v.Id).Distinct().Count());

    [Fact]
    public void Every_visit_expects_something() =>
        Assert.All(Visits, v => Assert.True(
            v.Expected.ChiefComplaint.Count + v.Expected.Findings.Count + v.Expected.ProceduresPerformed.Count +
            v.Expected.Perio.Count + v.Expected.MedicalHistory.Count + v.Expected.Plan.Count > 0 ||
            v.Expected.ReviewFlagRequired, $"{v.Id} has an empty answer key"));

    [Fact]
    public void Forbidden_terms_actually_occur_in_the_transcript()
    {
        // A forbidden term the visit never says can never leak, so it tests nothing.
        foreach (var v in Visits)
        {
            var text = string.Join(" ", v.Transcript.Select(l => l.Text)).ToLowerInvariant();
            Assert.All(v.Expected.Forbidden, f => Assert.Contains(f.ToLowerInvariant(), text));
        }
    }

    [Fact]
    public void Answer_key_teeth_are_universal_designations()
    {
        var teeth = Visits.SelectMany(v =>
            v.Expected.Findings.Concat(v.Expected.ProceduresPerformed).Concat(v.Expected.Plan)
             .SelectMany(k => new[] { k.Tooth, k.AcceptTooth })
             .Concat(v.Expected.Perio.Select(p => p.Tooth)).Where(t => t is not null).Select(t => (v.Id, Tooth: t!)));

        Assert.All(teeth, t => Assert.True(
            Regex.IsMatch(t.Tooth, "^([1-9]|[12][0-9]|3[0-2]|[A-T])$"), $"{t.Id}: '{t.Tooth}' is not a Universal tooth"));
    }

    [Fact]
    public void Every_key_item_has_keywords() =>
        Assert.All(Visits.SelectMany(v => v.Expected.Findings.Concat(v.Expected.ProceduresPerformed)
                                           .Concat(v.Expected.Plan).Concat(v.Expected.MedicalHistory)),
                   k => Assert.NotEmpty(k.Keywords));
}
