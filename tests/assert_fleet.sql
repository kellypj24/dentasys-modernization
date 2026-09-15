/*==============================================================================
  Fleet assertions

  The parity harness does not exist yet. This is the floor beneath it: proof
  that the fixtures the harness will run against are actually the fixtures this
  repo claims to produce. A harness built on a seed nobody checks is a harness
  that reports your seed's bugs as your migration's bugs.

  Every check is stated as an invariant the fleet must satisfy. Failures are
  collected rather than thrown one at a time, so a broken seed reports all of
  its problems in one run instead of one per edit. RAISERROR at the end makes
  sqlcmd -b exit nonzero, which is what `just test` keys off.
==============================================================================*/

SET NOCOUNT ON;
GO
USE DENTASYS_FLEET;
GO

DROP TABLE IF EXISTS #FACTS;
CREATE TABLE #FACTS (
    PRAC_ID      CHAR(6) NOT NULL PRIMARY KEY,
    N_PRACTICE   INT, N_PAT INT, N_APPT INT, N_LEDGER INT, N_RECALL INT, N_SCHEMA_VER INT,
    NCOLS_PAT    INT, NCOLS_APPT INT,
    HAS_TRIGGER  INT,
    LEN_UNITS_60 INT,          -- LEN_UNITS on the 60-minute control appointment
    N_SPRINGFWD  INT,          -- rows on the spring-forward date
    N_FALLBACK   INT,          -- rows on the fall-back date
    NCOLS_REMIND INT,          -- APPT_REMINDER columns, 0 when the table is absent
    N_REMIND     INT           -- reminder rows
);

DECLARE @p CHAR(6), @db SYSNAME, @sql NVARCHAR(MAX);
DECLARE f CURSOR LOCAL FAST_FORWARD FOR SELECT PRAC_ID FROM FLEET_ROSTER ORDER BY SEQ;
OPEN f;
FETCH NEXT FROM f INTO @p;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @db = QUOTENAME(N'DENTASYS_' + @p);
    SET @sql = N'
    SELECT @pp,
      (SELECT COUNT(*) FROM ' + @db + N'.dbo.PRACTICE),
      (SELECT COUNT(*) FROM ' + @db + N'.dbo.PAT_MSTR),
      (SELECT COUNT(*) FROM ' + @db + N'.dbo.APPT),
      (SELECT COUNT(*) FROM ' + @db + N'.dbo.LEDGER),
      (SELECT COUNT(*) FROM ' + @db + N'.dbo.RECALL),
      (SELECT COUNT(*) FROM ' + @db + N'.dbo.SCHEMA_VER),
      (SELECT COUNT(*) FROM ' + @db + N'.INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = ''PAT_MSTR''),
      (SELECT COUNT(*) FROM ' + @db + N'.INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = ''APPT''),
      (SELECT COUNT(*) FROM ' + @db + N'.sys.triggers WHERE name = ''TR_APPT_AUDIT''),
      (SELECT MAX(LEN_UNITS) FROM ' + @db + N'.dbo.APPT WHERE APPT_ID % 1000 = 1),
      (SELECT COUNT(*) FROM ' + @db + N'.dbo.APPT WHERE APPT_DT = ''20260308''),
      (SELECT COUNT(*) FROM ' + @db + N'.dbo.APPT WHERE APPT_DT = ''20261101''),
      (SELECT COUNT(*) FROM ' + @db + N'.INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = ''APPT_REMINDER''),
      (SELECT COUNT(*) FROM ' + @db + N'.INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = ''APPT_REMINDER'');';
    INSERT INTO #FACTS EXEC sp_executesql @sql, N'@pp CHAR(6)', @pp = @p;
    FETCH NEXT FROM f INTO @p;
END
CLOSE f;
DEALLOCATE f;

DECLARE @fail TABLE (SEQ INT IDENTITY, CHECK_NAME VARCHAR(90), DETAIL VARCHAR(200));

/*-- The schema is stratified by era, not uniformly old ----------------------
  APPT_REMINDER shipped in 07.01.04 with a real primary key, DATETIMEOFFSET,
  NVARCHAR, BIT and a check constraint -- none of which the engine was ever
  stopping anyone from using. Its presence on the newer half of the fleet is
  the evidence that the platform kept moving while the 1997 data model did not.
----------------------------------------------------------------------------*/
INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'APPT_REMINDER present exactly on 07.01.04 and later',
       f.PRAC_ID + ' on ' + r.VER_NBR + ' has table=' + CAST(f.N_REMIND AS VARCHAR(5))
  FROM #FACTS f
  JOIN FLEET_ROSTER r ON r.PRAC_ID = f.PRAC_ID
 WHERE f.N_REMIND <> CASE WHEN r.VER_ORD >= 70104 THEN 1 ELSE 0 END;

INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'APPT_REMINDER has its modern column set',
       f.PRAC_ID + ' has ' + CAST(f.NCOLS_REMIND AS VARCHAR(5)) + ' columns, expected 6'
  FROM #FACTS f
  JOIN FLEET_ROSTER r ON r.PRAC_ID = f.PRAC_ID
 WHERE r.VER_ORD >= 70104 AND f.NCOLS_REMIND <> 6;

INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'the newer half of the fleet carries the newer table',
       CAST(COUNT(*) AS VARCHAR(5)) + ' practices, expected 17'
  FROM FLEET_ROSTER WHERE VER_ORD >= 70104
HAVING COUNT(*) <> 17;

/*-- fleet shape ------------------------------------------------------------*/
INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'fleet size', 'roster has ' + CAST(COUNT(*) AS VARCHAR(10)) + ', expected 24'
  FROM FLEET_ROSTER HAVING COUNT(*) <> 24;

INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'every roster practice has a database', 'missing: ' + r.PRAC_ID
  FROM FLEET_ROSTER r
  LEFT JOIN #FACTS f ON f.PRAC_ID = r.PRAC_ID
 WHERE f.PRAC_ID IS NULL;

/*-- single tenancy: the database IS the practice ---------------------------*/
INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'exactly one PRACTICE row per database',
       f.PRAC_ID + ' has ' + CAST(f.N_PRACTICE AS VARCHAR(10))
  FROM #FACTS f WHERE f.N_PRACTICE <> 1;

/*-- row counts: the templates are fixed, so these are exact ----------------*/
INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'row counts per practice',
       f.PRAC_ID + ' pat=' + CAST(f.N_PAT AS VARCHAR(5)) + ' appt=' + CAST(f.N_APPT AS VARCHAR(5))
              + ' ledger=' + CAST(f.N_LEDGER AS VARCHAR(5)) + ' recall=' + CAST(f.N_RECALL AS VARCHAR(5))
              + ' (expected 6/11/9/4)'
  FROM #FACTS f
 WHERE f.N_PAT <> 6 OR f.N_APPT <> 11 OR f.N_LEDGER <> 9 OR f.N_RECALL <> 4;

/*-- SCHEMA_VER is an append log, so its depth encodes the upgrade history --*/
INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'SCHEMA_VER depth matches version ladder position',
       f.PRAC_ID + ' on ' + r.VER_NBR + ' has ' + CAST(f.N_SCHEMA_VER AS VARCHAR(5))
              + ' rows, expected ' + CAST(v.LADDER_POS AS VARCHAR(5))
  FROM #FACTS f
  JOIN FLEET_ROSTER r ON r.PRAC_ID = f.PRAC_ID
  JOIN (SELECT VER_ORD, ROW_NUMBER() OVER (ORDER BY VER_ORD) AS LADDER_POS FROM VER_LADDER) v
       ON v.VER_ORD = r.VER_ORD
 WHERE f.N_SCHEMA_VER <> v.LADDER_POS;

/*-- LANDMINE #7: trigger presence must match the roster exactly ------------*/
INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'TR_APPT_AUDIT presence matches roster',
       f.PRAC_ID + ' roster=' + r.HAS_TRIGGER + ' actual=' + CAST(f.HAS_TRIGGER AS VARCHAR(5))
  FROM #FACTS f
  JOIN FLEET_ROSTER r ON r.PRAC_ID = f.PRAC_ID
 WHERE f.HAS_TRIGGER <> CASE WHEN r.HAS_TRIGGER = 'Y' THEN 1 ELSE 0 END;

INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'roughly a third of the fleet carries the trigger',
       CAST(SUM(HAS_TRIGGER) AS VARCHAR(5)) + ' of 24, expected 8'
  FROM #FACTS HAVING SUM(HAS_TRIGGER) <> 8;

/*-- LANDMINE #6: LEN_UNITS means different things on different grids -------*/
INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'LEN_UNITS derives from the practice grid',
       f.PRAC_ID + ' grid=' + CAST(r.GRID_MIN AS VARCHAR(5))
              + ' LEN_UNITS=' + CAST(f.LEN_UNITS_60 AS VARCHAR(5))
              + ' expected ' + CAST(60 / r.GRID_MIN AS VARCHAR(5))
  FROM #FACTS f
  JOIN FLEET_ROSTER r ON r.PRAC_ID = f.PRAC_ID
 WHERE f.LEN_UNITS_60 <> 60 / r.GRID_MIN;

INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'the fleet actually contains both grids', 'only one distinct LEN_UNITS value present'
  FROM #FACTS HAVING COUNT(DISTINCT LEN_UNITS_60) < 2;

