/*==============================================================================
  DENTASYS -- fleet spawn

  02_seed.sql builds ONE practice, which is the shape of the on-prem world:
  the database IS the practice. This file builds the shape of the actual
  migration problem -- N single-tenant databases that were never meant to be
  compared to each other, and now have to be.

  Three kinds of divergence are modelled here, because all three are real and
  each one breaks a different assumption:

    1. VERSION drift (release).  Customers schedule their own upgrade windows and
                                 some decline for years, so the fleet is a
                                 *distribution* of SCHEMA_VER, not a version.
                                 Older databases are genuinely missing columns
                                 that newer ones have.

    2. CUSTOMIZATION drift.      Columns, indexes and a trigger added for one
                                 customer under pressure and never generalized,
                                 present in no upgrade script and no
                                 documentation. Nobody currently employed knows
                                 which practices have them.

    3. GEOGRAPHIC drift (physics). Two practices on the identical schema version
                                 in the identical ST_CD can sit in different
                                 IANA timezones. This is LANDMINE #1 and it is
                                 the reason parity output is a histogram.

  All 24 databases live in the same data center. The fleet is a fleet of
  databases, not of locations -- which is exactly why the timezone problem is
  invisible: the servers agree with each other perfectly, and none of them
  agrees with the practice.

  Deliberately deterministic. No RAND(), no NEWID(), no GETDATE() in the data.
  A parity harness whose fixtures move between runs cannot tell you whether the
  diff is your migration or your seed.

  All PHI is FABRICATED -- see LANDMINES #11. SSNs use the 900-999 area range,
  which the SSA has never issued and never will, so nothing in here can collide
  with a real person's number.

  Creates:  DENTASYS_FLEET          control/metadata DB (roster + staging)
            DENTASYS_<PRAC_ID>      one database per practice, N = 24

  Idempotent: re-running drops and respawns every DENTASYS_<PRAC_ID> database
  in the roster. It does NOT touch DENTASYS (the 02_seed single-practice
  sandbox), which is left alone on purpose.
==============================================================================*/

SET NOCOUNT ON;
GO

IF DB_ID('DENTASYS_FLEET') IS NULL
    CREATE DATABASE DENTASYS_FLEET COLLATE SQL_Latin1_General_CP1_CI_AS;
GO
USE DENTASYS_FLEET;
GO

/*------------------------------------------------------------------------------
  FLEET_ROSTER -- the definition of the fleet, and the harness's answer key.

  IANA_TZ is the single most important column in this repo. It is GROUND TRUTH
  that DOES NOT EXIST ANYWHERE IN THE PRODUCT. The migration has to infer the
  zone from PRACTICE.ST_CD, which is insufficient; this column is what the
  harness scores that inference against. Seven states below are deliberately
  represented twice, in two different zones, with the same ST_CD.
------------------------------------------------------------------------------*/
DROP TABLE IF EXISTS FLEET_ROSTER;
GO
CREATE TABLE FLEET_ROSTER (
    SEQ          SMALLINT     NOT NULL,
    PRAC_ID      CHAR(6)      NOT NULL,
    PRAC_NM      VARCHAR(40)  NULL,
    CITY         VARCHAR(30)  NULL,
    ST_CD        CHAR(2)      NULL,
    ZIP_CD       CHAR(10)     NULL,
    IANA_TZ      VARCHAR(40)  NOT NULL,  -- ground truth; absent from the product
    OBSERVES_DST CHAR(1)      NOT NULL,  -- 'N' for AZ and HI
    VER_NBR      CHAR(8)      NOT NULL,
    VER_ORD      INT          NOT NULL,  -- sortable/comparable form of VER_NBR
    GRID_MIN     SMALLINT     NOT NULL,  -- what LEN_UNITS means here. LANDMINE #6
    HAS_TRIGGER  CHAR(1)      NOT NULL,  -- TR_APPT_AUDIT present. LANDMINE #7
    HAS_DRIFT    CHAR(1)      NOT NULL,  -- one-off customer columns/indexes present
    CONSTRAINT PK_FLEET_ROSTER PRIMARY KEY (PRAC_ID)
);
GO

INSERT INTO FLEET_ROSTER
    (SEQ, PRAC_ID, PRAC_NM, CITY, ST_CD, ZIP_CD, IANA_TZ, OBSERVES_DST, VER_NBR, VER_ORD, GRID_MIN, HAS_TRIGGER, HAS_DRIFT)
VALUES
-- Arizona: does not observe DST. A fixed UTC offset per practice is WRONG here
-- for half the year, which is the cleanest possible refutation of "just store
-- the offset."
    ( 1, '000417', 'Camelback Family Dental',       'Phoenix',      'AZ', '85016',      'America/Phoenix',               'N', '07.02.11', 70211, 10, 'N', 'N'),
    ( 2, '000418', 'Saguaro Dental Arts',           'Tucson',       'AZ', '85718',      'America/Phoenix',               'N', '07.01.04', 70104, 15, 'Y', 'N'),
-- Florida -- same ST_CD, two zones. The panhandle is Central.
    ( 3, '000512', 'Biscayne Bay Dental Group',     'Miami',        'FL', '33131',      'America/New_York',              'Y', '07.02.11', 70211, 10, 'N', 'N'),
    ( 4, '000513', 'Pensacola Smile Center',        'Pensacola',    'FL', '32502',      'America/Chicago',               'Y', '07.01.04', 70104, 10, 'Y', 'N'),
-- Indiana -- same ST_CD, two zones, and the Indianapolis zone has its own
-- IANA identifier because Indiana's DST history is genuinely that strange.
    ( 5, '000604', 'Meridian Street Dentistry',     'Indianapolis', 'IN', '46204',      'America/Indiana/Indianapolis',  'Y', '07.02.11', 70211, 10, 'N', 'N'),
    ( 6, '000605', 'Lakeshore Dental Care',         'Gary',         'IN', '46402',      'America/Chicago',               'Y', '06.04.02', 60402, 10, 'Y', 'N'),
-- Tennessee -- same ST_CD, two zones.
    ( 7, '000701', 'Cumberland Dental Partners',    'Nashville',    'TN', '37203',      'America/Chicago',               'Y', '07.02.11', 70211, 15, 'N', 'Y'),
    ( 8, '000702', 'Smoky Mountain Dental',         'Knoxville',    'TN', '37902',      'America/New_York',              'Y', '07.00.09', 70009, 10, 'N', 'N'),
-- Oregon -- same ST_CD, two zones. Malheur County runs on Mountain time.
    ( 9, '000803', 'Willamette Dental Studio',      'Portland',     'OR', '97205',      'America/Los_Angeles',           'Y', '07.02.11', 70211, 10, 'Y', 'N'),
    (10, '000804', 'High Desert Dental',            'Ontario',      'OR', '97914',      'America/Boise',                 'Y', '07.01.04', 70104, 10, 'N', 'N'),
-- Idaho -- same ST_CD, two zones. The panhandle is Pacific.
    (11, '000905', 'Treasure Valley Dental',        'Boise',        'ID', '83702',      'America/Boise',                 'Y', '07.02.11', 70211, 10, 'N', 'N'),
    (12, '000906', 'Lake City Dental Group',        'Coeur d''Alene','ID','83814',      'America/Los_Angeles',           'Y', '06.04.02', 60402, 15, 'Y', 'N'),
