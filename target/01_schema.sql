/*==============================================================================
  DENTASYS target schema -- PostgreSQL

  Three schemas, and the separation between them is load-bearing:

    landing_   raw legacy rows, every column text, exactly as they exist on the
               1997 server. No cleaning on the way in. If a value is a CHAR(8)
               date with a two-digit year and trailing spaces, that is what
               lands. Transformation you cannot re-run against the original is
               transformation you cannot debug.

    dentasys   the target model. Multi-tenant, correctly typed, and it keeps
               BOTH the UTC instant and the original wall-clock reading.

    harness    ground truth, used only for scoring. THE MIGRATION MUST NEVER
               READ THIS. It is the answer key; a migration that consults it is
               not a migration, it is a lookup. The one guard a schema can give
               you here is a name that makes the violation obvious in review.
==============================================================================*/

CREATE EXTENSION IF NOT EXISTS citext;

DROP SCHEMA IF EXISTS landing_ CASCADE;
DROP SCHEMA IF EXISTS dentasys CASCADE;
DROP SCHEMA IF EXISTS harness  CASCADE;

CREATE SCHEMA landing_;
CREATE SCHEMA dentasys;
CREATE SCHEMA harness;

COMMENT ON SCHEMA landing_ IS 'Raw legacy rows, all text, uncleaned. Re-runnable source of truth for every transform.';
COMMENT ON SCHEMA dentasys IS 'The target model.';
COMMENT ON SCHEMA harness  IS 'Ground truth for scoring. The migration must never read this.';

/*------------------------------------------------------------------------------
  LANDING -- every column text, because that is what the source actually is.

  APPT_DT is CHAR(8) and APPT_TM is CHAR(4) on the legacy server. Landing them
  as date/time here would mean parsing them during transport, where a failure
  is invisible and unattributable. They land as text and get parsed in SQL that
  can be tested.
------------------------------------------------------------------------------*/
CREATE TABLE landing_.practice (
    prac_id      text NOT NULL,
    prac_nm      text,
    addr_1       text,
    city         text,
    st_cd        text,        -- the only geographic signal the product has
    zip_cd       text,
    phone        text,
    schema_ver   text,        -- highest VER_NBR in the practice's SCHEMA_VER log
    PRIMARY KEY (prac_id)
);

CREATE TABLE landing_.appt (
    prac_id      text NOT NULL,
    appt_id      text NOT NULL,
    pat_id       text,
    prov_cd      text,
    oper_cd      text,
    appt_dt      text,        -- YYYYMMDD, local wall-clock, no zone
    appt_tm      text,        -- HHMM,     local wall-clock, no zone
    len_units    text,        -- 10-minute units. Except where 15. See LANDMINE #6.
    appt_stat    text,
    proc_cd      text,
    note_txt     text,
    del_flg      text,        -- 'Y' / 'N' / NULL / '' -- all four occur
    PRIMARY KEY (prac_id, appt_id)
);

/*------------------------------------------------------------------------------
  TARGET

  practice.practice_tz is the column the entire migration turns on. It does not
  exist in the source. It is derived, it is sometimes wrong, and the schema says
  so out loud: tz_source records how it was obtained and tz_confidence records
  how much weight the derivation carries. A migration that writes a derived
  value without recording its provenance has thrown away the only thing that
  makes the error findable later.
------------------------------------------------------------------------------*/
CREATE TYPE dentasys.tz_source AS ENUM (
    'zip_prefix',   -- resolved from ZIP3. Specific enough to be trusted.
    'state_default',-- resolved from ST_CD alone. WRONG for any split state.
    'unresolved'    -- no rule matched. Must not be silently defaulted.
);

CREATE TABLE dentasys.practice (
    practice_id    text        PRIMARY KEY,
    practice_name  text        NOT NULL,
    city           text,
    state_code     char(2),
    postal_code    text,
    schema_version text,

    practice_tz    text,                      -- IANA zone, e.g. America/Boise
    tz_source      dentasys.tz_source NOT NULL,
    tz_confidence  text        NOT NULL,      -- 'high' | 'low' | 'none'

    CONSTRAINT practice_tz_present_unless_unresolved
        CHECK ((tz_source = 'unresolved') = (practice_tz IS NULL))
);

