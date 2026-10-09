using System.Security.Claims;
using Dentasys.Notetaker;

namespace Dentasys.Notes.Api;

/// <summary>
/// Every route needs a token. Capture routes take a capture agent's token,
/// review routes a clinician's, and both are confined to the one practice the
/// token names: a workstation at one practice cannot upload into another's
/// notes, and a dentist cannot open another practice's queue.
/// </summary>
public static class Endpoints
{
    public static void MapNotesEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- capture agent -----------------------------------------------------
        // Every call here is safe to repeat: the agent retries after any disconnect.
        var agent = app.MapGroup("/captures").RequireAuthorization(Auth.CaptureAgentPolicy);

        agent.MapPost("", async (RegisterCaptureRequest r, ClaimsPrincipal user, NotesService notes, CancellationToken ct) =>
        {
            if (r.PracticeId != user.Practice()) return Results.Forbid();
            if (!r.ConsentRecorded)
                return Results.Problem("no consent recorded for this visit; nothing may be stored", statusCode: 422);
            try
            {
                await notes.RegisterCaptureAsync(new CaptureRegistration(
                    r.CaptureId, r.PracticeId, r.PatientId, r.ApptId, r.ProviderCode, r.ConsentRecorded, r.AudioFormat), ct);
            }
            catch (UnsupportedAudioFormatException ex)
            {
                return Results.Problem(ex.Message, statusCode: 415);
            }
            return Results.NoContent();
        });

        agent.MapPut("/{captureId:guid}/chunks/{chunkNo:int}",
            async (Guid captureId, int chunkNo, HttpRequest request, ClaimsPrincipal user, NotesService notes, CancellationToken ct) =>
        {
            if (await Denied(captureId, user, notes, ct) is { } denied) return denied;
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body, ct);
            return await notes.PutChunkAsync(captureId, chunkNo, body.ToArray(), ct) switch
            {
                ChunkResult.Stored => Results.StatusCode(201),
                ChunkResult.Duplicate => Results.Ok(),
                _ => Results.NotFound(),
            };
        });

        agent.MapPost("/{captureId:guid}/complete",
            async (Guid captureId, CompleteUploadRequest r, ClaimsPrincipal user, NotesService notes, CancellationToken ct) =>
        {
            if (await Denied(captureId, user, notes, ct) is { } denied) return denied;
            var outcome = await notes.CompleteUploadAsync(captureId, r.ChunkCount, ct);
            return Results.Ok(new CompleteUploadResponse(outcome.Complete, outcome.MissingChunks));
        });

        // ---- review screen ----------------------------------------------------
        var review = app.MapGroup("").RequireAuthorization(Auth.ClinicianPolicy);

        review.MapGet("/practices/{practiceId}/notes", async (string practiceId, string? state, ClaimsPrincipal user,
                                                              NotesService notes, CancellationToken ct) =>
        {
            if (practiceId != user.Practice()) return Results.Forbid();
            var states = (state ?? "drafted,in_review").Split(',', StringSplitOptions.RemoveEmptyEntries);
            return Results.Ok(await notes.ListAsync(practiceId, states, ct));
        });

        review.MapGet("/notes/{noteId:guid}", async (Guid noteId, ClaimsPrincipal user, NotesService notes, CancellationToken ct) =>
            await Denied(noteId, user, notes, ct) ?? (await notes.GetAsync(noteId, ct) is { } n ? Results.Ok(ToResponse(n)) : Results.NotFound()));

        review.MapPut("/notes/{noteId:guid}/draft", async (Guid noteId, SaveDraftRequest r, ClaimsPrincipal user,
                                                           NotesService notes, CancellationToken ct) =>
            await Denied(noteId, user, notes, ct)
            ?? ToResult(await notes.SaveEditAsync(noteId, r.BaseVersion, r.Draft, user.AsClinician(), ct)));

        review.MapPost("/notes/{noteId:guid}/sign", async (Guid noteId, SignRequest r, ClaimsPrincipal user,
                                                           NotesService notes, CancellationToken ct) =>
            await Denied(noteId, user, notes, ct)
            ?? ToResult(await notes.SignAsync(noteId, r.Version, user.AsClinician(), r.AcknowledgedLocalDraft, ct)));

        review.MapPost("/notes/{noteId:guid}/addenda", async (Guid noteId, AddendumRequest r, ClaimsPrincipal user,
                                                              NotesService notes, CancellationToken ct) =>
        {
            if (await Denied(noteId, user, notes, ct) is { } denied) return denied;
            var outcome = await notes.AddAddendumAsync(noteId, r.Text, user.AsClinician(), ct);
            return outcome.Result switch
            {
                EditResult.Saved => Results.Created($"/notes/{noteId}/addenda/{outcome.AddendumId}", new { addendumId = outcome.AddendumId }),
                EditResult.NotNoteProvider => Results.Problem("only the note's provider can amend it", statusCode: 403),
                EditResult.NotFound => Results.NotFound(),
                _ => Results.Problem("only a signed note can be amended", statusCode: 422),
            };
        });
    }

    /// <summary>404 for a note or capture that does not exist, 403 for one at another practice; null when allowed.</summary>
    private static async Task<IResult?> Denied(Guid id, ClaimsPrincipal user, NotesService notes, CancellationToken ct) =>
        await notes.OwnerAsync(id, ct) switch
        {
            null => Results.NotFound(),
            var (practice, _) when practice != user.Practice() => Results.Forbid(),
            _ => null,
        };

    private static NoteResponse ToResponse(NoteView n) => new(
        n.NoteId, n.PracticeId, n.State, n.CurrentVersion, n.CurrentSource,
        n.CurrentSource?.StartsWith("local:") == true,
        n.Current, n.Current is null ? null : NoteRenderer.Render(n.Current), n.UnappliedVersions);

    /// <summary>409: you were looking at an older version. 422: not in a state that allows this. 403: not yours to sign.</summary>
    private static IResult ToResult(EditOutcome o) => o.Result switch
    {
        EditResult.Saved => Results.Ok(new VersionResponse(o.Version, o.State)),
        EditResult.Conflict => Results.Conflict(new VersionResponse(o.Version, o.State, "the note changed since you opened it")),
        EditResult.NotEditable => Results.UnprocessableEntity(new VersionResponse(o.Version, o.State, $"note is {o.State}")),
        EditResult.LocalDraftNotAcknowledged => Results.UnprocessableEntity(new VersionResponse(o.Version, o.State,
            "this draft came from the local fallback model; signing requires acknowledging that")),
        EditResult.NotNoteProvider => Results.Json(new VersionResponse(o.Version, o.State,
            "only the provider this visit was recorded under can sign it"), statusCode: 403),
        _ => Results.NotFound(),
    };
}