-- Texas -- same ST_CD, two zones. El Paso is Mountain.
    (13, '001102', 'Buffalo Bayou Dental',          'Houston',      'TX', '77002',      'America/Chicago',               'Y', '07.02.11', 70211, 10, 'N', 'Y'),
    (14, '001103', 'Franklin Mountain Dental',      'El Paso',      'TX', '79901',      'America/Denver',                'Y', '07.01.04', 70104, 10, 'N', 'N'),
-- Kentucky -- same ST_CD, two zones.
    (15, '001505', 'Ohio River Dental Associates',  'Louisville',   'KY', '40202',      'America/New_York',              'Y', '07.03.00', 70300, 10, 'N', 'N'),
    (16, '001506', 'Four Rivers Dental',            'Paducah',      'KY', '42001',      'America/Chicago',               'Y', '07.00.09', 70009, 10, 'Y', 'Y'),
-- Single-zone states, to keep the roster from being all edge cases.
    (17, '001010', 'Prospect Park Dental',          'Brooklyn',     'NY', '11215',      'America/New_York',              'Y', '07.02.11', 70211, 10, 'N', 'N'),
    (18, '001011', 'Queen City Dental',             'Buffalo',      'NY', '14202',      'America/New_York',              'Y', '07.00.09', 70009, 10, 'Y', 'N'),
    (19, '001204', 'San Joaquin Dental',            'Fresno',       'CA', '93721',      'America/Los_Angeles',           'Y', '07.03.00', 70300, 10, 'N', 'N'),
    (20, '001205', 'Harbor View Dental',            'San Diego',    'CA', '92101',      'America/Los_Angeles',           'Y', '07.01.04', 70104, 15, 'Y', 'N'),
    (21, '001403', 'Front Range Dental',            'Denver',       'CO', '80202',      'America/Denver',                'Y', '07.02.11', 70211, 10, 'N', 'N'),
    (22, '001404', 'Red River Dental',              'Fargo',        'ND', '58102',      'America/Chicago',               'Y', '07.01.04', 70104, 10, 'N', 'N'),
-- Alaska and Hawaii: the offsets nothing else in the fleet exercises, and
-- Hawaii is the second non-DST jurisdiction.
    (23, '001301', 'Cook Inlet Dental',             'Anchorage',    'AK', '99501',      'America/Anchorage',             'Y', '07.00.09', 70009, 10, 'N', 'N'),
    (24, '001302', 'Diamond Head Dental',           'Honolulu',     'HI', '96813',      'Pacific/Honolulu',              'N', '06.04.02', 60402, 10, 'N', 'N');
GO

/*------------------------------------------------------------------------------
  Name pools. Fabricated, and deliberately not drawn from any real roster.
  Indexed arithmetically off SEQ so every practice gets a different-looking
  patient list from the same template without a random number generator.
------------------------------------------------------------------------------*/
DROP TABLE IF EXISTS NAME_POOL;
GO
CREATE TABLE NAME_POOL (IDX SMALLINT NOT NULL, SURNAME VARCHAR(30), GIVEN VARCHAR(20));
GO
INSERT INTO NAME_POOL (IDX, SURNAME, GIVEN) VALUES
    ( 0,'Abernathy','Amara'),   ( 1,'Baptiste','Bertrand'),   ( 2,'Castellanos','Celeste'),
    ( 3,'Dziedzic','Desmond'),  ( 4,'Eriksson','Eloise'),     ( 5,'Fontaine','Ferran'),
    ( 6,'Guerrero','Giselle'),  ( 7,'Halvorsen','Hollis'),    ( 8,'Ibarra','Imani'),
    ( 9,'Jelinek','Jasper'),    (10,'Kowalczyk','Kiona'),     (11,'Lindqvist','Lucian'),
    (12,'Mbeki','Marisol'),     (13,'Nakamura','Nadia'),      (14,'Oyelaran','Osric'),
    (15,'Pemberton','Priya'),   (16,'Quintanilla','Quillon'), (17,'Rasmussen','Rosalind'),
    (18,'Sandoval','Soren'),    (19,'Thibodeaux','Tamsin'),   (20,'Ueda','Ulric'),
    (21,'Vasquez','Verity'),    (22,'Whitmore','Wendell'),    (23,'Yarborough','Xiomara');
GO

/*------------------------------------------------------------------------------
  TPL_PAT -- six patients per practice.
  N=6 is chosen so the fleet totals stay small enough to respawn in seconds
  under Rosetta, while still covering every DEL_FLG value and a pediatric
  patient for the Y2K-pivot recall case.
------------------------------------------------------------------------------*/
DROP TABLE IF EXISTS TPL_PAT;
GO
CREATE TABLE TPL_PAT (
    N SMALLINT, DOB CHAR(8), SEX CHAR(1), PRIM_PROV CHAR(4), PLAN_N SMALLINT,
    BAL FLOAT, LAST_VISIT CHAR(8), DEL_KIND VARCHAR(10),
    NO_FIRST_NM CHAR(1),          -- LANDMINE #9: makes the whole name NULL
    NOTE VARCHAR(50)
);
GO
-- Patient 3 has no FIRST_NM. In the legacy proc,
--   RTRIM(LAST_NM) + ', ' + RTRIM(FIRST_NM)
-- collapses the ENTIRE name to NULL and the row paints as a blank block on the
-- schedule. Staff have worked around it for years. The port has to reproduce
-- that exactly -- PostgreSQL's || behaves the same way, but "cleaning it up" to
-- CONCAT() during the port would silently un-blank those rows and change what
-- the front desk sees. Without a NULL name in the fixtures that is untestable.
INSERT INTO TPL_PAT (N, DOB, SEX, PRIM_PROV, PLAN_N, BAL, LAST_VISIT, DEL_KIND, NO_FIRST_NM, NOTE) VALUES
    (1,'19580312','F','DDS1',1,   0.00,'20260302','LIVE',   'N','medicare age, two plans historically'),
    (2,'19740825','M','DDS1',2, 418.28,'20260302','LIVE',   'N','crown in progress'),
    (3,'19910127','F','DDS2',1,  62.10,'20260304','LIVE',   'Y','no first name -- name collapses to NULL'),
    (4,'20190604','M','HYG1',2,   0.00,'20260310','LIVE',   'N','pediatric -- recall lands past the pivot'),
    (5,'19660919','F','DDS2',3,1487.33,'20251104','LIVE',   'N','balance carries the FLOAT artifact'),
    (6,'19830203','M','HYG1',1, 312.10,'20251106','DELETED','N','soft-deleted; still on the books');
GO

