using Dentasys.Notetaker;

namespace Dentasys.Notes.Api;

public static class Endpoints
{
    public static void MapNotesEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- capture agent -----------------------------------------------------
        // Every call here is safe to repeat: the agent retries after any disconnect.

        app.MapPost("/captures", async (RegisterCaptureRequest r, NotesService notes, CancellationToken ct) =>
        {
            if (!r.ConsentRecorded)
                return Results.Problem("no consent recorded for this visit; nothing may be stored", statusCode: 422);
            await notes.RegisterCaptureAsync(new CaptureRegistration(
                r.CaptureId, r.PracticeId, r.PatientId, r.ApptId, r.ProviderCode, r.ConsentRecorded), ct);
            return Results.NoContent();
        });

        app.MapPut("/captures/{captureId:guid}/chunks/{chunkNo:int}",
            async (Guid captureId, int chunkNo, HttpRequest request, NotesService notes, CancellationToken ct) =>
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body, ct);
            return await notes.PutChunkAsync(captureId, chunkNo, body.ToArray(), ct) switch
            {
                ChunkResult.Stored => Results.StatusCode(201),
                ChunkResult.Duplicate => Results.Ok(),
                _ => Results.NotFound(),
            };
        });

        app.MapPost("/captures/{captureId:guid}/complete",
            async (Guid captureId, CompleteUploadRequest r, NotesService notes, CancellationToken ct) =>
        {
            var outcome = await notes.CompleteUploadAsync(captureId, r.ChunkCount, ct);
            return Results.Ok(new CompleteUploadResponse(outcome.Complete, outcome.MissingChunks));
        });

        // ---- review screen ----------------------------------------------------

        app.MapGet("/practices/{practiceId}/notes", async (string practiceId, string? state, NotesService notes,
                                                           CancellationToken ct) =>
        {
            var states = (state ?? "drafted,in_review").Split(',', StringSplitOptions.RemoveEmptyEntries);
            return Results.Ok(await notes.ListAsync(practiceId, states, ct));
        });

        app.MapGet("/notes/{noteId:guid}", async (Guid noteId, NotesService notes, CancellationToken ct) =>
            await notes.GetAsync(noteId, ct) is { } n ? Results.Ok(ToResponse(n)) : Results.NotFound());

        app.MapPut("/notes/{noteId:guid}/draft", async (Guid noteId, SaveDraftRequest r, NotesService notes,
                                                        CancellationToken ct) =>
            ToResult(await notes.SaveEditAsync(noteId, r.BaseVersion, r.Draft, r.Actor, ct)));

        app.MapPost("/notes/{noteId:guid}/sign", async (Guid noteId, SignRequest r, NotesService notes,
                                                        CancellationToken ct) =>
            ToResult(await notes.SignAsync(noteId, r.Version, r.Actor, r.AcknowledgedLocalDraft, ct)));

        app.MapPost("/notes/{noteId:guid}/addenda", async (Guid noteId, AddendumRequest r, NotesService notes,
                                                           CancellationToken ct) =>
            await notes.AddAddendumAsync(noteId, r.Text, r.Actor, ct) is { } id
                ? Results.Created($"/notes/{noteId}/addenda/{id}", new { addendumId = id })
                : Results.Problem("only a signed note can be amended", statusCode: 422));
    }

    private static NoteResponse ToResponse(NoteView n) => new(
        n.NoteId, n.PracticeId, n.State, n.CurrentVersion, n.CurrentSource,
        n.CurrentSource?.StartsWith("local:") == true,
        n.Current, n.Current is null ? null : NoteRenderer.Render(n.Current), n.UnappliedVersions);

    /// <summary>409 means "you were looking at an older version"; 422 means "not in a state that allows this".</summary>
    private static IResult ToResult(EditOutcome o) => o.Result switch
    {
        EditResult.Saved => Results.Ok(new VersionResponse(o.Version, o.State)),
        EditResult.Conflict => Results.Conflict(new VersionResponse(o.Version, o.State, "the note changed since you opened it")),
        EditResult.NotEditable => Results.UnprocessableEntity(new VersionResponse(o.Version, o.State, $"note is {o.State}")),
        EditResult.LocalDraftNotAcknowledged => Results.UnprocessableEntity(new VersionResponse(o.Version, o.State,
            "this draft came from the local fallback model; signing requires acknowledging that")),
        _ => Results.NotFound(),
    };
}