/*-- version drift is structural, not NULLs in a uniform schema -------------*/
INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'PAT_MSTR column count matches version + customization drift',
       f.PRAC_ID + ' on ' + r.VER_NBR + ' drift=' + r.HAS_DRIFT
              + ' has ' + CAST(f.NCOLS_PAT AS VARCHAR(5)) + ' cols, expected '
              + CAST(21 + CASE WHEN r.VER_ORD >= 70104 THEN 2 ELSE 0 END
                        + CASE WHEN r.HAS_DRIFT = 'Y'  THEN 1 ELSE 0 END AS VARCHAR(5))
  FROM #FACTS f
  JOIN FLEET_ROSTER r ON r.PRAC_ID = f.PRAC_ID
 WHERE f.NCOLS_PAT <> 21 + CASE WHEN r.VER_ORD >= 70104 THEN 2 ELSE 0 END
                         + CASE WHEN r.HAS_DRIFT = 'Y'  THEN 1 ELSE 0 END;

INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'APPT column count matches version + drift',
       f.PRAC_ID + ' on ' + r.VER_NBR + ' has ' + CAST(f.NCOLS_APPT AS VARCHAR(5)) + ' cols, expected '
              + CAST(12 + CASE WHEN r.VER_ORD >= 70009 THEN 1 ELSE 0 END
                        + CASE WHEN r.VER_ORD >= 70300 THEN 1 ELSE 0 END
                        + CASE WHEN r.HAS_DRIFT = 'Y'  THEN 1 ELSE 0 END AS VARCHAR(5))
  FROM #FACTS f
  JOIN FLEET_ROSTER r ON r.PRAC_ID = f.PRAC_ID
 WHERE f.NCOLS_APPT <> 12 + CASE WHEN r.VER_ORD >= 70009 THEN 1 ELSE 0 END
                          + CASE WHEN r.VER_ORD >= 70300 THEN 1 ELSE 0 END
                          + CASE WHEN r.HAS_DRIFT = 'Y'  THEN 1 ELSE 0 END;

INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'the fleet is heterogeneous',
       'PAT_MSTR column count is uniform at ' + CAST(MIN(NCOLS_PAT) AS VARCHAR(5))
  FROM #FACTS HAVING MIN(NCOLS_PAT) = MAX(NCOLS_PAT);

/*-- LANDMINE #1: the DST edge rows must exist in every practice ------------*/
INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'DST edge appointments present',
       f.PRAC_ID + ' springfwd=' + CAST(f.N_SPRINGFWD AS VARCHAR(5))
              + ' fallback=' + CAST(f.N_FALLBACK AS VARCHAR(5)) + ' (expected 2/2)'
  FROM #FACTS f WHERE f.N_SPRINGFWD <> 2 OR f.N_FALLBACK <> 2;

/*-- LANDMINE #1: ST_CD must be demonstrably insufficient -------------------*/
INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'states spanning more than one timezone',
       CAST(COUNT(*) AS VARCHAR(5)) + ' states, expected 7'
  FROM (SELECT ST_CD FROM FLEET_ROSTER GROUP BY ST_CD HAVING COUNT(DISTINCT IANA_TZ) > 1) x
HAVING COUNT(*) <> 7;

INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'fleet spans multiple IANA zones',
       CAST(COUNT(DISTINCT IANA_TZ) AS VARCHAR(5)) + ' zones, expected 9'
  FROM FLEET_ROSTER HAVING COUNT(DISTINCT IANA_TZ) <> 9;

INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'fleet includes non-DST jurisdictions',
       CAST(COUNT(*) AS VARCHAR(5)) + ' practices, expected 3'
  FROM FLEET_ROSTER WHERE OBSERVES_DST = 'N' HAVING COUNT(*) <> 3;

/*-- version distribution ---------------------------------------------------*/
INSERT INTO @fail (CHECK_NAME, DETAIL)
SELECT 'version histogram',
       r.VER_NBR + ' has ' + CAST(COUNT(*) AS VARCHAR(5)) + ', expected ' + CAST(e.N AS VARCHAR(5))
  FROM FLEET_ROSTER r
  JOIN (VALUES ('06.04.02',3),('07.00.09',4),('07.01.04',6),('07.02.11',9),('07.03.00',2)) e(VER,N)
       ON e.VER = r.VER_NBR
 GROUP BY r.VER_NBR, e.N
HAVING COUNT(*) <> e.N;

/*------------------------------------------------------------------------------
  Report
------------------------------------------------------------------------------*/
DECLARE @nfail INT = (SELECT COUNT(*) FROM @fail);

IF @nfail = 0
BEGIN
    PRINT '';
    PRINT 'fleet assertions: PASS';
    PRINT '  24 practices | 5 schema versions | 9 IANA zones | 7 split states';
    PRINT '  8 practices carry TR_APPT_AUDIT | 3 carry customization drift | 4 on a 15-min grid';
    PRINT '  17 carry APPT_REMINDER -- a 2021 table with modern types, in the same databases';
    PRINT '';
END
ELSE
BEGIN
    PRINT '';
    PRINT 'fleet assertions: FAIL';
    SELECT SEQ, CHECK_NAME, DETAIL FROM @fail ORDER BY SEQ;
    PRINT '';
    RAISERROR ('fleet assertions failed: %d check(s)', 16, 1, @nfail);
END

DROP TABLE #FACTS;
GO