/*------------------------------------------------------------------------------
  TPL_APPT -- eleven appointments per practice, every one of them load-bearing.

  MINUTES is the real-world duration. LEN_UNITS is derived per practice as
  MINUTES / GRID_MIN, which is precisely LANDMINE #6: the same SMALLINT means
  10-minute units on most of the fleet and 15-minute units on the rest, with
  nothing in the database to say which.

  The 45-minute row is there because 45 / 10 is not an integer. Integer
  division silently truncates it to 4 units = 40 minutes on a 10-minute grid.
  That is data loss with no error, and it is in the seed on purpose.
------------------------------------------------------------------------------*/
DROP TABLE IF EXISTS TPL_APPT;
GO
CREATE TABLE TPL_APPT (
    N SMALLINT, PAT_N SMALLINT, PROV CHAR(4), OPER CHAR(4),
    APPT_DT CHAR(8), APPT_TM CHAR(4), MINUTES SMALLINT,
    STAT CHAR(1), PROC_CD CHAR(5), DEL_KIND VARCHAR(10), NOTE VARCHAR(80)
);
GO
INSERT INTO TPL_APPT (N, PAT_N, PROV, OPER, APPT_DT, APPT_TM, MINUTES, STAT, PROC_CD, DEL_KIND, NOTE) VALUES
    ( 1,1,'HYG1','HYG1','20260302','0800',60,'C','D1110','LIVE','ordinary Monday -- the control row'),
    ( 2,2,'DDS1','OP1 ','20260302','0900',90,'C','D2740','LIVE','crown seat'),
    ( 3,3,'DDS2','OP2 ','20260304','1030',30,'C','D0120','LIVE',NULL),
    -- LANDMINE #1, case 3: 02:30 on spring-forward does not exist in any
    -- DST-observing zone. It exists fine in Phoenix and Honolulu. Same row,
    -- same schema, two different truths, decided by a column the DB lacks.
    ( 4,5,'DDS1','OP1 ','20260308','0230',60,'X','D0150','LIVE','SPRING FORWARD -- nonexistent local time in DST zones'),
    -- LANDMINE #8: 2350 + 30 minutes. String arithmetic yields 2380.
    ( 5,6,'HYG1','HYG1','20260308','2350',30,'C','D1206','LIVE','crosses midnight -- string date arithmetic'),
    ( 6,4,'HYG1','HYG1','20260310','1300',60,'C','D1120','LIVE','pediatric prophy'),
    -- 45 minutes: not representable on a 10-minute grid.
    ( 7,2,'DDS1','OP1 ','20260312','0915',45,'C','D2391','LIVE','45 min -- truncates to 40 on a 10-min grid'),
    -- LANDMINE #1, case 4: 01:30 on fall-back happens TWICE. Unrecoverable
    -- from the row alone. This is the row that forces a documented tie-break.
    ( 8,1,'DDS1','OP1 ','20261101','0130',60,'C','D2750','LIVE','FALL BACK -- ambiguous local time, occurs twice'),
    ( 9,3,'DDS2','OP2 ','20261101','0230',30,'C','D0120','LIVE','fall back, unambiguous hour -- the contrast row'),
    (10,5,'HYG1','HYG1','20251104','0800',60,'C','D1110','LIVE','prior year -- proves the harness is not date-scoped'),
    (11,6,'DDS2','OP2 ','20260313','1600',30,'B','D0220','DELETED','soft-deleted appointment');
GO

/*------------------------------------------------------------------------------
  TPL_LEDGER / TPL_RECALL / TPL_PROC / TPL_PROV / TPL_OPER / TPL_INS
------------------------------------------------------------------------------*/
DROP TABLE IF EXISTS TPL_LEDGER;
GO
CREATE TABLE TPL_LEDGER (
    N SMALLINT, PAT_N SMALLINT, TRAN_DT CHAR(8), TRAN_TYPE CHAR(1),
    PROC_CD CHAR(5), PROV CHAR(4), AMT FLOAT, INS_EST FLOAT, PAID FLOAT,
    APPLIED_TO_N SMALLINT, DEL_KIND VARCHAR(10), NOTE VARCHAR(60)
);
GO
-- The amounts are chosen so that charges minus payments does NOT come out even
-- in binary floating point. That is LANDMINE #2 and it has to be in the data,
-- not just in the docs, or the harness has nothing to classify.
INSERT INTO TPL_LEDGER (N, PAT_N, TRAN_DT, TRAN_TYPE, PROC_CD, PROV, AMT, INS_EST, PAID, APPLIED_TO_N, DEL_KIND, NOTE) VALUES
    (1,2,'20260302','C','D2740','DDS1',1285.00, 642.50,   0.00,NULL,'LIVE','crown charge'),
    (2,2,'20260302','I',NULL,   'DDS1',   0.00,   0.00, 642.50,   1,'LIVE','insurance payment'),
    (3,2,'20260315','P',NULL,   'DDS1',   0.00,   0.00, 224.22,   1,'LIVE','patient payment -- 0.1 cannot be represented'),
    (4,3,'20260304','C','D0120','DDS2',  62.10,  31.05,   0.00,NULL,'LIVE','exam'),
    (5,5,'20251104','C','D1110','HYG1', 118.00,  94.40,   0.00,NULL,'LIVE','prophy'),
    (6,5,'20251104','A',NULL,   'HYG1', -23.60,   0.00,   0.00,   5,'LIVE','write-off'),
    (7,6,'20251106','C','D1206','HYG1',  52.10,   0.00,   0.00,NULL,'LIVE','fluoride'),
    (8,1,'20260302','C','D1110','HYG1', 118.00,   0.00, 118.00,NULL,'LIVE','paid same day'),
    -- APPLIED_TO points at a TRAN_ID that will not exist. Orphaned references
    -- are in the real data; a migration with a real FK will reject this row.
    (9,4,'20260310','P',NULL,   'HYG1',   0.00,   0.00,  40.00, 999,'LIVE','orphaned APPLIED_TO -- no such TRAN_ID');
GO

DROP TABLE IF EXISTS TPL_RECALL;
GO
CREATE TABLE TPL_RECALL (N SMALLINT, PAT_N SMALLINT, RECALL_TYPE CHAR(4), DUE_YM CHAR(4), LAST_SENT CHAR(8), DEL_KIND VARCHAR(10), NOTE VARCHAR(60));
GO
-- LANDMINE #5: DUE_YM is YYMM with a 1999-vintage pivot at 50. '5103' is meant
-- to be March 2051 and resolves to March 1951.
INSERT INTO TPL_RECALL (N, PAT_N, RECALL_TYPE, DUE_YM, LAST_SENT, DEL_KIND, NOTE) VALUES
    (1,1,'PROP','2609','20260302','LIVE','ordinary six-month recall'),
    (2,3,'PERI','2610','20260304','LIVE',NULL),
    (3,4,'PROP','5103','20260310','LIVE','pediatric -- pivots to 1951, not 2051'),
    (4,5,'XRAY','2703','20251104','DELETED','cancelled recall');
GO

DROP TABLE IF EXISTS TPL_PROC;
GO
CREATE TABLE TPL_PROC (PROC_CD CHAR(5), PROC_DESC VARCHAR(60), FEE FLOAT, CATEGORY CHAR(4));
GO
INSERT INTO TPL_PROC (PROC_CD, PROC_DESC, FEE, CATEGORY) VALUES
    ('D0120','Periodic oral evaluation',                62.10,'DIAG'),
    ('D0150','Comprehensive oral evaluation',          104.00,'DIAG'),
    ('D0220','Intraoral periapical first film',         38.50,'DIAG'),
    ('D1110','Prophylaxis - adult',                    118.00,'PREV'),
    ('D1120','Prophylaxis - child',                     84.00,'PREV'),
    ('D1206','Topical fluoride varnish',                52.10,'PREV'),
    ('D2391','Resin composite - one surface posterior',215.00,'REST'),
    ('D2740','Crown - porcelain/ceramic',             1285.00,'REST'),
    ('D2750','Crown - porcelain fused to metal',      1190.00,'REST');
