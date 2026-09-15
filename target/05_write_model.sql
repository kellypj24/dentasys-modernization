/*==============================================================================
  The write side

  Reads could be wrong and you would find out. Writes are wrong permanently, so
  the constraints the 1997 schema never had go in here -- and every one of them
  is a behavior change that somebody has to agree to, because staff have spent
  29 years adapting to their absence.
==============================================================================*/

/*------------------------------------------------------------------------------
  Appointment identity

  The legacy client picked APPT_ID itself, which works exactly as long as one
  practice has one database and one client at a time is inserting. Under a shared
  database it does not work at all. New appointments get their id from a sequence
  starting well above anything the fleet has, so migrated and newly-booked rows
  cannot collide while both systems are live during a strangler-fig cutover.
------------------------------------------------------------------------------*/
CREATE SEQUENCE IF NOT EXISTS dentasys.appointment_id_seq AS bigint START WITH 900000000;

/*------------------------------------------------------------------------------
  outbox

  The dual-write problem: "save the appointment, then publish the event" is two
  systems, and anything between them can fail. Crash after the commit and the
  event is lost; publish first and fail to commit and you have announced
  something that did not happen. Neither is acceptable for a recall list that
  drives letters to patients.

  The outbox makes the event part of the SAME transaction as the data change, so
  they succeed or fail together, and a worker drains the table afterwards. The
  broker becomes a delivery detail rather than a correctness dependency -- which
  is also why this repo can demonstrate the pattern without running one.

  What it buys is at-least-once delivery, not exactly-once. Consumers must be
  idempotent. That is a real constraint, not a footnote.
------------------------------------------------------------------------------*/
CREATE TABLE dentasys.outbox (
    event_id     uuid        PRIMARY KEY,
    practice_id  text        NOT NULL,
    event_type   text        NOT NULL,
    occurred_at  timestamptz NOT NULL,
    actor_id     text        NOT NULL,
    payload      jsonb       NOT NULL,

    published_at timestamptz,
    attempts     integer     NOT NULL DEFAULT 0,
    last_error   text
);

-- Partial index: the only query that matters is "what is still unpublished",
-- and that set stays small while the table grows without bound.
CREATE INDEX outbox_pending_idx ON dentasys.outbox (occurred_at)
    WHERE published_at IS NULL;

COMMENT ON TABLE dentasys.outbox IS
    'Domain events, committed with the data change that produced them. Drained by a worker.';

/*------------------------------------------------------------------------------
  Write-side integrity

  None of this existed in 1997. There are no foreign keys, no check constraints
  and no unique indexes anywhere in the legacy schema, so the fat client could
  and did write whatever it liked.

  Note what is NOT here: a constraint preventing two appointments overlapping in
  one operatory. A chair holds one patient at a time and that is as solid as a
  domain invariant gets -- but overlap needs durations, a duration is LEN_UNITS
  times a number that lives in a workstation .INI file, and a constraint cannot
  be conditional on a fact the database does not have. It is enforced in the
  application, for practices whose grid has been discovered, and skipped with a
  reported reason for the rest. A constraint that can only be applied to a third
  of the rows is not a constraint.
------------------------------------------------------------------------------*/
ALTER TABLE dentasys.appointment
    ADD CONSTRAINT appointment_patient_exists
    FOREIGN KEY (practice_id, patient_id) REFERENCES dentasys.patient(practice_id, patient_id);

ALTER TABLE dentasys.appointment
    ADD CONSTRAINT appointment_length_units_positive
    CHECK (length_units IS NULL OR length_units > 0);

/*------------------------------------------------------------------------------
  Audit

  The legacy system's only record of who changed what is APPT.CREATE_USER, set at
  insert and never updated, so a rescheduled appointment records the person who
  first booked it and nothing about whoever moved it.
------------------------------------------------------------------------------*/
CREATE TABLE dentasys.appointment_audit (
    audit_id       bigserial   PRIMARY KEY,
    practice_id    text        NOT NULL,
    appointment_id bigint      NOT NULL,
    action         text        NOT NULL,
    actor_id       text        NOT NULL,
    occurred_at    timestamptz NOT NULL DEFAULT now(),
    detail         jsonb
);

CREATE INDEX appointment_audit_appt_idx
    ON dentasys.appointment_audit (practice_id, appointment_id, occurred_at DESC);
