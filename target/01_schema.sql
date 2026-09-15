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

    -- The raw unit count, stored as-is. Minutes are NOT stored, because minutes
    -- are a CALCULATION -- length_units x the practice's grid -- and the grid is
    -- not in this database. Persisting a computed duration here would bake an
    -- assumption about the grid into the data, permanently and invisibly. The
    -- application computes it, from configuration, where it can be changed when
    -- the discovery finally happens.
    length_units  integer,

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
        CHECK (NOT local_time_nonexistent OR start_utc IS NULL)
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

/*==============================================================================
  Reference model

  Everything below is DATA. None of it carries behavior -- no triggers, no
  procedures, no computed business rules. The 1997 system put its logic in the
  database because that was the only place to put it; the target puts logic in
  C# where it can be unit tested, reviewed as a diff, and debugged with a
  debugger. The database's job here is to hold rows and enforce integrity.
==============================================================================*/

CREATE TABLE landing_.pat_mstr (
    prac_id text NOT NULL, pat_id text NOT NULL, chart_nbr text,
    last_nm text, first_nm text, mid_init text, pat_dob text, sex_cd text,
    -- Last four only. The source column is a full plaintext SSN (LANDMINE #11)
    -- and it is truncated on the LEGACY side, before export, so the full value
    -- never crosses the wire and never exists in the target database at all.
    -- Landing it here and declining to copy it forward would still have put
    -- plaintext PHI in a new system.
    ssn_last4 text, home_phone text, prim_prov text, bal_amt text, last_visit text,
    del_flg text,
    PRIMARY KEY (prac_id, pat_id)
);

CREATE TABLE landing_.prov (
    prac_id text NOT NULL, prov_cd text NOT NULL,
    prov_nm text, prov_type text, npi text, active_flg text,
    PRIMARY KEY (prac_id, prov_cd)
);

CREATE TABLE landing_.oper (
    prac_id text NOT NULL, oper_cd text NOT NULL, oper_nm text, active_flg text,
    PRIMARY KEY (prac_id, oper_cd)
);

CREATE TABLE landing_.proc_code (
    prac_id text NOT NULL, proc_cd text NOT NULL,
    proc_desc text, default_fee text, active_flg text,
    PRIMARY KEY (prac_id, proc_cd)
);

/*------------------------------------------------------------------------------
  patient

  balance is numeric(12,2). The source is FLOAT, so the migrated value will
  DISAGREE with the legacy value by fractions of a cent -- correctly. The parity
  harness has to classify that as expected divergence with a known bound rather
  than a regression (LANDMINE #2).

  ssn_last4 only. The source stores a full plaintext SSN (LANDMINE #11); the
  target does not, on purpose. Full identifiers belong in a separate encrypted
  store with its own access path, and a migration is the one moment you get to
  stop copying them forward. The harness classifies this as omitted-by-policy,
  which is neither agreement nor a defect.
------------------------------------------------------------------------------*/
CREATE TABLE dentasys.patient (
    practice_id   text NOT NULL REFERENCES dentasys.practice(practice_id),
    patient_id    bigint NOT NULL,
    chart_number  citext,             -- practice-assigned, NOT unique
    last_name     text,
    first_name    text,
    middle_initial char(1),
    date_of_birth date,
    sex_code      char(1),
    ssn_last4     char(4),
    home_phone    text,
    primary_provider citext,
    balance       numeric(12,2),
    last_visit    date,
    is_deleted    boolean NOT NULL,
    PRIMARY KEY (practice_id, patient_id)
);

CREATE TABLE dentasys.provider (
    practice_id   text NOT NULL REFERENCES dentasys.practice(practice_id),
    provider_code citext NOT NULL,
    provider_name text,
    provider_type char(1),
    npi           text,
    is_active     boolean NOT NULL,
    PRIMARY KEY (practice_id, provider_code)
);

CREATE TABLE dentasys.operatory (
    practice_id    text NOT NULL REFERENCES dentasys.practice(practice_id),
    operatory_code citext NOT NULL,
    operatory_name text,
    is_active      boolean NOT NULL,
    PRIMARY KEY (practice_id, operatory_code)
);

/*------------------------------------------------------------------------------
  procedure_code

  citext, deliberately. The source collation is case-INSENSITIVE, so 'd1110'
  joins to 'D1110' and has for 29 years. Under PostgreSQL's case-sensitive
  default that join silently drops and the description renders NULL on the
  schedule -- no error, no log (LANDMINE #3). citext preserves the legacy
  behavior exactly, which is why the parity harness reports agreement here.

  That agreement is a RESULT, not a given. Change this column to text and the
  harness reports regressions on every practice with a lowercase code.
------------------------------------------------------------------------------*/
CREATE TABLE dentasys.procedure_code (
    practice_id    text NOT NULL REFERENCES dentasys.practice(practice_id),
    procedure_code citext NOT NULL,
    description    text,
    default_fee    numeric(12,2),
    is_active      boolean NOT NULL,
    PRIMARY KEY (practice_id, procedure_code)
);

/*------------------------------------------------------------------------------
  practice_config

  The appointment grid -- whether LEN_UNITS means 10 or 15 minutes -- is not in
  the database. It is in a .INI file on each workstation (LANDMINE #6), so it
  cannot be migrated; it has to be collected, practice by practice, by a human
  asking a question. This table is where that answer lands when someone gets it.

  It starts EMPTY on purpose. An empty row here is an honest statement that the
  discovery has not happened yet, and every appointment duration stays NULL
  until it does. Defaulting it to 10 would make 20 of 24 practices right and
  silently halve the appointment lengths at the other 4.
------------------------------------------------------------------------------*/
CREATE TABLE dentasys.practice_config (
    practice_id              text PRIMARY KEY REFERENCES dentasys.practice(practice_id),
    appointment_grid_minutes integer NOT NULL,
    source                   text NOT NULL,   -- how it was obtained; never 'assumed'
    collected_at             date NOT NULL,
    CONSTRAINT grid_is_plausible CHECK (appointment_grid_minutes IN (5, 10, 15, 20, 30))
);

COMMENT ON TABLE dentasys.practice_config IS
    'Populated by discovery, not by migration. Empty means nobody has asked yet.';