GO

DROP TABLE IF EXISTS TPL_PROV;
GO
CREATE TABLE TPL_PROV (N SMALLINT, PROV_CD CHAR(4), PROV_TYPE CHAR(1), SUFFIX VARCHAR(6));
GO
INSERT INTO TPL_PROV (N, PROV_CD, PROV_TYPE, SUFFIX) VALUES
    (1,'DDS1','D','DDS'), (2,'DDS2','D','DMD'), (3,'HYG1','H','RDH'), (4,'SPC1','S','DDS');
GO

DROP TABLE IF EXISTS TPL_OPER;
GO
CREATE TABLE TPL_OPER (OPER_CD CHAR(4), OPER_NM VARCHAR(20));
GO
INSERT INTO TPL_OPER (OPER_CD, OPER_NM) VALUES
    ('OP1 ','OP 1'), ('OP2 ','OP 2'), ('HYG1','HYG 1'), ('HYG2','HYG 2');
GO

DROP TABLE IF EXISTS TPL_INS;
GO
CREATE TABLE TPL_INS (N SMALLINT, CARRIER VARCHAR(40), GRP VARCHAR(20), ANN_MAX FLOAT, DEDUCT FLOAT, PREV SMALLINT, BASIC SMALLINT, MAJOR SMALLINT);
GO
INSERT INTO TPL_INS (N, CARRIER, GRP, ANN_MAX, DEDUCT, PREV, BASIC, MAJOR) VALUES
    (1,'Ironwood Dental Benefits',   'GRP-40182',1500.00,50.00,100,80,50),
    (2,'Northstar Health Dental',    'GRP-77310',2000.00, 0.00,100,80,50),
    (3,'Cascade Employee Dental',    'GRP-11294',1000.00,75.00, 80,60,50);
GO

/*==============================================================================
  SPAWN

  One database per practice. The DDL is assembled per practice from VER_ORD, so
  an 06.04.02 database is genuinely missing columns that a 07.02.11 database
  has -- it is not the same table with NULLs in it. That distinction is the
  whole reason the fleet is hard: a migration written against the current
  schema compiles fine and then meets a database from 2011.

  Column history (vendor):
    07.00.09  + APPT.CREATE_USER, + LEDGER.INS_EST_AMT
    07.01.04  + PAT_MSTR.CUSTOM_4/5, + PROC_CODE.CATEGORY
    07.02.11  + RECALL.DEL_FLG
    07.03.00  + APPT.APPT_TZ_CD  -- see the note at the bottom of this file
==============================================================================*/

DECLARE @seq SMALLINT, @prac CHAR(6), @vord INT, @ver CHAR(8), @grid SMALLINT,
        @trg CHAR(1), @drift CHAR(1), @tz VARCHAR(40),
        @db SYSNAME, @sql NVARCHAR(MAX), @exec NVARCHAR(300);

DECLARE fleet CURSOR LOCAL FAST_FORWARD FOR
    SELECT SEQ, PRAC_ID, VER_ORD, VER_NBR, GRID_MIN, HAS_TRIGGER, HAS_DRIFT, IANA_TZ
      FROM FLEET_ROSTER ORDER BY SEQ;

OPEN fleet;
FETCH NEXT FROM fleet INTO @seq, @prac, @vord, @ver, @grid, @trg, @drift, @tz;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @db    = N'DENTASYS_' + @prac;
    SET @exec  = QUOTENAME(@db) + N'.sys.sp_executesql';

    /*-- respawn ---------------------------------------------------------------
      Only ever touches DENTASYS_<PRAC_ID> names that came out of the roster.
      The 02_seed sandbox database (DENTASYS, no suffix) is never a target.    */
    IF DB_ID(@db) IS NOT NULL
    BEGIN
        SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE;'
                 + N'DROP DATABASE ' + QUOTENAME(@db) + N';';
        EXEC sp_executesql @sql;
    END

    SET @sql = N'CREATE DATABASE ' + QUOTENAME(@db) + N' COLLATE SQL_Latin1_General_CP1_CI_AS;';
    EXEC sp_executesql @sql;

    /*-- DDL, assembled from the practice's schema version --------------------*/
    SET @sql = N'
CREATE TABLE SCHEMA_VER (VER_NBR CHAR(8) NOT NULL, APPLIED_DT CHAR(8) NULL, APPLIED_BY VARCHAR(30) NULL);

CREATE TABLE PRACTICE (
    PRAC_ID CHAR(6) NOT NULL, PRAC_NM VARCHAR(40) NULL, ADDR_1 VARCHAR(40) NULL,
    CITY VARCHAR(30) NULL, ST_CD CHAR(2) NULL, ZIP_CD CHAR(10) NULL,
    PHONE CHAR(10) NULL, TAX_ID CHAR(9) NULL);

CREATE TABLE PROV (
    PROV_CD CHAR(4) NOT NULL, PROV_NM VARCHAR(40) NULL, PROV_TYPE CHAR(1) NULL,
    NPI CHAR(10) NULL, ACTIVE_FLG CHAR(1) NULL);

CREATE TABLE OPER (
    OPER_CD CHAR(4) NOT NULL, OPER_NM VARCHAR(20) NULL, ACTIVE_FLG CHAR(1) NULL);

CREATE TABLE INS_PLAN (
    INS_PLAN_ID INT NOT NULL, CARRIER_NM VARCHAR(40) NULL, GROUP_NBR VARCHAR(20) NULL,
    ANN_MAX_AMT FLOAT NULL, DEDUCT_AMT FLOAT NULL, COV_PCT_PREV SMALLINT NULL,
    COV_PCT_BASIC SMALLINT NULL, COV_PCT_MAJOR SMALLINT NULL);

CREATE TABLE PROC_CODE (
    PROC_CD CHAR(5) NOT NULL, PROC_DESC VARCHAR(60) NULL, DEFAULT_FEE FLOAT NULL'
    + CASE WHEN @vord >= 70104 THEN N', CATEGORY CHAR(4) NULL' ELSE N'' END + N',
    ACTIVE_FLG CHAR(1) NULL);

