/*==============================================================================
  Timezone resolution

  The source schema has no timezone. It has PRACTICE.ST_CD, and state is not a
  timezone. This file is the derivation, and it is the most consequential code
  in the migration -- get it wrong and every appointment in an office is off by
  an hour, silently, forever.

  Two tiers, most specific first:

    1. ZIP3 prefix.  Specific enough to be right inside a split state. This is
       the mapping you buy, build, or scrape, and -- critically -- the one you
       will never have complete coverage for. The table below is deliberately
       missing four of the fleet's prefixes, because a reference dataset with
       100% coverage of your fleet is not a thing that happens.

    2. State default. Correct for single-zone states. In a split state it is a
       coin flip weighted by population, and it FAILS SILENTLY: you get a valid
       IANA zone, the conversion succeeds, nothing errors, and a whole office
       is an hour off.

  The important behaviour is not the accuracy number. It is that tier 2 in a
  split state is marked UNSAFE at resolution time, before anyone knows whether
  it happened to be right. The migration cannot know it is wrong. It can know
  it is untrustworthy, and refusing to launder a guess into a fact is the whole
  job.
==============================================================================*/

/*------------------------------------------------------------------------------
  Tier 1 -- ZIP3 -> IANA
------------------------------------------------------------------------------*/
CREATE TABLE dentasys.tz_zip_prefix (
    zip3     char(3) PRIMARY KEY,
    iana_tz  text NOT NULL
);

COMMENT ON TABLE dentasys.tz_zip_prefix IS
    'Deliberately incomplete. Coverage gaps are the normal case, not the error case.';

INSERT INTO dentasys.tz_zip_prefix (zip3, iana_tz) VALUES
    ('850', 'America/Phoenix'),              -- Phoenix AZ
    ('857', 'America/Phoenix'),              -- Tucson AZ
    ('331', 'America/New_York'),             -- Miami FL
    ('325', 'America/Chicago'),              -- Pensacola FL -- panhandle is Central
    ('462', 'America/Indiana/Indianapolis'), -- Indianapolis IN
    ('464', 'America/Chicago'),              -- Gary IN -- Chicago commuter belt
    ('372', 'America/Chicago'),              -- Nashville TN
    ('379', 'America/New_York'),             -- Knoxville TN -- east TN is Eastern
    ('972', 'America/Los_Angeles'),          -- Portland OR
    ('837', 'America/Boise'),                -- Boise ID
    ('770', 'America/Chicago'),              -- Houston TX
    ('799', 'America/Denver'),               -- El Paso TX -- far west TX is Mountain
    ('402', 'America/New_York'),             -- Louisville KY
    ('420', 'America/Chicago'),              -- Paducah KY -- western KY is Central
    ('112', 'America/New_York'),             -- Brooklyn NY
    ('142', 'America/New_York'),             -- Buffalo NY
    ('937', 'America/Los_Angeles'),          -- Fresno CA
    ('921', 'America/Los_Angeles'),          -- San Diego CA
    ('802', 'America/Denver'),               -- Denver CO
    ('968', 'Pacific/Honolulu');             -- Honolulu HI
