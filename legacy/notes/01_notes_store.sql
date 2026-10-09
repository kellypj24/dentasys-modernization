/*==============================================================================
  DENTASYS_NOTES -- the notes service's store (docs/NOTETAKER.md)

  One database for the whole fleet, keyed by practice_id, in the data center
  next to the practice databases. SQL Server because that is what the data
  center runs; written to move: no procedures, no triggers, and types that map
  one-to-one onto PostgreSQL.

  Every timestamp is supplied by the service's clock, never SYSDATETIMEOFFSET(),
  so backoff, leases and holds can be tested by moving time rather than waiting.

  The queues (job, chart_write) are claimed with UPDLOCK + READPAST, SQL
  Server's equivalent of FOR UPDATE SKIP LOCKED, and processed under a lease:
  the claim commits, the slow work (a model call, a write to a practice DB)
  happens outside any transaction, and completion is accepted only from the
  holder of the current lease. A worker that dies mid-job loses its lease and
  the job runs again, so every handler is idempotent.
==============================================================================*/
IF DB_ID('DENTASYS_NOTES') IS NULL CREATE DATABASE DENTASYS_NOTES;
GO
USE DENTASYS_NOTES;
GO
-- sqlcmd defaults this OFF; filtered indexes require it ON. Application
-- connections (SqlClient) already run with it ON.
SET QUOTED_IDENTIFIER ON;
GO
IF SCHEMA_ID('notes') IS NULL EXEC('CREATE SCHEMA notes');
GO

DROP TABLE IF EXISTS notes.audit, notes.chart_write, notes.job, notes.addendum,
                     notes.signature, notes.note_version, notes.note,
                     notes.capture_chunk, notes.capture;
GO

/*-- Capture: minted on the workstation, so capture_id exists before any server
     has heard of it. Chunks are keyed (capture_id, chunk_no): a re-sent chunk
     hits the primary key and is acknowledged, not stored twice. ------------*/
CREATE TABLE notes.capture (
    capture_id    UNIQUEIDENTIFIER  NOT NULL PRIMARY KEY,
    practice_id   CHAR(6)           NOT NULL,
    patient_id    INT               NOT NULL,
    appt_id       INT               NULL,
    provider_cd   CHAR(4)           NOT NULL,
    audio_format  VARCHAR(80)       NOT NULL, -- declared by the agent; checked against the transcriber
    started_at    DATETIMEOFFSET(3) NOT NULL,
    chunk_count   INT               NULL      -- known once the agent says it is done
);

CREATE TABLE notes.capture_chunk (
    capture_id    UNIQUEIDENTIFIER  NOT NULL REFERENCES notes.capture (capture_id),
    chunk_no      INT               NOT NULL,
    content       VARBINARY(MAX)    NOT NULL, -- audio; PHI, encrypted at rest in production
    received_at   DATETIMEOFFSET(3) NOT NULL,
    PRIMARY KEY (capture_id, chunk_no)
);

/*-- Note: one per capture. state is the lifecycle in docs/NOTETAKER.md;
     current_version points at the draft the provider is looking at. --------*/
CREATE TABLE notes.note (
    note_id         UNIQUEIDENTIFIER  NOT NULL PRIMARY KEY REFERENCES notes.capture (capture_id),
    practice_id     CHAR(6)           NOT NULL,
    patient_id      INT               NOT NULL,
    appt_id         INT               NULL,
    provider_cd     CHAR(4)           NOT NULL,
    state           VARCHAR(20)       NOT NULL,
    current_version INT               NULL,
    transcript      NVARCHAR(MAX)     NULL,     -- Transcription JSON: diarized segments with confidence
    updated_at      DATETIMEOFFSET(3) NOT NULL,
    CONSTRAINT ck_note_state CHECK (state IN
        ('awaiting_audio', 'transcribing', 'drafting', 'drafted', 'in_review', 'signed', 'charted'))
);
CREATE INDEX ix_note_practice ON notes.note (practice_id, state);

/*-- Every draft and every edit is a new version; nothing is edited in place.
     is_applied = 0 is a draft that arrived too late to be shown: stored for the
     record, never displayed in place of what the provider was working on. ---*/
