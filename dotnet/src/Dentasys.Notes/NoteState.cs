namespace Dentasys.Notes;

/// <summary>
/// The note lifecycle:
/// awaiting_audio → transcribing → drafting → drafted → in_review → signed → charted.
/// signed is terminal for content; a later correction is an addendum.
/// </summary>
public static class NoteState
{
    public const string AwaitingAudio = "awaiting_audio";
    public const string Transcribing = "transcribing";
    public const string Drafting = "drafting";
    public const string Drafted = "drafted";
    public const string InReview = "in_review";
    public const string Signed = "signed";
    public const string Charted = "charted";

    public static bool IsEditable(string state) => state is Drafted or InReview;
    public static bool IsSigned(string state) => state is Signed or Charted;
}