-- Absent on purpose: 979 (Ontario OR), 838 (Coeur d'Alene ID),
--                    581 (Fargo ND),   995 (Anchorage AK).
-- Two of those four sit in split states. Watch what tier 2 does to them.

/*------------------------------------------------------------------------------
  Tier 2 -- state default, with the split flag that makes it honest
------------------------------------------------------------------------------*/
CREATE TABLE dentasys.tz_state_default (
    st_cd     char(2) PRIMARY KEY,
    iana_tz   text NOT NULL,
    is_split  boolean NOT NULL,
    note      text
);

INSERT INTO dentasys.tz_state_default (st_cd, iana_tz, is_split, note) VALUES
    ('AZ', 'America/Phoenix',              true,  'no DST statewide, except the Navajo Nation, which observes it'),
    ('FL', 'America/New_York',             true,  'panhandle west of the Apalachicola is Central'),
    ('IN', 'America/Indiana/Indianapolis', true,  'northwest and southwest counties are Central'),
    ('TN', 'America/Chicago',              true,  'eastern third is Eastern'),
    ('OR', 'America/Los_Angeles',          true,  'most of Malheur County is Mountain'),
    ('ID', 'America/Boise',                true,  'the panhandle north of the Salmon River is Pacific'),
    ('TX', 'America/Chicago',              true,  'El Paso and Hudspeth counties are Mountain'),
    ('KY', 'America/New_York',             true,  'western half is Central'),
    ('ND', 'America/Chicago',              true,  'southwestern counties are Mountain'),
    ('AK', 'America/Anchorage',            true,  'the western Aleutians are Hawaii-Aleutian'),
    ('NY', 'America/New_York',             false, NULL),
    ('CA', 'America/Los_Angeles',          false, NULL),
    ('CO', 'America/Denver',               false, NULL),
    ('HI', 'Pacific/Honolulu',             false, NULL);

/*------------------------------------------------------------------------------
  The resolver

  Returns the zone, how it was reached, and how much it should be trusted.
  Deliberately returns 'unresolved' rather than a plausible default: a wrong
  zone that looks confident is worse than no zone, because no zone stops the
  cutover and a wrong zone does not.
------------------------------------------------------------------------------*/
CREATE FUNCTION dentasys.resolve_tz(p_st_cd text, p_zip text)
RETURNS TABLE (iana_tz text, tz_source dentasys.tz_source, tz_confidence text)
LANGUAGE sql STABLE AS $$
    WITH zip_hit AS (
        SELECT z.iana_tz
          FROM dentasys.tz_zip_prefix z
         WHERE z.zip3 = left(regexp_replace(coalesce(p_zip, ''), '[^0-9]', '', 'g'), 3)
    ),
    state_hit AS (
        SELECT s.iana_tz, s.is_split
          FROM dentasys.tz_state_default s
         WHERE s.st_cd = upper(trim(coalesce(p_st_cd, '')))
    )
    SELECT z.iana_tz, 'zip_prefix'::dentasys.tz_source, 'high'
      FROM zip_hit z
    UNION ALL
    SELECT s.iana_tz, 'state_default'::dentasys.tz_source, 'low'
      FROM state_hit s
     WHERE NOT EXISTS (SELECT 1 FROM zip_hit)
    UNION ALL
    SELECT NULL, 'unresolved'::dentasys.tz_source, 'none'
     WHERE NOT EXISTS (SELECT 1 FROM zip_hit)
       AND NOT EXISTS (SELECT 1 FROM state_hit);
$$;

/*------------------------------------------------------------------------------
  Local wall-clock -> UTC, with the two DST failures detected rather than
  absorbed.

  PostgreSQL will silently do *something* with both of these. For a nonexistent
  time it shifts forward; for an ambiguous one it picks the first occurrence.
  Neither raises. So the detection has to be explicit:

    nonexistent -- convert to an instant, convert back, and see whether you got
                   the same wall-clock reading you started with. Spring-forward
                   02:30 comes back as 03:30.

    ambiguous   -- take the instant, shift it an hour EITHER WAY, convert back.
                   If either shift renders the SAME wall-clock reading, that
                   reading happens twice. Fall-back 01:30 does; 01:30 on any
                   other date does not.

                   Both directions are probed on purpose. PostgreSQL resolves an
                   ambiguous local time to the LATER (standard-time) instant, so
                   only the -1h probe fires here -- but that is an implementation
                   detail of AT TIME ZONE, not something to build on. Checking
                   both makes the test independent of which side the engine
                   happens to pick. (Assumes a 1-hour shift, which holds for
                   every US zone; Lord Howe Island's 30 minutes would need a
                   second probe.)

  Both are pure functions of (local reading, zone), which is why they can be
  computed for the whole fleet in one pass and why a non-DST practice like
  Phoenix correctly reports false for both on the identical row.
------------------------------------------------------------------------------*/
CREATE FUNCTION dentasys.classify_local(p_local timestamp, p_tz text)
RETURNS TABLE (start_utc timestamptz, nonexistent boolean, ambiguous boolean)
LANGUAGE sql IMMUTABLE AS $$
    WITH probe AS (
        SELECT (p_local AT TIME ZONE p_tz) AS inst
    )
    SELECT
        CASE WHEN (p.inst AT TIME ZONE p_tz) IS DISTINCT FROM p_local
             THEN NULL                       -- no such instant; do not invent one
             ELSE p.inst
        END,
        (p.inst AT TIME ZONE p_tz) IS DISTINCT FROM p_local,
        ((p.inst - interval '1 hour') AT TIME ZONE p_tz) = p_local
     OR ((p.inst + interval '1 hour') AT TIME ZONE p_tz) = p_local
      FROM probe p;
$$;