COMMENT ON COLUMN dentasys.practice.practice_tz IS
    'Derived, not migrated. No timezone exists anywhere in the source schema.';
COMMENT ON COLUMN dentasys.practice.tz_source IS
    'How practice_tz was obtained. state_default is a known-unsafe derivation in split states.';

/*------------------------------------------------------------------------------
  appointment

  Keeps BOTH representations, permanently:

    start_utc        for ordering, querying, and anything that spans practices
    local_wall       the business's actual intent, exactly as staff typed it
    practice_tz      the zone the two are related by

  Throwing away the wall-clock reading is the classic irreversible mistake in
  this migration. If the zone was resolved wrongly, start_utc is wrong and
  local_wall is still right -- so keeping both means a bad zone is a recoverable
  error rather than permanent data loss. That property is worth the two columns.

  start_utc is NULLABLE on purpose. A nonexistent local time has no UTC instant,
  and inventing one to satisfy NOT NULL would be the migration lying.
------------------------------------------------------------------------------*/
CREATE TABLE dentasys.appointment (
    practice_id   text        NOT NULL REFERENCES dentasys.practice(practice_id),
    appointment_id bigint     NOT NULL,
    patient_id    bigint,
    provider_code citext,     -- citext: the source collation is case-INSENSITIVE
    operatory_code citext,
    procedure_code citext,

    start_utc     timestamptz,            -- NULL when the local time does not exist
    local_wall    timestamp   NOT NULL,   -- what the front desk actually booked
    practice_tz   text,

    duration_min  integer,                -- NULL when not recoverable from the DB
    duration_unrecoverable boolean NOT NULL DEFAULT false,

    local_time_nonexistent boolean NOT NULL DEFAULT false,
    local_time_ambiguous   boolean NOT NULL DEFAULT false,

    status_code   char(1),
    note          text,
    is_deleted    boolean     NOT NULL,   -- normalized from four truth values

    PRIMARY KEY (practice_id, appointment_id),

    -- A nonexistent local time must not carry a UTC instant, and an existent
    -- one must (unless the zone itself never resolved). Stated as a constraint
    -- because "we flagged it" and "we acted on the flag" are different claims.
    CONSTRAINT nonexistent_has_no_instant
        CHECK (NOT local_time_nonexistent OR start_utc IS NULL),
    CONSTRAINT unrecoverable_duration_is_null
        CHECK (duration_unrecoverable = (duration_min IS NULL))
);

CREATE INDEX appointment_start_utc_idx ON dentasys.appointment (start_utc);
CREATE INDEX appointment_practice_local_idx ON dentasys.appointment (practice_id, local_wall);

/*------------------------------------------------------------------------------
  Exceptions -- the report a human has to work before cutover.

  Every row here is something the migration could not decide correctly on its
  own. The count is not a defect metric; it is the size of the manual queue,
  and knowing it before cutover is the difference between a migration and a
  surprise.
------------------------------------------------------------------------------*/
CREATE TABLE dentasys.migration_exception (
    exception_id  bigserial PRIMARY KEY,
    practice_id   text NOT NULL,
    entity        text NOT NULL,   -- 'practice' | 'appointment'
    entity_key    text,
    kind          text NOT NULL,
    detail        text NOT NULL,
    severity      text NOT NULL    -- 'blocker' | 'review'
);

CREATE INDEX migration_exception_kind_idx ON dentasys.migration_exception (kind);

/*------------------------------------------------------------------------------
  HARNESS -- ground truth. Scoring only.
------------------------------------------------------------------------------*/
CREATE TABLE harness.fleet_roster (
    prac_id       text PRIMARY KEY,
    iana_tz       text NOT NULL,   -- the true zone
    observes_dst  char(1) NOT NULL,
    ver_nbr       text NOT NULL,
    grid_min      integer NOT NULL -- the true meaning of LEN_UNITS
);

COMMENT ON TABLE harness.fleet_roster IS
    'Answer key. Readable by target/04_score.sql only. Never by a transform.';
