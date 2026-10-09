using Dentasys.Client;

namespace Dentasys.App;

/// <summary>
/// dentasys notes queue &lt;practiceId&gt;
/// dentasys notes show &lt;noteId&gt;
/// dentasys notes sign &lt;noteId&gt; --version N [--accept-local-draft]
///
/// The review screen, as console commands. What matters is what it puts in front
/// of the provider: the exact text the chart will get, which model wrote it, and
/// a refusal to sign a local-fallback draft unless the provider says they read it
/// as one.
/// </summary>
public static class NotesCommand
{
    public static async Task<int> RunAsync(List<string> argv, NotesApiClient notes)
    {
        string? Opt(string name)
        {
            var i = argv.IndexOf(name);
            return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null;
        }

        switch (argv.ElementAtOrDefault(0))
        {
            case "queue" when argv.Count >= 2:
                var queue = await notes.QueueAsync(argv[1]);
                if (queue.Count == 0) Console.WriteLine("  no notes waiting for review");
                foreach (var q in queue)
                    Console.WriteLine($"  {q.NoteId}  patient {q.PatientId,-8} {q.ProviderCode}  {q.State,-10} " +
                                      $"{q.CurrentSource}{(q.CurrentSource?.StartsWith("local:") == true ? "  [LOCAL DRAFT]" : "")}");
                return 0;

            case "show" when argv.Count >= 2 && Guid.TryParse(argv[1], out var showId):
                var note = await notes.GetAsync(showId);
                if (note is null) { Console.Error.WriteLine("no such note"); return 1; }
                Console.WriteLine($"  note {note.NoteId}   {note.State}   version {note.Version}   drafted by {note.Source}");
                if (note.IsLocalDraft)
                {
                    Console.WriteLine("  ** Drafted by the LOCAL model while the cloud was unreachable. It is less **");
                    Console.WriteLine("  ** accurate. Check every tooth number before signing.                      **");
                }
                // Flags are for the provider, not the chart: shown here, never rendered into NOTE_TEXT.
                foreach (var flag in note.Draft?.ReviewFlags ?? [])
                    Console.WriteLine($"  CHECK: {flag}");
                if (note.UnappliedDrafts > 0)
                    Console.WriteLine($"  ({note.UnappliedDrafts} later draft(s) stored but not shown: you had already edited or signed)");
                Console.WriteLine();
                Console.WriteLine(note.Text);
                return 0;

            case "sign" when argv.Count >= 2 && Guid.TryParse(argv[1], out var signId)
                             && int.TryParse(Opt("--version"), out var version):
                var status = await notes.SignAsync(signId, version, argv.Contains("--accept-local-draft"));
                Console.WriteLine(status switch
                {
                    SignStatus.Signed => "  signed; on its way to the chart",
                    SignStatus.ChangedSinceOpened => "  not signed: the note changed since you opened it. Show it again.",
                    SignStatus.NeedsLocalDraftAcknowledgement =>
                        "  not signed: this is a LOCAL-model draft. Re-run with --accept-local-draft once you have checked it.",
                    SignStatus.NotSignable => "  not signed: the note is not in a signable state",
                    SignStatus.NotYourNote => "  not signed: only the provider this visit was recorded under can sign it",
                    _ => "  no such note",
                });
                return status == SignStatus.Signed ? 0 : 1;

            default:
                Console.Error.WriteLine("usage: dentasys notes queue <practiceId> | show <noteId> | sign <noteId> --version N [--accept-local-draft]");
                return 2;
        }
    }
}