CREATE TABLE notes.note_version (
    note_id      UNIQUEIDENTIFIER  NOT NULL REFERENCES notes.note (note_id),
    version      INT               NOT NULL,
    source       VARCHAR(80)       NOT NULL,  -- 'cloud:<model>', 'local:<model>', 'human:<user>'
    content      NVARCHAR(MAX)     NOT NULL,  -- ClinicalNoteDraft JSON
    job_id       UNIQUEIDENTIFIER  NULL,      -- the job that produced it; replays cannot add a second
    is_applied   BIT               NOT NULL,
    created_at   DATETIMEOFFSET(3) NOT NULL,
    PRIMARY KEY (note_id, version)
);
CREATE UNIQUE INDEX ux_note_version_job ON notes.note_version (job_id) WHERE job_id IS NOT NULL;

CREATE TABLE notes.signature (
    note_id    UNIQUEIDENTIFIER  NOT NULL PRIMARY KEY REFERENCES notes.note (note_id),
    version    INT               NOT NULL,
    signed_by  NVARCHAR(40)      NOT NULL,
    signed_at  DATETIMEOFFSET(3) NOT NULL
);

CREATE TABLE notes.addendum (
    addendum_id  UNIQUEIDENTIFIER  NOT NULL PRIMARY KEY,
    note_id      UNIQUEIDENTIFIER  NOT NULL REFERENCES notes.note (note_id),
    text         NVARCHAR(MAX)     NOT NULL,
    signed_by    NVARCHAR(40)      NOT NULL,
    signed_at    DATETIMEOFFSET(3) NOT NULL
);

/*-- Work for the cloud and local models. ------------------------------------*/
CREATE TABLE notes.job (
    job_id       UNIQUEIDENTIFIER  NOT NULL PRIMARY KEY,
    note_id      UNIQUEIDENTIFIER  NOT NULL REFERENCES notes.note (note_id),
    kind         VARCHAR(20)       NOT NULL,
    state        VARCHAR(20)       NOT NULL,
    attempts     INT               NOT NULL,
    run_after    DATETIMEOFFSET(3) NOT NULL,
    lease_token  UNIQUEIDENTIFIER  NULL,
    lease_until  DATETIMEOFFSET(3) NULL,
    last_error   NVARCHAR(400)     NULL,
    CONSTRAINT ck_job_kind  CHECK (kind IN ('transcribe', 'draft', 'redraft')),
    CONSTRAINT ck_job_state CHECK (state IN ('pending', 'done', 'abandoned'))
);
CREATE INDEX ix_job_pending ON notes.job (run_after) WHERE state = 'pending';

/*-- The outbox to the chart: one row per signed note or addendum, committed in
     the same transaction as the signature. -------------------------------- */
CREATE TABLE notes.chart_write (
    item_id      UNIQUEIDENTIFIER  NOT NULL PRIMARY KEY,  -- note_id or addendum_id
    note_id      UNIQUEIDENTIFIER  NOT NULL REFERENCES notes.note (note_id),
    practice_id  CHAR(6)           NOT NULL,
    kind         VARCHAR(10)       NOT NULL,
    state        VARCHAR(20)       NOT NULL,
    attempts     INT               NOT NULL,
    run_after    DATETIMEOFFSET(3) NOT NULL,
    lease_token  UNIQUEIDENTIFIER  NULL,
    lease_until  DATETIMEOFFSET(3) NULL,
    last_error   NVARCHAR(400)     NULL,
    delivered_at DATETIMEOFFSET(3) NULL,
    CONSTRAINT ck_chart_kind  CHECK (kind IN ('NOTE', 'ADDENDUM')),
    CONSTRAINT ck_chart_state CHECK (state IN ('pending', 'delivered'))
);
CREATE INDEX ix_chart_write_pending ON notes.chart_write (run_after) WHERE state = 'pending';

/*-- Ids, states and actors. Never transcript or note text: the audit log is
     read by people who should not be reading PHI to do it. ---------------- */
CREATE TABLE notes.audit (
    audit_id  BIGINT IDENTITY     NOT NULL PRIMARY KEY,
    note_id   UNIQUEIDENTIFIER    NOT NULL,
    at        DATETIMEOFFSET(3)   NOT NULL,
    actor     NVARCHAR(60)        NOT NULL,
    action    VARCHAR(40)         NOT NULL,
    detail    NVARCHAR(400)       NULL
);
CREATE INDEX ix_audit_note ON notes.audit (note_id, at);
GO
