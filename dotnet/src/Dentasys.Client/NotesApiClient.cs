using System.Net;
using System.Net.Http.Json;
using Dentasys.Notetaker;

namespace Dentasys.Client;

public sealed record NoteQueueItem(Guid NoteId, int PatientId, int? ApptId, string ProviderCode, string State,
                                   string? CurrentSource, DateTimeOffset UpdatedAt);

public sealed record ReviewNote(Guid NoteId, string PracticeId, string State, int? Version, string? Source,
                                bool IsLocalDraft, ClinicalNoteDraft? Draft, string? Text, int UnappliedDrafts);

public enum SignStatus { Signed, ChangedSinceOpened, NeedsLocalDraftAcknowledgement, NotSignable, NotYourNote, NotFound }

/// <summary>
/// The review screen's view of the notes service. HTTP only: like the schedule,
/// the workstation never learns where the notes are stored. The HttpClient
/// carries the signed-in clinician's token, which is the only identity the
/// service believes.
/// </summary>
public sealed class NotesApiClient(HttpClient http)
{
    public async Task<IReadOnlyList<NoteQueueItem>> QueueAsync(string practiceId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<NoteQueueItem>>($"practices/{Uri.EscapeDataString(practiceId)}/notes", ct) ?? [];

    public async Task<ReviewNote?> GetAsync(Guid noteId, CancellationToken ct = default)
    {
        var r = await http.GetAsync($"notes/{noteId}", ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        // Web defaults (camelCase), matching the API. ClinicalNoteDraft.Json is the
        // snake_case shape for model output and storage, not the wire.
        return await r.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<ReviewNote>(ct);
    }

    /// <summary>
    /// Signs the version the provider was shown. <paramref name="acknowledgedLocalDraft"/>
    /// must be the provider's own answer to "this draft came from the local model"
    /// -- never defaulted to true by the screen.
    /// </summary>
    public async Task<SignStatus> SignAsync(Guid noteId, int version, bool acknowledgedLocalDraft,
                                            CancellationToken ct = default)
    {
        var r = await http.PostAsJsonAsync($"notes/{noteId}/sign", new { version, acknowledgedLocalDraft }, ct);
        if (r.IsSuccessStatusCode) return SignStatus.Signed;

        var body = await r.Content.ReadAsStringAsync(ct);
        return r.StatusCode switch
        {
            HttpStatusCode.Conflict => SignStatus.ChangedSinceOpened,
            HttpStatusCode.UnprocessableEntity when body.Contains("local fallback") => SignStatus.NeedsLocalDraftAcknowledgement,
            HttpStatusCode.UnprocessableEntity => SignStatus.NotSignable,
            HttpStatusCode.Forbidden => SignStatus.NotYourNote,
            _ => SignStatus.NotFound,
        };
    }
}
