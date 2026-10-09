using Dentasys.Notetaker;

namespace Dentasys.Notes.Api;

// Wire contracts. Shared shapes with Dentasys.Client by convention, not by a
// project reference: the workstation must not depend on the server's assembly.

public sealed record RegisterCaptureRequest(
    Guid CaptureId, string PracticeId, int PatientId, int? ApptId, string ProviderCode, bool ConsentRecorded,
    string AudioFormat);

public sealed record CompleteUploadRequest(int ChunkCount);
public sealed record CompleteUploadResponse(bool Complete, IReadOnlyList<int> MissingChunks);

// No actor in any request: who did it comes from the caller's token. A signature
// is only as good as the identity behind it, and a body field is not one.
public sealed record SaveDraftRequest(int BaseVersion, ClinicalNoteDraft Draft);
public sealed record SignRequest(int Version, bool AcknowledgedLocalDraft);
public sealed record AddendumRequest(string Text);

public sealed record NoteResponse(
    Guid NoteId, string PracticeId, string State, int? Version, string? Source, bool IsLocalDraft,
    ClinicalNoteDraft? Draft, string? Text, int UnappliedDrafts);

public sealed record VersionResponse(int? Version, string? State, string? Reason = null);