CREATE TABLE PAT_MSTR (
    PAT_ID INT NOT NULL, CHART_NBR CHAR(10) NULL, LAST_NM VARCHAR(30) NULL,
    FIRST_NM VARCHAR(20) NULL, MID_INIT CHAR(1) NULL, PAT_DOB CHAR(8) NULL,
    SEX_CD CHAR(1) NULL, SSN CHAR(9) NULL, ADDR_1 VARCHAR(40) NULL,
    CITY VARCHAR(30) NULL, ST_CD CHAR(2) NULL, ZIP_CD CHAR(10) NULL,
    HOME_PHONE CHAR(10) NULL, PRIM_PROV CHAR(4) NULL, INS_PLAN_ID INT NULL,
    BAL_AMT FLOAT NULL, LAST_VISIT CHAR(8) NULL, DEL_FLG CHAR(1) NULL,
    CUSTOM_1 VARCHAR(50) NULL, CUSTOM_2 VARCHAR(50) NULL, CUSTOM_3 VARCHAR(50) NULL'
    + CASE WHEN @vord >= 70104 THEN N', CUSTOM_4 VARCHAR(50) NULL, CUSTOM_5 VARCHAR(50) NULL' ELSE N'' END
    + CASE WHEN @drift = 'Y'   THEN N', CUST_XREF VARCHAR(20) NULL' ELSE N'' END + N');
CREATE CLUSTERED INDEX IX_PAT_MSTR_ID ON PAT_MSTR (PAT_ID);

CREATE TABLE APPT (
    APPT_ID INT NOT NULL, PAT_ID INT NULL, PROV_CD CHAR(4) NULL, OPER_CD CHAR(4) NULL,
    APPT_DT CHAR(8) NOT NULL, APPT_TM CHAR(4) NOT NULL, LEN_UNITS SMALLINT NULL,
    APPT_STAT CHAR(1) NULL, PROC_CD CHAR(5) NULL, NOTE_TXT VARCHAR(255) NULL,
    CREATE_DTM DATETIME NULL, DEL_FLG CHAR(1) NULL'
    + CASE WHEN @vord >= 70009 THEN N', CREATE_USER VARCHAR(20) NULL' ELSE N'' END
    + CASE WHEN @vord >= 70300 THEN N', APPT_TZ_CD CHAR(4) NULL'    ELSE N'' END
    + CASE WHEN @drift = 'Y'   THEN N', SMS_OPT_IN CHAR(1) NULL'    ELSE N'' END + N');
CREATE CLUSTERED INDEX IX_APPT_DT ON APPT (APPT_DT, APPT_TM);

CREATE TABLE LEDGER (
    TRAN_ID INT NOT NULL, PAT_ID INT NULL, TRAN_DT CHAR(8) NULL, TRAN_TYPE CHAR(1) NULL,
    PROC_CD CHAR(5) NULL, PROV_CD CHAR(4) NULL, AMT FLOAT NULL'
    + CASE WHEN @vord >= 70009 THEN N', INS_EST_AMT FLOAT NULL' ELSE N'' END + N',
    PAID_AMT FLOAT NULL, APPLIED_TO INT NULL, POST_DTM DATETIME NULL, DEL_FLG CHAR(1) NULL);
CREATE CLUSTERED INDEX IX_LEDGER_PAT ON LEDGER (PAT_ID, TRAN_DT);

CREATE TABLE RECALL (
    RECALL_ID INT NOT NULL, PAT_ID INT NULL, RECALL_TYPE CHAR(4) NULL,
    DUE_YM CHAR(4) NULL, LAST_SENT_DT CHAR(8) NULL'
    + CASE WHEN @vord >= 70211 THEN N', DEL_FLG CHAR(1) NULL' ELSE N'' END + N');'
    /*  APPT_REMINDER arrived in 07.01.04 (2021) and looks nothing like the tables
        around it: real primary key, DATETIMEOFFSET, NVARCHAR, BIT, a check
        constraint. The platform was never the constraint -- SQL Server has had
        all of this for years. The 1997 tables kept their 1997 types because
        changing them was the risk nobody could justify, not because the engine
        could not do better.

        It still cannot have a foreign key to APPT, because APPT has no primary
        key for one to reference.                                             */
    + CASE WHEN @vord >= 70104 THEN N'
CREATE TABLE APPT_REMINDER (
    REMINDER_ID BIGINT IDENTITY(1,1) NOT NULL,
    APPT_ID INT NOT NULL,
    CHANNEL NVARCHAR(10) NOT NULL,
    SCHEDULED_AT DATETIMEOFFSET(0) NOT NULL,
    SENT_AT DATETIMEOFFSET(0) NULL,
    IS_OPTED_OUT BIT NOT NULL CONSTRAINT DF_APPT_REMINDER_OPT DEFAULT (0),
    CONSTRAINT PK_APPT_REMINDER PRIMARY KEY (REMINDER_ID),
    CONSTRAINT CK_APPT_REMINDER_CHANNEL CHECK (CHANNEL IN (N''SMS'', N''EMAIL''))
);
CREATE INDEX IX_APPT_REMINDER_APPT ON APPT_REMINDER (APPT_ID);' ELSE N'' END
    -- Customization drift: an index no upgrade script knows about, added for
    -- one customer's integration and never removed.
    + CASE WHEN @drift = 'Y' THEN N'
CREATE NONCLUSTERED INDEX IX_PAT_XREF ON PAT_MSTR (CUST_XREF);' ELSE N'' END;

    EXEC @exec @sql;

    /*-- LANDMINE #7: present on a third of the fleet, in no documentation -----*/
    IF @trg = 'Y'
    BEGIN
        SET @sql = N'CREATE TRIGGER TR_APPT_AUDIT ON APPT AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE PAT_MSTR
       SET LAST_VISIT = i.APPT_DT
      FROM PAT_MSTR p
      JOIN inserted i ON i.PAT_ID = p.PAT_ID
     WHERE i.APPT_STAT = ''C'';
END';
        EXEC @exec @sql;
    END

    FETCH NEXT FROM fleet INTO @seq, @prac, @vord, @ver, @grid, @trg, @drift, @tz;
END

CLOSE fleet;
DEALLOCATE fleet;
GO

/*------------------------------------------------------------------------------
  VER_LADDER -- SCHEMA_VER is an append-only upgrade log, not a single row.
  Each practice gets every release up to and including the one it is on, which
  is what lets you see that a practice has been sitting on 06.04.02 since 2014.
------------------------------------------------------------------------------*/
DROP TABLE IF EXISTS VER_LADDER;
GO
CREATE TABLE VER_LADDER (VER_NBR CHAR(8), VER_ORD INT, APPLIED_DT CHAR(8), APPLIED_BY VARCHAR(30));
GO
INSERT INTO VER_LADDER (VER_NBR, VER_ORD, APPLIED_DT, APPLIED_BY) VALUES
    ('06.04.02', 60402, '20140317', 'VANTAGE_DENTAL_SVC'),
    ('07.00.09', 70009, '20180612', 'VANTAGE_DENTAL_SVC'),
    ('07.01.04', 70104, '20210208', 'MERIDIAN_RELEASE_ENG'),
    ('07.02.11', 70211, '20230419', 'MERIDIAN_RELEASE_ENG'),
    ('07.03.00', 70300, '20260126', 'VANTAGE_DENTAL_SVC');
GO

/*==============================================================================
  LOAD

  Runs from the control database using three-part names, so the row-building
  SQL stays ordinary set-based SQL instead of a wall of escaped string literals.
  The staging templates are the source of truth for both the fleet AND the
  harness's expected values -- same rows, one definition.
==============================================================================*/

DECLARE @seq SMALLINT, @prac CHAR(6), @vord INT, @grid SMALLINT, @drift CHAR(1),
        @tz VARCHAR(40), @offmin INT,
        @db SYSNAME, @sql NVARCHAR(MAX), @q NVARCHAR(10) = N'.dbo.';

DECLARE load CURSOR LOCAL FAST_FORWARD FOR
    SELECT SEQ, PRAC_ID, VER_ORD, GRID_MIN, HAS_DRIFT, IANA_TZ FROM FLEET_ROSTER ORDER BY SEQ;

OPEN load;
FETCH NEXT FROM load INTO @seq, @prac, @vord, @grid, @drift, @tz;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @db = QUOTENAME(N'DENTASYS_' + @prac);

    -- The practice's STANDARD-time offset in minutes. Derived here from the
    -- roster because this is fixture generation; the product itself has no idea.
    SET @offmin = CASE
        WHEN @tz = 'Pacific/Honolulu'      THEN -600
        WHEN @tz = 'America/Anchorage'     THEN -540
        WHEN @tz = 'America/Los_Angeles'   THEN -480
        WHEN @tz IN ('America/Phoenix', 'America/Boise', 'America/Denver') THEN -420
        WHEN @tz = 'America/Chicago'       THEN -360
        ELSE -300
    END;

    /*-- SCHEMA_VER: full upgrade history up to this practice's release -------*/
    SET @sql = N'INSERT INTO ' + @db + @q + N'SCHEMA_VER (VER_NBR, APPLIED_DT, APPLIED_BY)
                 SELECT VER_NBR, APPLIED_DT, APPLIED_BY FROM DENTASYS_FLEET.dbo.VER_LADDER
                  WHERE VER_ORD <= @v ORDER BY VER_ORD;';
    EXEC sp_executesql @sql, N'@v INT', @v = @vord;

    /*-- PRACTICE: exactly one row, because the database IS the practice ------*/
    SET @sql = N'INSERT INTO ' + @db + @q + N'PRACTICE (PRAC_ID, PRAC_NM, ADDR_1, CITY, ST_CD, ZIP_CD, PHONE, TAX_ID)
                 SELECT PRAC_ID, PRAC_NM,
                        CAST(100 + SEQ AS VARCHAR(4)) + '' Main Street, Suite '' + CAST(SEQ AS VARCHAR(3)),
                        CITY, ST_CD, ZIP_CD,
                        RIGHT(''000'' + CAST(200 + SEQ AS VARCHAR(3)), 3) + ''5550100'',
                        ''9'' + RIGHT(''00000000'' + CAST(4000000 + SEQ AS VARCHAR(8)), 8)
                   FROM DENTASYS_FLEET.dbo.FLEET_ROSTER WHERE PRAC_ID = @p;';
    EXEC sp_executesql @sql, N'@p CHAR(6)', @p = @prac;

    /*-- PROV / OPER / INS_PLAN / PROC_CODE -----------------------------------*/
    SET @sql = N'INSERT INTO ' + @db + @q + N'PROV (PROV_CD, PROV_NM, PROV_TYPE, NPI, ACTIVE_FLG)
                 SELECT t.PROV_CD, np.SURNAME + '', '' + LEFT(np.GIVEN,1) + ''. '' + t.SUFFIX, t.PROV_TYPE,
                        ''1'' + RIGHT(''000000000'' + CAST(r.SEQ * 40 + t.N AS VARCHAR(9)), 9),
                        CASE WHEN t.N = 4 THEN ''N'' ELSE ''Y'' END
                   FROM DENTASYS_FLEET.dbo.FLEET_ROSTER r
                  CROSS JOIN DENTASYS_FLEET.dbo.TPL_PROV t
                   JOIN DENTASYS_FLEET.dbo.NAME_POOL np ON np.IDX = (r.SEQ * 5 + t.N) % 24
                  WHERE r.PRAC_ID = @p;';
    EXEC sp_executesql @sql, N'@p CHAR(6)', @p = @prac;

    SET @sql = N'INSERT INTO ' + @db + @q + N'OPER (OPER_CD, OPER_NM, ACTIVE_FLG)
                 SELECT OPER_CD, OPER_NM, ''Y'' FROM DENTASYS_FLEET.dbo.TPL_OPER;';
    EXEC sp_executesql @sql;

    SET @sql = N'INSERT INTO ' + @db + @q + N'INS_PLAN (INS_PLAN_ID, CARRIER_NM, GROUP_NBR, ANN_MAX_AMT, DEDUCT_AMT, COV_PCT_PREV, COV_PCT_BASIC, COV_PCT_MAJOR)
                 SELECT CAST(@p AS INT) * 10 + t.N, t.CARRIER, t.GRP, t.ANN_MAX, t.DEDUCT, t.PREV, t.BASIC, t.MAJOR
                   FROM DENTASYS_FLEET.dbo.TPL_INS t;';
    EXEC sp_executesql @sql, N'@p CHAR(6)', @p = @prac;

    -- LANDMINE #3: one procedure code is stored lowercase. Under the server's
    -- CI collation it joins perfectly and has done for 29 years.
    SET @sql = N'INSERT INTO ' + @db + @q + N'PROC_CODE (PROC_CD, PROC_DESC, DEFAULT_FEE'
             + CASE WHEN @vord >= 70104 THEN N', CATEGORY' ELSE N'' END + N', ACTIVE_FLG)
                 SELECT CASE WHEN t.PROC_CD = ''D1110'' AND @s % 2 = 0 THEN LOWER(t.PROC_CD) ELSE t.PROC_CD END,
                        t.PROC_DESC, t.FEE'
             + CASE WHEN @vord >= 70104 THEN N', t.CATEGORY' ELSE N'' END + N', ''Y''
                   FROM DENTASYS_FLEET.dbo.TPL_PROC t;';
    EXEC sp_executesql @sql, N'@s SMALLINT', @s = @seq;

    /*-- PAT_MSTR -------------------------------------------------------------
      CUSTOM_1 deliberately means something different at every practice. That is
      not flavour text: it is why a fleet migration cannot map these columns
      centrally, and why they end up in a jsonb bag with the practice's own key.
      DEL_FLG value depends on which client version last wrote -- LANDMINE #10.
      CHART_NBR collides for one patient on every fourth practice, because the
      schema comment says it is not unique and the seed should prove it.       */
    SET @sql = N'INSERT INTO ' + @db + @q + N'PAT_MSTR
                 (PAT_ID, CHART_NBR, LAST_NM, FIRST_NM, MID_INIT, PAT_DOB, SEX_CD, SSN,
                  ADDR_1, CITY, ST_CD, ZIP_CD, HOME_PHONE, PRIM_PROV, INS_PLAN_ID,
                  BAL_AMT, LAST_VISIT, DEL_FLG, CUSTOM_1, CUSTOM_2, CUSTOM_3'
             + CASE WHEN @vord >= 70104 THEN N', CUSTOM_4, CUSTOM_5' ELSE N'' END
             + CASE WHEN @drift = 'Y'   THEN N', CUST_XREF'          ELSE N'' END + N')
                 SELECT CAST(r.PRAC_ID AS INT) * 100 + t.N,
                        ''C'' + RIGHT(''0000'' + CAST(CASE WHEN t.N = 6 AND r.SEQ % 4 = 0 THEN 1 ELSE t.N END AS VARCHAR(4)), 4),
                        np.SURNAME,
                        CASE WHEN t.NO_FIRST_NM = ''Y'' THEN NULL ELSE np.GIVEN END,
                        LEFT(np2.GIVEN, 1), t.DOB, t.SEX,
                        ''9'' + RIGHT(''00000000'' + CAST(r.SEQ * 1000 + t.N AS VARCHAR(8)), 8),
                        CAST(200 + t.N AS VARCHAR(4)) + '' '' + np2.SURNAME + '' Avenue'',
                        r.CITY, r.ST_CD, r.ZIP_CD,
                        RIGHT(''000'' + CAST(200 + r.SEQ AS VARCHAR(3)), 3) + ''555'' + ''01'' + RIGHT(''0'' + CAST(t.N AS VARCHAR(2)), 2),
                        t.PRIM_PROV, CAST(r.PRAC_ID AS INT) * 10 + t.PLAN_N,
                        t.BAL, t.LAST_VISIT,
                        CASE WHEN t.DEL_KIND = ''DELETED'' THEN ''Y''
                             WHEN @v = 60402 THEN ''''
                             WHEN @v = 70009 THEN NULL
                             ELSE ''N'' END,
                        CASE r.SEQ % 3 WHEN 0 THEN ''REF:'' + np2.SURNAME
                                       WHEN 1 THEN ''CONTACT=TEXT''
                                       ELSE CAST(20240100 + t.N AS VARCHAR(8)) END,
                        CASE WHEN t.N % 2 = 0 THEN ''PPO'' ELSE NULL END,
                        t.NOTE'
             + CASE WHEN @vord >= 70104 THEN N', NULL, CASE WHEN t.N = 1 THEN ''LEGACY-CONV-2021'' ELSE NULL END' ELSE N'' END
             + CASE WHEN @drift = 'Y'   THEN N', ''X'' + CAST(CAST(r.PRAC_ID AS INT) * 100 + t.N AS VARCHAR(10))' ELSE N'' END + N'
                   FROM DENTASYS_FLEET.dbo.FLEET_ROSTER r
                  CROSS JOIN DENTASYS_FLEET.dbo.TPL_PAT t
                   JOIN DENTASYS_FLEET.dbo.NAME_POOL np  ON np.IDX  = (r.SEQ * 7 + t.N) % 24
                   JOIN DENTASYS_FLEET.dbo.NAME_POOL np2 ON np2.IDX = (r.SEQ * 11 + t.N * 3) % 24
                  WHERE r.PRAC_ID = @p;';
    EXEC sp_executesql @sql, N'@p CHAR(6), @v INT', @p = @prac, @v = @vord;

    /*-- APPT -----------------------------------------------------------------
      LEN_UNITS = MINUTES / GRID_MIN, integer division. LANDMINE #6 and the
      45-minute truncation both fall out of this one expression.              */
    SET @sql = N'INSERT INTO ' + @db + @q + N'APPT
                 (APPT_ID, PAT_ID, PROV_CD, OPER_CD, APPT_DT, APPT_TM, LEN_UNITS,
                  APPT_STAT, PROC_CD, NOTE_TXT, CREATE_DTM, DEL_FLG'
             + CASE WHEN @vord >= 70009 THEN N', CREATE_USER' ELSE N'' END
             + CASE WHEN @vord >= 70300 THEN N', APPT_TZ_CD'  ELSE N'' END
             + CASE WHEN @drift = 'Y'   THEN N', SMS_OPT_IN'  ELSE N'' END + N')
                 SELECT CAST(r.PRAC_ID AS INT) * 1000 + t.N,
                        CAST(r.PRAC_ID AS INT) * 100 + t.PAT_N,
                        t.PROV, t.OPER, t.APPT_DT, t.APPT_TM,
                        t.MINUTES / r.GRID_MIN,
                        t.STAT, t.PROC_CD, t.NOTE,
                        DATEADD(hour, 9, DATEADD(day, -14, CONVERT(DATETIME, t.APPT_DT, 112))),
                        CASE WHEN t.DEL_KIND = ''DELETED'' THEN ''Y''
                             WHEN @v = 60402 THEN ''''
                             WHEN @v = 70009 THEN NULL
                             ELSE ''N'' END'
             + CASE WHEN @vord >= 70009 THEN N', ''FRONTDESK'' + CAST(r.SEQ % 3 + 1 AS VARCHAR(2))' ELSE N'' END
             -- 07.03.00 shipped a timezone column and never finished populating
             -- it. It holds a 3-letter ABBREVIATION, which is not a zone: "CST"
             -- is US Central and also China Standard, and abbreviations do not
             -- survive DST. A migration that trusts this column is worse off
             -- than one that ignores it. NULL on most rows, because the feature
             -- was half-shipped. That is the realistic case, not the clean one.
             + CASE WHEN @vord >= 70300 THEN N', CASE WHEN t.N % 3 = 0 THEN
                           CASE WHEN r.IANA_TZ LIKE ''%New_York'' THEN ''EST''
                                WHEN r.IANA_TZ LIKE ''%Chicago''  THEN ''CST''
                                WHEN r.IANA_TZ LIKE ''%Denver''   THEN ''MST''
                                ELSE ''PST'' END
                      ELSE NULL END' ELSE N'' END
             + CASE WHEN @drift = 'Y'   THEN N', CASE WHEN t.PAT_N % 2 = 0 THEN ''Y'' ELSE ''N'' END' ELSE N'' END + N'
                   FROM DENTASYS_FLEET.dbo.FLEET_ROSTER r
                  CROSS JOIN DENTASYS_FLEET.dbo.TPL_APPT t
                  WHERE r.PRAC_ID = @p;';
    EXEC sp_executesql @sql, N'@p CHAR(6), @v INT', @p = @prac, @v = @vord;

    /*-- LEDGER ---------------------------------------------------------------*/
    SET @sql = N'INSERT INTO ' + @db + @q + N'LEDGER
                 (TRAN_ID, PAT_ID, TRAN_DT, TRAN_TYPE, PROC_CD, PROV_CD, AMT'
             + CASE WHEN @vord >= 70009 THEN N', INS_EST_AMT' ELSE N'' END + N',
                  PAID_AMT, APPLIED_TO, POST_DTM, DEL_FLG)
                 SELECT CAST(r.PRAC_ID AS INT) * 1000 + t.N,
                        CAST(r.PRAC_ID AS INT) * 100 + t.PAT_N,
                        t.TRAN_DT, t.TRAN_TYPE, t.PROC_CD, t.PROV, t.AMT'
             + CASE WHEN @vord >= 70009 THEN N', t.INS_EST' ELSE N'' END + N',
                        t.PAID,
                        CASE WHEN t.APPLIED_TO_N IS NULL THEN NULL
                             ELSE CAST(r.PRAC_ID AS INT) * 1000 + t.APPLIED_TO_N END,
                        DATEADD(hour, 17, CONVERT(DATETIME, t.TRAN_DT, 112)),
                        CASE WHEN t.DEL_KIND = ''DELETED'' THEN ''Y''
                             WHEN @v = 60402 THEN ''''
                             WHEN @v = 70009 THEN NULL
                             ELSE ''N'' END
                   FROM DENTASYS_FLEET.dbo.FLEET_ROSTER r
                  CROSS JOIN DENTASYS_FLEET.dbo.TPL_LEDGER t
                  WHERE r.PRAC_ID = @p;';
    EXEC sp_executesql @sql, N'@p CHAR(6), @v INT', @p = @prac, @v = @vord;

    /*-- APPT_REMINDER ---------------------------------------------------------
      Only exists on 07.01.04+. SCHEDULED_AT carries a real UTC offset, computed
      from the practice's actual zone -- which is exactly why it is useful: it is
      the one place in DENTASYS where a practice's true offset is recorded, and
      it got there because a 2021 developer picked a sensible column type without
      thinking about it as a migration asset.
    --------------------------------------------------------------------------*/
    IF @vord >= 70104
    BEGIN
        SET @sql = N'INSERT INTO ' + @db + @q + N'APPT_REMINDER (APPT_ID, CHANNEL, SCHEDULED_AT, IS_OPTED_OUT)
                     SELECT CAST(r.PRAC_ID AS INT) * 1000 + t.N,
                            CASE WHEN t.N % 2 = 0 THEN N''SMS'' ELSE N''EMAIL'' END,
                            TODATETIMEOFFSET(
                                DATEADD(day, -1, CONVERT(DATETIME2(0), t.APPT_DT, 112)),
                                @offsetMin),
                            0
                       FROM DENTASYS_FLEET.dbo.FLEET_ROSTER r
                      CROSS JOIN DENTASYS_FLEET.dbo.TPL_APPT t
                      WHERE r.PRAC_ID = @p AND t.DEL_KIND = ''LIVE'' AND t.N <= 4;';
        EXEC sp_executesql @sql, N'@p CHAR(6), @offsetMin INT', @p = @prac, @offsetMin = @offmin;
    END

    /*-- RECALL ---------------------------------------------------------------*/
    SET @sql = N'INSERT INTO ' + @db + @q + N'RECALL
                 (RECALL_ID, PAT_ID, RECALL_TYPE, DUE_YM, LAST_SENT_DT'
             + CASE WHEN @vord >= 70211 THEN N', DEL_FLG' ELSE N'' END + N')
                 SELECT CAST(r.PRAC_ID AS INT) * 100 + t.N,
                        CAST(r.PRAC_ID AS INT) * 100 + t.PAT_N,
                        t.RECALL_TYPE, t.DUE_YM, t.LAST_SENT'
             + CASE WHEN @vord >= 70211 THEN N', CASE WHEN t.DEL_KIND = ''DELETED'' THEN ''Y'' ELSE ''N'' END' ELSE N'' END + N'
                   FROM DENTASYS_FLEET.dbo.FLEET_ROSTER r
                  CROSS JOIN DENTASYS_FLEET.dbo.TPL_RECALL t
                  WHERE r.PRAC_ID = @p;';
    EXEC sp_executesql @sql, N'@p CHAR(6)', @p = @prac;

    FETCH NEXT FROM load INTO @seq, @prac, @vord, @grid, @drift, @tz;
END

CLOSE load;
DEALLOCATE load;
GO

/*==============================================================================
  SUMMARY

  Printed as histograms rather than a row count, because that is the shape the
  parity harness has to report in too. "Does the new system agree with the old
  one" has no single answer across a fleet -- it has a distribution, and the
  interesting cell is always the smallest one.
==============================================================================*/

PRINT '';
PRINT '=== fleet spawned ===';
GO

SELECT 'by schema version' AS HISTOGRAM, VER_NBR, COUNT(*) AS PRACTICES,
       CAST(ROUND(100.0 * COUNT(*) / (SELECT COUNT(*) FROM FLEET_ROSTER), 1) AS DECIMAL(5,1)) AS PCT
  FROM FLEET_ROSTER GROUP BY VER_NBR ORDER BY VER_NBR;

SELECT 'by timezone' AS HISTOGRAM, IANA_TZ, COUNT(*) AS PRACTICES,
       MIN(OBSERVES_DST) AS OBSERVES_DST
  FROM FLEET_ROSTER GROUP BY IANA_TZ ORDER BY IANA_TZ;

/*-- The money query. Every one of these states is a place where the only
    geographic column in the product gives you the wrong answer for some of
    your practices, and nothing in the database tells you which ones.        */
SELECT 'states spanning >1 timezone' AS HISTOGRAM, ST_CD,
       COUNT(DISTINCT IANA_TZ) AS ZONES,
       COUNT(*) AS PRACTICES
  FROM FLEET_ROSTER
 GROUP BY ST_CD
HAVING COUNT(DISTINCT IANA_TZ) > 1
 ORDER BY ST_CD;

SELECT 'structural variants' AS HISTOGRAM,
       SUM(CASE WHEN HAS_TRIGGER = 'Y' THEN 1 ELSE 0 END) AS WITH_TR_APPT_AUDIT,
       SUM(CASE WHEN HAS_DRIFT   = 'Y' THEN 1 ELSE 0 END) AS WITH_CUSTOM_DRIFT,
       SUM(CASE WHEN GRID_MIN    = 15  THEN 1 ELSE 0 END) AS ON_15_MIN_GRID,
       SUM(CASE WHEN OBSERVES_DST= 'N' THEN 1 ELSE 0 END) AS NO_DST,
       COUNT(*) AS TOTAL_PRACTICES
  FROM FLEET_ROSTER;
GO

/*-- Proof that the column sets genuinely differ, rather than being one schema
    with NULLs in the new columns. This reads the real catalog of every spawned
    database. It is the query to point at when someone says "just migrate the
    current schema and backfill."                                            */
DROP TABLE IF EXISTS #COLS;
CREATE TABLE #COLS (PRAC_ID CHAR(6), TABLE_NAME SYSNAME, NCOLS INT);

DECLARE @p CHAR(6), @sql NVARCHAR(MAX);
DECLARE cat CURSOR LOCAL FAST_FORWARD FOR SELECT PRAC_ID FROM FLEET_ROSTER ORDER BY SEQ;
OPEN cat;
FETCH NEXT FROM cat INTO @p;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'SELECT @pp, TABLE_NAME, COUNT(*) FROM '
             + QUOTENAME(N'DENTASYS_' + @p) + N'.INFORMATION_SCHEMA.COLUMNS
                GROUP BY TABLE_NAME;';
    INSERT INTO #COLS (PRAC_ID, TABLE_NAME, NCOLS)
    EXEC sp_executesql @sql, N'@pp CHAR(6)', @pp = @p;
    FETCH NEXT FROM cat INTO @p;
END
CLOSE cat;
DEALLOCATE cat;

SELECT 'column count by version' AS HISTOGRAM, r.VER_NBR, c.TABLE_NAME,
       COUNT(*) AS PRACTICES, MIN(c.NCOLS) AS MIN_COLS, MAX(c.NCOLS) AS MAX_COLS
  FROM FLEET_ROSTER r
  JOIN #COLS c ON c.PRAC_ID = r.PRAC_ID
  -- MIN_COLS <> MAX_COLS inside a single version is customization drift: two
  -- practices on the identical vendor release with physically different
  -- tables. That is the cell that breaks a fleet migration.
 WHERE c.TABLE_NAME IN ('PAT_MSTR', 'APPT', 'LEDGER', 'RECALL', 'PROC_CODE')
 GROUP BY r.VER_NBR, c.TABLE_NAME
 ORDER BY c.TABLE_NAME, r.VER_NBR;

/*-- The same fact stated as the thing that actually bites: which practices are
    missing a column the current release takes for granted.                  */
SELECT 'practices missing APPT.CREATE_USER' AS GAP, COUNT(*) AS PRACTICES
  FROM FLEET_ROSTER WHERE VER_ORD < 70009
UNION ALL
SELECT 'practices missing PAT_MSTR.CUSTOM_4/5', COUNT(*) FROM FLEET_ROSTER WHERE VER_ORD < 70104
UNION ALL
SELECT 'practices missing RECALL.DEL_FLG',      COUNT(*) FROM FLEET_ROSTER WHERE VER_ORD < 70211
UNION ALL
SELECT 'practices with the half-shipped APPT_TZ_CD', COUNT(*) FROM FLEET_ROSTER WHERE VER_ORD >= 70300;

DROP TABLE #COLS;
GO
