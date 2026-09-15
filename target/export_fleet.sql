/*==============================================================================
  Export the legacy fleet as a psql script

  Emits COPY ... FROM stdin blocks on stdout, so the whole transport is:

      sqlcmd -i target/export_fleet.sql | psql

  Deliberately dumb. The transport's only job is to move bytes without
  interpreting them -- every column leaves as text and arrives as text. Parsing
  CHAR(8) dates or FLOAT money in the pipe would put the most failure-prone
  logic in the one place with no tests and no error messages.

  Emits three streams:
    landing_.practice      raw practice rows
    landing_.appt          raw appointment rows, all 24 practices
    harness.fleet_roster   ground truth -- into the fenced schema, for scoring
==============================================================================*/

SET NOCOUNT ON;
GO

/*-- COPY text format: \N is NULL, and backslash, tab, CR and LF need escaping.
    Escape the backslash first, or the \N sentinel gets mangled. ------------*/
CREATE OR ALTER FUNCTION dbo.pgtext (@s VARCHAR(MAX))
RETURNS VARCHAR(MAX)
AS
BEGIN
    RETURN CASE
        WHEN @s IS NULL THEN '\N'
        ELSE REPLACE(REPLACE(REPLACE(REPLACE(@s, '\', '\\'),
                     CHAR(9), '\t'), CHAR(13), '\r'), CHAR(10), '\n')
    END;
END
GO

DROP TABLE IF EXISTS #PRAC_RAW;
DROP TABLE IF EXISTS #APPT_RAW;
DROP TABLE IF EXISTS #OUT;

CREATE TABLE #PRAC_RAW (
    PRAC_ID VARCHAR(20), PRAC_NM VARCHAR(80), ADDR_1 VARCHAR(80), CITY VARCHAR(60),
    ST_CD VARCHAR(10), ZIP_CD VARCHAR(20), PHONE VARCHAR(20), SCHEMA_VER VARCHAR(20));

CREATE TABLE #APPT_RAW (
    PRAC_ID VARCHAR(20), APPT_ID VARCHAR(20), PAT_ID VARCHAR(20),
    PROV_CD VARCHAR(10), OPER_CD VARCHAR(10), APPT_DT VARCHAR(20), APPT_TM VARCHAR(20),
    LEN_UNITS VARCHAR(20), APPT_STAT VARCHAR(10), PROC_CD VARCHAR(20),
    DEL_FLG VARCHAR(10), NOTE_TXT VARCHAR(500));

CREATE TABLE #OUT (SEQ INT IDENTITY(1,1) PRIMARY KEY, LINE VARCHAR(MAX));

DECLARE @p CHAR(6), @db SYSNAME, @sql NVARCHAR(MAX);
DECLARE x CURSOR LOCAL FAST_FORWARD FOR SELECT PRAC_ID FROM FLEET_ROSTER ORDER BY SEQ;
OPEN x;
FETCH NEXT FROM x INTO @p;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @db = QUOTENAME(N'DENTASYS_' + @p);

    SET @sql = N'SELECT RTRIM(PRAC_ID), PRAC_NM, ADDR_1, CITY, RTRIM(ST_CD), RTRIM(ZIP_CD), RTRIM(PHONE),
                        (SELECT MAX(RTRIM(VER_NBR)) FROM ' + @db + N'.dbo.SCHEMA_VER)
                   FROM ' + @db + N'.dbo.PRACTICE;';
    INSERT INTO #PRAC_RAW EXEC sp_executesql @sql;

    -- RTRIM on the CHAR columns only where the padding is an artifact of the
    -- 1997 column type rather than data. DEL_FLG is left exactly as stored,
    -- because the difference between '', ' ' and NULL is LANDMINE #10 itself.
    SET @sql = N'SELECT RTRIM(@pp), CAST(APPT_ID AS VARCHAR(20)), CAST(PAT_ID AS VARCHAR(20)),
                        RTRIM(PROV_CD), RTRIM(OPER_CD), RTRIM(APPT_DT), RTRIM(APPT_TM),
                        CAST(LEN_UNITS AS VARCHAR(20)), APPT_STAT, RTRIM(PROC_CD),
                        DEL_FLG, NOTE_TXT
                   FROM ' + @db + N'.dbo.APPT;';
    INSERT INTO #APPT_RAW EXEC sp_executesql @sql, N'@pp CHAR(6)', @pp = @p;

    FETCH NEXT FROM x INTO @p;
