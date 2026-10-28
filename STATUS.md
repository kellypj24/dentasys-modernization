# Status

Current state only; history is in git and the PRs. Update this file in the same PR as the
work it describes.

_Updated 2026-10-28._

## Now

- Priority: an AI notetaker (ambient chairside capture) working on the legacy stack,
  ahead of the cloud migration. Design and pacing: `docs/NOTETAKER.md`.
- Lab phases 1–4 merged (#3–#6): notes store and job runner with offline fallback, HTTP
  API, encrypted capture spool, review and sign client, token identity, and a
  transcription contract with low-confidence review flags.
- Recall run moved to the worker with fleet parity (#7); eval scoring refined (#8).
  Local baselines: `llama3.1:8b` 72% recall, `gemma3:4b` 60%.
- The lab is an onboarding framework, not production: clarity over hardening.

## Next

1. A cloud speech engine behind `ITranscriber`.
2. Speaker-role attribution.
3. A keyed Claude eval run of the drafter.
4. Pick an identity provider for production sign-in.

## Open decisions

- Identity provider.
- Tenancy stays an assumption (database per client) until confirmed.

## Blocked on

- The keyed Claude eval needs PJ's API key.
