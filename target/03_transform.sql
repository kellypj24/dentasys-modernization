/*==============================================================================
  landing_ -> dentasys

  Reads only landing_ and the reference tables. Does not read harness. If this
  file ever needs the answer key to produce a correct result, the migration is
  not solving the problem -- it is copying the solution.
==============================================================================*/

-- outbox and audit go too: re-running the migration means the fleet is being
-- rebuilt from source, and events describing appointments that no longer exist
-- would be replayed to consumers as though they had just happened.
TRUNCATE dentasys.appointment, dentasys.practice, dentasys.migration_exception,
         dentasys.outbox, dentasys.appointment_audit
    RESTART IDENTITY CASCADE;
ALTER SEQUENCE dentasys.appointment_id_seq RESTART WITH 900000000;

/*------------------------------------------------------------------------------
  practice -- resolve the zone that does not exist in the source
------------------------------------------------------------------------------*/
INSERT INTO dentasys.practice
    (practice_id, practice_name, city, state_code, postal_code, schema_version,
     practice_tz, tz_source, tz_confidence)
SELECT lp.prac_id,
       lp.prac_nm,
       lp.city,
       nullif(trim(lp.st_cd), '')::char(2),
       nullif(trim(lp.zip_cd), ''),
       lp.schema_ver,
       r.iana_tz,
       r.tz_source,
       r.tz_confidence
  FROM landing_.practice lp
  CROSS JOIN LATERAL dentasys.resolve_tz(lp.st_cd, lp.zip_cd) r;

/*------------------------------------------------------------------------------
  Exceptions from the resolution itself.

  Raised BEFORE anyone knows whether the guess was right. That ordering is the
  point: correctness is not knowable here, only trustworthiness.
------------------------------------------------------------------------------*/
INSERT INTO dentasys.migration_exception (practice_id, entity, entity_key, kind, detail, severity)
SELECT p.practice_id, 'practice', p.practice_id,
       'tz_resolved_by_state_default_in_split_state',
       format('%s %s resolved to %s from ST_CD alone -- %s has more than one zone (%s). '
              'No ZIP3 rule covered %s. Confirm with the practice before cutover.',
              p.city, p.state_code, p.practice_tz, p.state_code, s.note, p.postal_code),
       'blocker'
  FROM dentasys.practice p
  JOIN dentasys.tz_state_default s ON s.st_cd = p.state_code
 WHERE p.tz_source = 'state_default' AND s.is_split;

INSERT INTO dentasys.migration_exception (practice_id, entity, entity_key, kind, detail, severity)
SELECT p.practice_id, 'practice', p.practice_id,
       'tz_resolved_by_state_default',
       format('%s %s resolved to %s from ST_CD alone. Single-zone state, so the '
              'derivation is probably safe, but it is still a derivation.',
              p.city, p.state_code, p.practice_tz),
       'review'
  FROM dentasys.practice p
  JOIN dentasys.tz_state_default s ON s.st_cd = p.state_code
 WHERE p.tz_source = 'state_default' AND NOT s.is_split;

INSERT INTO dentasys.migration_exception (practice_id, entity, entity_key, kind, detail, severity)
SELECT p.practice_id, 'practice', p.practice_id,
       'tz_unresolved',
       format('No ZIP3 or state rule matched (ST_CD=%s ZIP=%s). Appointments cannot '
              'be placed on a timeline.', p.state_code, p.postal_code),
       'blocker'
  FROM dentasys.practice p
 WHERE p.tz_source = 'unresolved';

/*------------------------------------------------------------------------------
  Load order

  Reference data goes in BEFORE appointments, because dentasys.appointment now
  carries a foreign key to dentasys.patient and the 1997 schema has no foreign
  keys anywhere. Ordering that did not matter for 29 years starts mattering the
  moment the target enforces integrity the source never did -- which is the
  point of adding it, and also the first thing it breaks.
------------------------------------------------------------------------------*/