END
CLOSE x;
DEALLOCATE x;

/*------------------------------------------------------------------------------
  Emit
------------------------------------------------------------------------------*/
INSERT INTO #OUT (LINE) VALUES ('\set ON_ERROR_STOP on');
INSERT INTO #OUT (LINE) VALUES ('BEGIN;');
INSERT INTO #OUT (LINE) VALUES ('TRUNCATE landing_.practice, landing_.appt, harness.fleet_roster;');

INSERT INTO #OUT (LINE) VALUES
    ('COPY landing_.practice (prac_id, prac_nm, addr_1, city, st_cd, zip_cd, phone, schema_ver) FROM stdin;');
INSERT INTO #OUT (LINE)
SELECT dbo.pgtext(PRAC_ID) + CHAR(9) + dbo.pgtext(PRAC_NM) + CHAR(9) + dbo.pgtext(ADDR_1) + CHAR(9)
     + dbo.pgtext(CITY)    + CHAR(9) + dbo.pgtext(ST_CD)   + CHAR(9) + dbo.pgtext(ZIP_CD) + CHAR(9)
     + dbo.pgtext(PHONE)   + CHAR(9) + dbo.pgtext(SCHEMA_VER)
  FROM #PRAC_RAW;
INSERT INTO #OUT (LINE) VALUES ('\.');

INSERT INTO #OUT (LINE) VALUES
    ('COPY landing_.appt (prac_id, appt_id, pat_id, prov_cd, oper_cd, appt_dt, appt_tm, len_units, appt_stat, proc_cd, del_flg, note_txt) FROM stdin;');
INSERT INTO #OUT (LINE)
SELECT dbo.pgtext(PRAC_ID)   + CHAR(9) + dbo.pgtext(APPT_ID)   + CHAR(9) + dbo.pgtext(PAT_ID)  + CHAR(9)
     + dbo.pgtext(PROV_CD)   + CHAR(9) + dbo.pgtext(OPER_CD)   + CHAR(9) + dbo.pgtext(APPT_DT) + CHAR(9)
     + dbo.pgtext(APPT_TM)   + CHAR(9) + dbo.pgtext(LEN_UNITS) + CHAR(9) + dbo.pgtext(APPT_STAT) + CHAR(9)
     + dbo.pgtext(PROC_CD)   + CHAR(9) + dbo.pgtext(DEL_FLG)   + CHAR(9) + dbo.pgtext(NOTE_TXT)
  FROM #APPT_RAW;
INSERT INTO #OUT (LINE) VALUES ('\.');

-- Ground truth goes to the fenced schema. The transform never reads it.
INSERT INTO #OUT (LINE) VALUES
    ('COPY harness.fleet_roster (prac_id, iana_tz, observes_dst, ver_nbr, grid_min) FROM stdin;');
INSERT INTO #OUT (LINE)
SELECT dbo.pgtext(RTRIM(PRAC_ID)) + CHAR(9) + dbo.pgtext(IANA_TZ) + CHAR(9)
     + dbo.pgtext(OBSERVES_DST)   + CHAR(9) + dbo.pgtext(RTRIM(VER_NBR)) + CHAR(9)
     + dbo.pgtext(CAST(GRID_MIN AS VARCHAR(10)))
  FROM FLEET_ROSTER ORDER BY SEQ;
INSERT INTO #OUT (LINE) VALUES ('\.');

INSERT INTO #OUT (LINE) VALUES ('COMMIT;');

SELECT LINE FROM #OUT ORDER BY SEQ;
GO
