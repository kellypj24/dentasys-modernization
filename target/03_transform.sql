/*==============================================================================
  landing_ -> dentasys

  Reads only landing_ and the reference tables. Does not read harness. If this
  file ever needs the answer key to produce a correct result, the migration is
  not solving the problem -- it is copying the solution.
==============================================================================*/

TRUNCATE dentasys.appointment, dentasys.practice, dentasys.migration_exception
    RESTART IDENTITY CASCADE;

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
  appointment

  LEN_UNITS is landed and then deliberately NOT converted. It is 10-minute units
  on most of the fleet and 15-minute units on the rest, and the distinguishing
  configuration lives in a .INI file on a workstation (LANDMINE #6). Nothing in
  the database can tell you which, so duration_min is NULL and the row says why.

  Writing len_units * 10 here would produce a column that is correct for most
  of the fleet and quietly 50% short for the rest. That is worse than NULL: a
  NULL stops someone, a plausible wrong number does not.
------------------------------------------------------------------------------*/
INSERT INTO dentasys.appointment
    (practice_id, appointment_id, patient_id, provider_code, operatory_code, procedure_code,
     start_utc, local_wall, practice_tz,
     duration_min, duration_unrecoverable,
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
       NULL,                              -- duration: not recoverable. See above.
       true,
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
       'appointment_duration_not_in_database',
       format('LEN_UNITS is 10-minute units on part of the fleet and 15-minute units '
              'on the rest. The grid lives in a workstation .INI file that will not be '
              'migrated, so duration is unrecoverable for all %s appointments here.',
              count(*)),
       'blocker'
  FROM dentasys.appointment a
  JOIN dentasys.practice p ON p.practice_id = a.practice_id
 GROUP BY p.practice_id;