/*==============================================================================
  Reference model

  Typed conversions, and each one is a decision the parity harness will have to
  account for:

    bal_amt   FLOAT text -> numeric(12,2). Rounds. Will disagree with legacy by
              fractions of a cent, correctly, on every patient with a balance.
    pat_dob   CHAR(8) 'YYYYMMDD' -> date.
    del_flg   four truth values -> boolean. Only 'Y' means deleted.
    active_flg'Y'/'N' -> boolean, with NULL read as active, matching how the
              legacy screens behave.
==============================================================================*/

TRUNCATE dentasys.patient, dentasys.provider, dentasys.operatory,
         dentasys.procedure_code, dentasys.practice_config RESTART IDENTITY CASCADE;

INSERT INTO dentasys.patient
    (practice_id, patient_id, chart_number, last_name, first_name, middle_initial,
     date_of_birth, sex_code, ssn_last4, home_phone, primary_provider,
     balance, last_visit, is_deleted)
SELECT lp.prac_id,
       lp.pat_id::bigint,
       nullif(trim(lp.chart_nbr), '')::citext,
       nullif(trim(coalesce(lp.last_nm,  '')), ''),
       nullif(trim(coalesce(lp.first_nm, '')), ''),
       nullif(trim(coalesce(lp.mid_init, '')), '')::char(1),
       to_date(nullif(trim(lp.pat_dob), ''), 'YYYYMMDD'),
       nullif(trim(lp.sex_cd), '')::char(1),
       nullif(trim(lp.ssn_last4), '')::char(4),
       nullif(trim(lp.home_phone), ''),
       nullif(trim(lp.prim_prov), '')::citext,
       round(nullif(trim(lp.bal_amt), '')::numeric, 2),
       to_date(nullif(trim(lp.last_visit), ''), 'YYYYMMDD'),
       upper(trim(coalesce(lp.del_flg, ''))) = 'Y'
  FROM landing_.pat_mstr lp
  JOIN dentasys.practice p ON p.practice_id = lp.prac_id;

INSERT INTO dentasys.provider (practice_id, provider_code, provider_name, provider_type, npi, is_active)
SELECT l.prac_id, trim(l.prov_cd)::citext, l.prov_nm,
       nullif(trim(coalesce(l.prov_type, '')), '')::char(1), nullif(trim(l.npi), ''),
       upper(trim(coalesce(l.active_flg, 'Y'))) <> 'N'
  FROM landing_.prov l JOIN dentasys.practice p ON p.practice_id = l.prac_id;

INSERT INTO dentasys.operatory (practice_id, operatory_code, operatory_name, is_active)
SELECT l.prac_id, trim(l.oper_cd)::citext, l.oper_nm,
       upper(trim(coalesce(l.active_flg, 'Y'))) <> 'N'
  FROM landing_.oper l JOIN dentasys.practice p ON p.practice_id = l.prac_id;

INSERT INTO dentasys.procedure_code (practice_id, procedure_code, description, default_fee, is_active)
SELECT l.prac_id, trim(l.proc_cd)::citext, l.proc_desc,
       round(nullif(trim(l.default_fee), '')::numeric, 2),
       upper(trim(coalesce(l.active_flg, 'Y'))) <> 'N'
  FROM landing_.proc_code l JOIN dentasys.practice p ON p.practice_id = l.prac_id;

/*  dentasys.practice_config is deliberately NOT populated. The appointment grid
    is not in the source database and cannot be derived from it. It gets filled
    in by whoever collects the workstation .INI files, one practice at a time,
    and until then every appointment duration in that practice stays NULL.     */


/*------------------------------------------------------------------------------
  appointment

  LEN_UNITS crosses over unchanged, as a unit count. It is NOT converted to
  minutes, because it cannot be: units are 10 minutes on most of the fleet and 15
  on the rest, and the distinguishing configuration lives in a workstation .INI
  file (LANDMINE #6).

  Writing len_units * 10 into a duration column would produce data correct for
  most of the fleet and quietly half-length for the rest -- and once persisted,
  nothing downstream can tell the two apart. Keeping the raw units and computing
  minutes in the application keeps the assumption in one place, visible, and
  changeable the day somebody collects the .INI files.
------------------------------------------------------------------------------*/
INSERT INTO dentasys.appointment
    (practice_id, appointment_id, patient_id, provider_code, operatory_code, procedure_code,
     start_utc, local_wall, practice_tz, length_units,
     local_time_nonexistent, local_time_ambiguous,
     status_code, note, is_deleted)
