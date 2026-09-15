/*==============================================================================
  Scoring

  The only file permitted to read harness.fleet_roster.

  Accuracy is the headline number but it is not the important one. The
  important one is the safety property at the bottom: every practice the
  migration got WRONG must already have been flagged as untrustworthy. A
  migration that is 95% accurate and knows which 5% to escalate is shippable.
  One that is 99% accurate and silently confident everywhere is not, because
  the 1% reaches a dental office as an hour of vanished appointments.
==============================================================================*/

\echo ''
\echo '=== timezone inference vs ground truth ==='

SELECT p.tz_source,
       count(*)                                                   AS practices,
       count(*) FILTER (WHERE p.practice_tz = r.iana_tz)          AS correct,
       count(*) FILTER (WHERE p.practice_tz IS DISTINCT FROM r.iana_tz) AS wrong,
       round(100.0 * count(*) FILTER (WHERE p.practice_tz = r.iana_tz) / count(*), 1) AS pct
  FROM dentasys.practice p
  JOIN harness.fleet_roster r ON r.prac_id = p.practice_id
 GROUP BY p.tz_source
 ORDER BY p.tz_source;

SELECT count(*)                                              AS fleet,
       count(*) FILTER (WHERE p.practice_tz = r.iana_tz)     AS correct,
       round(100.0 * count(*) FILTER (WHERE p.practice_tz = r.iana_tz) / count(*), 1) AS pct
  FROM dentasys.practice p
  JOIN harness.fleet_roster r ON r.prac_id = p.practice_id;

\echo ''
\echo '=== the misses ==='

SELECT p.practice_id,
       p.city || ', ' || p.state_code AS location,
       p.postal_code,
       p.practice_tz                  AS inferred,
       r.iana_tz                      AS actual,
       p.tz_source,
       CASE WHEN EXISTS (SELECT 1 FROM dentasys.migration_exception e
                          WHERE e.practice_id = p.practice_id
                            AND e.severity = 'blocker'
                            AND e.kind LIKE 'tz_%')
            THEN 'yes' ELSE 'NO -- SILENT' END AS was_flagged
  FROM dentasys.practice p
  JOIN harness.fleet_roster r ON r.prac_id = p.practice_id
 WHERE p.practice_tz IS DISTINCT FROM r.iana_tz
 ORDER BY p.practice_id;

\echo ''
\echo '=== was the unsafe flag worth anything? ==='

WITH j AS (
    SELECT p.practice_id,
           p.practice_tz = r.iana_tz AS correct,
           EXISTS (SELECT 1 FROM dentasys.migration_exception e
                    WHERE e.practice_id = p.practice_id
                      AND e.severity = 'blocker'
                      AND e.kind LIKE 'tz_%') AS flagged
      FROM dentasys.practice p
      JOIN harness.fleet_roster r ON r.prac_id = p.practice_id
)
SELECT count(*) FILTER (WHERE flagged)                      AS flagged_unsafe,
       count(*) FILTER (WHERE flagged AND NOT correct)      AS flagged_and_wrong,
       count(*) FILTER (WHERE flagged AND correct)          AS flagged_but_right,
       count(*) FILTER (WHERE NOT flagged AND NOT correct)  AS unflagged_and_wrong
  FROM j;

\echo ''
\echo '    flagged_but_right is the cost of being conservative.'
\echo '    unflagged_and_wrong is the number that has to be zero.'

\echo ''
\echo '=== DST edges the conversion refused to guess at ==='

SELECT count(*) FILTER (WHERE local_time_nonexistent) AS nonexistent_local_times,
       count(*) FILTER (WHERE local_time_ambiguous)   AS ambiguous_local_times,
       count(*) FILTER (WHERE local_time_nonexistent AND start_utc IS NOT NULL) AS invented_an_instant,
       count(*)                                       AS total_appointments
  FROM dentasys.appointment;

\echo ''
\echo '=== the same row, in a DST zone and a non-DST zone ==='

SELECT a.practice_id,
       p.practice_tz,
       to_char(a.local_wall, 'YYYY-MM-DD HH24:MI') AS booked_local,
       coalesce(to_char(a.start_utc AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI'), '(no such instant)') AS utc,
       a.local_time_nonexistent AS nonexistent,
       a.local_time_ambiguous   AS ambiguous
  FROM dentasys.appointment a
  JOIN dentasys.practice p ON p.practice_id = a.practice_id
 WHERE a.practice_id IN ('000417', '001010')
   AND a.local_wall::date IN (DATE '2026-03-08', DATE '2026-11-01')
 ORDER BY a.local_wall, a.practice_id;

\echo ''
\echo '=== the manual queue this migration hands to a human ==='

SELECT kind, severity, count(*) AS rows
  FROM dentasys.migration_exception
 GROUP BY kind, severity
 ORDER BY severity, count(*) DESC;

/*------------------------------------------------------------------------------
  The safety property, as an assertion rather than a number in a report.
------------------------------------------------------------------------------*/
DO $$
DECLARE
    n_silent  int;
    n_invented int;
    n_bad_dur int;
BEGIN
    SELECT count(*) INTO n_silent
      FROM dentasys.practice p
      JOIN harness.fleet_roster r ON r.prac_id = p.practice_id
     WHERE p.practice_tz IS DISTINCT FROM r.iana_tz
       AND NOT EXISTS (SELECT 1 FROM dentasys.migration_exception e
                        WHERE e.practice_id = p.practice_id
                          AND e.severity = 'blocker'
                          AND e.kind LIKE 'tz_%');

    SELECT count(*) INTO n_invented
      FROM dentasys.appointment
     WHERE local_time_nonexistent AND start_utc IS NOT NULL;

    SELECT count(*) INTO n_bad_dur
      FROM dentasys.appointment
     WHERE duration_unrecoverable <> (duration_min IS NULL);

    IF n_silent > 0 THEN
        RAISE EXCEPTION
            'SAFETY VIOLATION: % practice(s) got the wrong timezone without being flagged as unsafe', n_silent;
    END IF;
    IF n_invented > 0 THEN
        RAISE EXCEPTION
            'SAFETY VIOLATION: % appointment(s) were given a UTC instant for a local time that does not exist', n_invented;
    END IF;
    IF n_bad_dur > 0 THEN
        RAISE EXCEPTION
            'SAFETY VIOLATION: % appointment(s) disagree with their own duration_unrecoverable flag', n_bad_dur;
    END IF;

    RAISE NOTICE '';
    RAISE NOTICE 'safety properties: PASS';
    RAISE NOTICE '  no silently-wrong timezone, no invented instants, no contradictory durations';
    RAISE NOTICE '';
END $$;
