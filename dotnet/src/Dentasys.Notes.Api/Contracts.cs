using Dentasys.Notetaker;

namespace Dentasys.Notes.Api;

// Wire contracts. Shared shapes with Dentasys.Client by convention, not by a
// project reference: the workstation must not depend on the server's assembly.

public sealed record RegisterCaptureRequest(
    Guid CaptureId, string PracticeId, int PatientId, int? ApptId, string ProviderCode, bool ConsentRecorded);

public sealed record CompleteUploadRequest(int ChunkCount);
public sealed record CompleteUploadResponse(bool Complete, IReadOnlyList<int> MissingChunks);

/// <summary>
/// Actor travels in the body in the lab. In production it comes from the
/// authenticated session -- a signature is only as good as the identity behind
/// it -- and that is on the security-review list in docs/NOTETAKER.md.
/// </summary>
public sealed record SaveDraftRequest(int BaseVersion, ClinicalNoteDraft Draft, string Actor);
public sealed record SignRequest(int Version, string Actor, bool AcknowledgedLocalDraft);
public sealed record AddendumRequest(string Text, string Actor);

public sealed record NoteResponse(
    Guid NoteId, string PracticeId, string State, int? Version, string? Source, bool IsLocalDraft,
    ClinicalNoteDraft? Draft, string? Text, int UnappliedDrafts);

public sealed record VersionResponse(int? Version, string? State, string? Reason = null);