SELECT la.prac_id,
       la.appt_id::bigint,
       nullif(trim(la.pat_id), '')::bigint,
       nullif(trim(la.prov_cd), '')::citext,
       nullif(trim(la.oper_cd), '')::citext,
       nullif(trim(la.proc_cd), '')::citext,
       c.start_utc,
       w.local_wall,
       p.practice_tz,
       nullif(trim(la.len_units), '')::int,
       coalesce(c.nonexistent, false),
       coalesce(c.ambiguous,   false),
       nullif(trim(la.appt_stat), '')::char(1),
       nullif(trim(coalesce(la.note_txt, '')), ''),
       -- LANDMINE #10: 'Y' / 'N' / NULL / '' all occur, depending on which
       -- client version last wrote the row. Only 'Y' means deleted.
       upper(trim(coalesce(la.del_flg, ''))) = 'Y'
  FROM landing_.appt la
  JOIN dentasys.practice p ON p.practice_id = la.prac_id
  CROSS JOIN LATERAL (
        SELECT make_timestamp(
                   substr(la.appt_dt, 1, 4)::int,
                   substr(la.appt_dt, 5, 2)::int,
                   substr(la.appt_dt, 7, 2)::int,
                   substr(la.appt_tm, 1, 2)::int,
                   substr(la.appt_tm, 3, 2)::int, 0) AS local_wall
       ) w
  LEFT JOIN LATERAL dentasys.classify_local(w.local_wall, p.practice_tz) c
         ON p.practice_tz IS NOT NULL;

/*------------------------------------------------------------------------------
  Exceptions from the conversion
------------------------------------------------------------------------------*/
INSERT INTO dentasys.migration_exception (practice_id, entity, entity_key, kind, detail, severity)
SELECT a.practice_id, 'appointment', a.appointment_id::text,
       'local_time_does_not_exist',
       format('%s in %s falls in the spring-forward gap. There is no such instant, '
              'so the appointment has no UTC time. Someone has to say what was meant.',
              to_char(a.local_wall, 'YYYY-MM-DD HH24:MI'), a.practice_tz),
       'blocker'
  FROM dentasys.appointment a
 WHERE a.local_time_nonexistent;

INSERT INTO dentasys.migration_exception (practice_id, entity, entity_key, kind, detail, severity)
SELECT a.practice_id, 'appointment', a.appointment_id::text,
       'local_time_is_ambiguous',
       format('%s in %s occurs twice on the fall-back date. The row cannot say which, '
              'and no amount of looking at it will tell you. Tie-break: the later '
              '(standard-time) occurrence. Documented, deterministic, and still a guess.',
              to_char(a.local_wall, 'YYYY-MM-DD HH24:MI'), a.practice_tz),
       'review'
  FROM dentasys.appointment a
 WHERE a.local_time_ambiguous;

INSERT INTO dentasys.migration_exception (practice_id, entity, entity_key, kind, detail, severity)
SELECT p.practice_id, 'practice', p.practice_id,
       'appointment_grid_not_discovered',
       format('No row in dentasys.practice_config. LEN_UNITS is 10-minute units on part '
              'of the fleet and 15 on the rest, and the grid lives in a workstation .INI '
              'that will not be migrated. Until somebody collects it, all %s appointments '
              'here render with no duration and no end time.', count(*)),
       'blocker'
  FROM dentasys.appointment a
  JOIN dentasys.practice p ON p.practice_id = a.practice_id
 WHERE NOT EXISTS (SELECT 1 FROM dentasys.practice_config pc WHERE pc.practice_id = p.practice_id)
 GROUP BY p.practice_id;
