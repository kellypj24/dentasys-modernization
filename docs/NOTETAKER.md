# Ambient clinical notetaker — v1 design

An ambient scribe for the operatory: it listens to the visit, drafts the clinical
note, and the provider reviews and signs it inside the existing client. v1 ships
on the legacy stack (practice databases in the data center, .NET client on
operatory workstations) within six months, and carries over unchanged when the
rest of the system moves to the cloud.

## Constraints

| Constraint | Consequence |
|---|---|
| Ships in ~6 months, on the legacy stack | No dependency on the Postgres migration. New components sit beside the legacy system and talk to it through one narrow write path. |
| Ambient capture, chairside | Microphone per operatory, consent per visit, and diarization (who said what) in the pipeline from the start. |
| PHI processed in a BAA-covered cloud, American models only | Speech and the LLM run in BAA-covered services (Bedrock or Foundry; the provider sits behind an interface). No Chinese-origin models anywhere, including the on-prem offline drafter. Coverage for the specific endpoint is confirmed before any PHI is sent. |
| Must work when disconnected from the cloud, then resync | Capture never waits on a network. Every hand-off is a durable queue with idempotent delivery. Drafting has a local fallback. |
| Nothing enters the chart unsigned | AI output is a draft. The signed note is the clinical record and is immutable; corrections are addenda. |

## Topology

```
 operatory workstation             data center (on-prem)                 cloud (BAA)
 ┌──────────────────────┐          ┌──────────────────────────────┐      ┌───────────────┐
 │ legacy .NET client   │──SQL────▶│ practice DBs (SQL Server)    │      │ speech-to-text│
 │  + review/sign panel │          │   └ CLINICAL_NOTE (signed)   │      │ (batch, diar.)│
 │                      │          │                              │      │               │
 │ capture agent        │──HTTPS──▶│ notes service                │─────▶│ LLM endpoint  │
 │  └ encrypted spool   │          │  ├ notes store (working copy)│      └───────────────┘
 └──────────────────────┘          │  ├ job queue + outbox        │
                                   │  └ local drafter (fallback)  │
                                   └──────────────────────────────┘
```

The notes service lives in the data center, next to the databases it writes to.
That placement is what makes cloud outages survivable: everything except the
cloud AI calls keeps working when the cloud link is down.

## What happens when a link is down

| Link down | Still works | Waits | Converges when restored |
|---|---|---|---|
| Workstation ↔ data center | Recording; audio goes to the local encrypted spool | Upload, drafting, review. The legacy client is down too, since its database is in the data center. | The capture agent resumes uploading; the server dedupes by `capture_id` and chunk number. |
| Data center ↔ cloud | Upload, notes store, review and signing of existing drafts, chart writes | Cloud transcription and drafting | Queued jobs drain. Unsigned local drafts can be re-drafted by the cloud. Signed notes are never touched. |
| Cloud slow or erroring | Same as above, after a timeout | — | Retries with backoff; `attempts` and `last_error` per job, as on the existing outbox. |
| Practice DB not yet on the schema version with `CLINICAL_NOTE` | Everything; the signed note is held in the notes store | The chart write only | The chart write retries after that practice's upgrade window. |

The last row matters as much as the network ones. The fleet is on five schema
versions, and upgrades happen in windows each customer schedules, so "the table
isn't there yet" is a normal state to resync from, not an error.

## Offline drafting

When the cloud is unreachable, the notes service can draft locally (speech-to-text
plus a US-origin open-weight LLM such as Llama or Gemma on a data-center GPU box),
or queue the visit and wait. A local
draft is labelled with its source, and the provider sees that before signing.

Whether local drafts are good enough to show is decided by the eval harness, not
by preference. The same synthetic visits run through both drafters, and the
scores decide whether offline mode drafts or only queues. If local quality is too
low, "queue and wait" is still a complete offline story: capture and signing of
earlier drafts carry on.

## State and sync

**Capture**, on the workstation: `recording → spooled → uploading → acknowledged`.
`capture_id` is a GUID minted on the workstation at record start, so it exists
before any server does. Uploads are chunked and resumable, keyed by
`(capture_id, chunk_no)`. Audio is encrypted at rest (DPAPI, machine scope) and
deleted from the workstation after the server acknowledges it.

