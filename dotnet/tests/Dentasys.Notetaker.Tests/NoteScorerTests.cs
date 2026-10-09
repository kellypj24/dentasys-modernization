using Dentasys.Notetaker;
using Xunit;

namespace Dentasys.Notetaker.Tests;

public sealed class NoteScorerTests
{
    private static readonly Visit Visit = new()
    {
        Id = "t1",
        Transcript =
        [
            new("DENTIST", "Decay on nineteen is mesial and occlusal."),
            new("DENTIST", "Placing a mesial occlusal composite on nineteen."),
            new("PATIENT", "We took the boat out to Lake Travis."),
        ],
        Expected = new AnswerKey
        {
            Findings = [new KeyItem { Tooth = "19", Surfaces = "MO", Keywords = ["decay"] }],
            ProceduresPerformed = [new KeyItem { Tooth = "19", Surfaces = "MO", Keywords = ["composite"] }],
            Forbidden = ["lake travis"],
        },
    };

    private static ClinicalNoteDraft Draft(string tooth = "19", string surfaces = "MO",
                                           string evidence = "Decay on nineteen is mesial and occlusal.") => new()
    {
        Findings = [new ToothItem { Tooth = tooth, Surfaces = surfaces, Description = "decay", Evidence = evidence }],
        ProceduresPerformed =
        [
            new ToothItem { Tooth = "19", Surfaces = "MO", Description = "composite",
                            Evidence = "Placing a mesial occlusal composite on nineteen." },
        ],
    };

    [Fact]
    public void A_correct_draft_scores_full_recall_and_no_errors()
    {
        var s = NoteScorer.Score(Visit, Draft());
        Assert.Equal(1.0, s.Recall);
        Assert.Equal(0, s.ToothErrors + s.Ungrounded + s.NoiseHits + s.Extras);
    }

    [Fact]
    public void The_right_finding_on_the_wrong_tooth_is_a_tooth_error_not_a_match()
    {
        var s = NoteScorer.Score(Visit, Draft(tooth: "18"));
        Assert.Equal(1, s.ToothErrors);
        Assert.Equal(0.5, s.Recall);
    }

    [Fact]
    public void Wrong_surfaces_are_a_tooth_error_too() =>
        Assert.Equal(1, NoteScorer.Score(Visit, Draft(surfaces: "DO")).ToothErrors);

    [Fact]
    public void Evidence_the_visit_never_said_is_ungrounded() =>
        Assert.Equal(1, NoteScorer.Score(Visit, Draft(evidence: "Patient reports sensitivity to cold.")).Ungrounded);

    [Fact]
    public void Small_talk_in_the_note_is_counted()
    {
        var leaky = Draft() with { ChiefComplaint = "Went to Lake Travis, here for decay" };
        Assert.Equal(1, NoteScorer.Score(Visit, leaky).NoiseHits);
    }

    [Fact]
    public void A_required_review_flag_is_a_key_item()
    {
        var visit = Visit with { Expected = Visit.Expected with { ReviewFlagRequired = true } };
        Assert.Equal(2.0 / 3, NoteScorer.Score(visit, Draft()).Recall, 3);
        Assert.Equal(1.0, NoteScorer.Score(visit, Draft() with { ReviewFlags = ["tooth inaudible"] }).Recall);
    }

    [Theory]
    [InlineData("MO", "MO")]
    [InlineData("om", "MO")]
    [InlineData("mesial occlusal", "MO")]
    [InlineData("mesiobuccal", "MB")]
    [InlineData("distolingual", "DL")]
    [InlineData("F", "B")]
    [InlineData("facial", "B")]
    [InlineData("MOD", "MOD")]
    [InlineData(null, "")]
    public void Surfaces_normalize_from_letters_or_words(string? input, string expected) =>
        Assert.Equal(expected, NoteScorer.NormalizeSurfaces(input));

    [Theory]
    [InlineData("#14", "14")]
    [InlineData("tooth 14", "14")]
    [InlineData("14", "14")]
    [InlineData("k", "K")]
    [InlineData("03", "3")]
    [InlineData(null, null)]
    public void Teeth_normalize(string? input, string? expected) =>
        Assert.Equal(expected, NoteScorer.NormalizeTooth(input));
}