**Note**, in the notes service:
`awaiting_audio → transcribing → drafted (local|cloud) → in_review → signed → charted`.

- Each draft is a new version; nothing is edited in place. Review uses optimistic
  concurrency on the draft version, so two workstations opening the same draft
  can't silently overwrite each other.
- `signed` is terminal for content. A later correction is an addendum with its
  own signature, which is how a clinical record is meant to change.
- `signed → charted` goes through an outbox: the signature and the pending chart
  write commit together, and a worker delivers to the practice DB. Delivery is at
  least once, so the chart write is an upsert keyed by `note_id`.
- A cloud draft that arrives after a local draft was signed is stored, not
  applied. The signed note stays as signed.

Every hand-off above is the outbox/SKIP LOCKED pattern this repo already uses for
domain events. The notetaker does not need a new consistency mechanism.

## Storage

| Store | Holds | Engine (v1) | Why |
|---|---|---|---|
| Workstation spool | Encrypted audio chunks until acknowledged | Files | Survives every disconnect; no database on the workstation. |
| Notes store | Captures, jobs, transcripts, draft versions, signatures, audit | One central multi-tenant database in the data center, keyed by `practice_id` | Available whenever the data center is. Designed migration-ready: no procs, types that map cleanly to Postgres. |
| `CLINICAL_NOTE` in each practice DB | Signed notes and addenda only | SQL Server, new table in the next schema version | The chart stays complete in the legal record, its backups and exports. |

## PHI handling

- Audio and transcripts are PHI everywhere: encrypted on the workstation, in
  transit, and at rest in the notes store.
- Logs carry ids, states and timings, never transcript text or audio.
- Audio retention after signing is a policy decision (open question). The design
  supports deleting audio once the note is signed.
- The lab uses synthetic visits only, which is why development can run on a local
  model with no BAA in scope.

## Quality: the eval harness

At least 30 synthetic visits, each with a script and an answer key. Per visit, the
harness scores:

- **fact recall:** findings, procedures, teeth and surfaces, perio readings, plan
  items from the key that appear in the draft
- **unsupported statements:** anything in the draft not grounded in the
  transcript. The most dangerous failure for a clinical note.
- **tooth and surface accuracy:** exact match, scored separately, because "#14 MO"
  versus "#15 MO" is a different patient record
- **noise rejection:** small talk in the script that must not reach the note

The same harness compares cloud and local drafters, and later scores
transcription by feeding synthetic speech through the speech step.

## Six-month plan

The lab builds each row as a phase, against the simulated system, in far less
time. The months are what a row costs against the real one: their client
codebase and release train, operatory audio, compliance review, a schema
release through each customer's upgrade window, and a pilot. The lab takes the
architecture and failure-handling risk out early so those months go to
integration.

| Month | Deliverable |
|---|---|
| 1 | Note contract, 30+ synthetic visits with answer keys, drafter interface, local drafter, eval harness. Lab only. |
| 2 | Notes service: store, job queue, state machine, outbox to `CLINICAL_NOTE`, disconnect tests with injected faults. |
| 3 | Capture agent and spool; review/sign panel in the client; cloud drafter behind the same interface, compared on the harness. |
| 4 | Schema version with `CLINICAL_NOTE`; BAA confirmation; consent flow; security review. |
| 5 | Pilot at a few practices; tune on pilot feedback (still no PHI in the lab). |
| 6 | General availability, gated per practice by upgrade window. |

## Open questions for the company

1. Is there already a clinical-notes table in the real schema, and what reads it
   (reports, exports, other integrations)?
2. Which operatories have workstations, and what audio hardware is acceptable?
3. Consent: per visit, per patient, or practice-level signage? State rules vary
   (two-party consent states).
4. Audio retention after signing: delete, or keep for a set period?
5. Is there a GPU-capable host in the data center for the local drafter, or is
   offline mode "queue and wait" only?
6. Which BAA-covered endpoint does compliance accept (Bedrock or Foundry), and
   which American model on it?
7. What connects the data center to the cloud today (VPN, ExpressRoute, Direct
   Connect), and how often does it drop?
