/*==============================================================================
  DENTASYS -- synthetic seed data

  All PHI here is FABRICATED. That is not a disclaimer, it is the design
  constraint: under HIPAA you cannot pull a practice's production database onto
  a laptop to debug a migration, so realistic synthetic fixtures are the only
  lawful way to do this work. See LANDMINES #11.

  Every row below is chosen to exercise a specific landmine. Nothing here is
  filler -- if a row looks arbitrary, check docs/LANDMINES.md.

  This seeds ONE practice. The fleet spawn (N databases at differing
  SCHEMA_VER values) is 03_seed_fleet.sql -- not written yet.
==============================================================================*/

USE DENTASYS;
GO

DELETE FROM RECALL;    DELETE FROM LEDGER;  DELETE FROM APPT;
DELETE FROM PAT_MSTR;  DELETE FROM PROC_CODE;
DELETE FROM OPER;      DELETE FROM PROV;    DELETE FROM INS_PLAN;
DELETE FROM PRACTICE;  DELETE FROM SCHEMA_VER;
GO

/*------------------------------------------------------------------------------
  Schema version. This practice is three years behind. Roughly a third of the
  fleet looks like this, and it is the third that still has TR_APPT_AUDIT.
------------------------------------------------------------------------------*/
INSERT INTO SCHEMA_VER (VER_NBR, APPLIED_DT, APPLIED_BY)
VALUES ('07.02.11', '20230419', 'MERIDIAN_RELEASE_ENG');
GO

/*------------------------------------------------------------------------------
  PRACTICE -- Phoenix, Arizona.

  Chosen deliberately as the worst case for LANDMINE #1. Arizona does not
  observe DST, so:
    - a fixed UTC offset per practice is WRONG half the year
    - deriving the zone from ST_CD='AZ' is ALSO wrong, because the Navajo
      Nation within Arizona DOES observe DST
  ST_CD is the only geographic column in the entire schema. It is not
  sufficient to determine a timezone, and no amount of schema conversion
  tooling will tell you that.
------------------------------------------------------------------------------*/
INSERT INTO PRACTICE (PRAC_ID, PRAC_NM, ADDR_1, CITY, ST_CD, ZIP_CD, PHONE, TAX_ID)
VALUES ('000417', 'Elm Street Family Dental', '1847 N Elm St', 'Phoenix', 'AZ', '85004', '6025550118', '860042117');
GO

INSERT INTO PROV (PROV_CD, PROV_NM, PROV_TYPE, NPI, ACTIVE_FLG) VALUES
    ('DDS1', 'Rivera, A. DDS',    'D', '1548227390', 'Y'),
    ('DDS2', 'Okafor, N. DMD',    'D', '1730049518', 'Y'),
    ('HYG1', 'Delgado, M. RDH',   'H', '1902773641', 'Y'),
    ('HYG2', 'Brennan, K. RDH',   'H', '1665038822', 'N');   -- left in 2024, still on old appts
GO

INSERT INTO OPER (OPER_CD, OPER_NM, ACTIVE_FLG) VALUES
    ('OP1 ', 'OP 1',        'Y'),
    ('OP2 ', 'OP 2',        'Y'),
    ('HYG1', 'HYG 1',       'Y'),
    ('OP3 ', 'Dr. R rm',    'Y');   -- named by a human, not by a system
GO

/*------------------------------------------------------------------------------
  PROC_CODE -- real ADA CDT codes.

  LANDMINE #3: note the casing. 'd1110' was typed lowercase by front desk staff
  in 2019 and 'D111O' has a letter O instead of a zero. Under this server's
  CI collation the first joins fine and nobody has ever noticed. Under
  PostgreSQL's case-sensitive default it silently stops joining.
------------------------------------------------------------------------------*/
INSERT INTO PROC_CODE (PROC_CD, PROC_DESC, DEFAULT_FEE, CATEGORY, ACTIVE_FLG) VALUES
    ('D0120', 'Periodic oral evaluation',              65.00,  'DIAG', 'Y'),
    ('D0150', 'Comprehensive oral evaluation',        125.00,  'DIAG', 'Y'),
    ('D0274', 'Bitewings - four radiographic images',  89.99,  'DIAG', 'Y'),
    ('D1110', 'Prophylaxis - adult',                  129.95,  'PREV', 'Y'),
    ('D1206', 'Topical fluoride varnish',              48.00,  'PREV', 'Y'),
    ('D2391', 'Resin composite - one surface, post.', 245.50,  'REST', 'Y'),
    ('D2740', 'Crown - porcelain/ceramic',           1487.33,  'REST', 'Y'),
    ('D4341', 'Perio scaling/root planing per quad',  312.10,  'PERI', 'Y'),
    ('D7140', 'Extraction, erupted tooth',            198.75,  'SURG', 'Y');
GO

INSERT INTO INS_PLAN (INS_PLAN_ID, CARRIER_NM, GROUP_NBR, ANN_MAX_AMT, DEDUCT_AMT,
                      COV_PCT_PREV, COV_PCT_BASIC, COV_PCT_MAJOR) VALUES
    (1, 'Delta Dental PPO',      'GRP-88421', 1500.00, 50.00, 100, 80, 50),
    (2, 'MetLife Preferred',     'GRP-10277', 2000.00, 75.00, 100, 80, 50),
    (3, 'Cigna DPPO Advantage',  'GRP-55190', 1000.00, 50.00,  80, 70, 50);
GO

/*------------------------------------------------------------------------------
  PAT_MSTR

  LANDMINE #9  -- PAT_ID 1006 has a NULL FIRST_NM. The schedule concatenates
                  with '+', so the entire patient name renders NULL and the
                  appointment paints as a blank block.
  LANDMINE #10 -- DEL_FLG uses all four of its truth values: 'Y', 'N', NULL
                  and ''. 1009 is the '' case: hidden from the production
                  report but VISIBLE on the schedule.
  Duplicates   -- 1002 and 1011 are the same human with two chart numbers, a
                  different DOB typo, and a split balance. Universal in dental
                  PM, and it means patient identity resolution is part of the
                  migration whether you planned for it or not.
------------------------------------------------------------------------------*/
INSERT INTO PAT_MSTR (PAT_ID, CHART_NBR, LAST_NM, FIRST_NM, MID_INIT, PAT_DOB, SEX_CD,
                      SSN, CITY, ST_CD, ZIP_CD, HOME_PHONE, PRIM_PROV, INS_PLAN_ID,
                      BAL_AMT, LAST_VISIT, DEL_FLG, CUSTOM_1) VALUES
    (1001, 'A0001     ', 'Hollis',    'Marguerite', 'T', '19410722', 'F', '900110001', 'Phoenix', 'AZ', '85004', '6025550143', 'DDS1', 1,    0.00, '20260210', 'N',  'WHEELCHAIR'),
    (1002, 'A0002     ', 'Vandermeer','Theodore',   'J', '19680114', 'M', '900110002', 'Phoenix', 'AZ', '85012', '6025550187', 'DDS1', 1,  418.28, '20260302', 'N',  NULL),
    (1003, 'A0003     ', 'Okonkwo',   'Adaeze',     NULL,'19890930', 'F', '900110003', 'Tempe',   'AZ', '85281', '4805550166', 'DDS2', 2,   62.10, '20260115', 'N',  'NO EMAIL'),
    (1004, 'A0004     ', 'Bui',       'Lan',        'H', '19750408', 'F', '900110004', 'Mesa',    'AZ', '85201', '4805550129', 'HYG1', 3,    0.00, '20251118', 'N',  NULL),
    (1005, 'A0005     ', 'Threlkeld', 'Ambrose',    'W', '19330216', 'M', '900110005', 'Phoenix', 'AZ', '85004', '6025550175', 'DDS1', NULL, 1487.33,'20260225','N', 'MEDICARE?'),
    (1006, 'A0006     ', 'Petrosyan', NULL,         NULL,'20180605', 'M', '900110006', 'Phoenix', 'AZ', '85008', '6025550190', 'HYG1', 2,    0.00, '20260120', 'N',  'PEDO'),
    (1007, 'A0007     ', 'Castellanos','Rosalinda', 'M', '19520911', 'F', '900110007', 'Glendale','AZ', '85301', '6235550111', 'DDS2', 1,  245.50, '20260218', NULL, NULL),
    (1008, 'A0008     ', 'Fairweather','Jonquil',   NULL,'19961203', 'F', '900110008', 'Phoenix', 'AZ', '85016', '6025550152', 'HYG1', 3,    0.00, '20260105', 'Y',  'MOVED TX'),
    (1009, 'A0009     ', 'Duquesne',  'Alaric',     'P', '19840719', 'M', '900110009', 'Scottsdale','AZ','85251','4805550138','DDS1', 2,   89.99, '20251222', '',   NULL),
    (1010, 'A0010     ', 'Winterbourne','Cordelia', 'A', '19630527', 'F', '900110010', 'Phoenix', 'AZ', '85013', '6025550164', 'DDS2', 1,  312.10, '20260304', 'N',  NULL),
    (1011, 'A0247     ', 'Vandermeer','Ted',        NULL,'19680141', 'M', '900110002', 'Phoenix', 'AZ', '85012', '6025550187', 'DDS1', 1,   75.00, '20240916', 'N',  'DUP?');
    -- 1011: same SSN as 1002, DOB day transposed (0114 -> 0141, not a valid date),
    --       balance split across both records. Nobody has merged them.
GO

/*------------------------------------------------------------------------------
  APPT -- THE POINT OF THIS FILE.

  2026 US DST transitions (both verified Sundays):
      spring forward  2026-03-08  02:00 local -> 03:00 local   (02:00-02:59 does not exist)
      fall back       2026-11-01  02:00 local -> 01:00 local   (01:00-01:59 happens twice)

  In Phoenix NEITHER transition occurs -- which is exactly why this practice is
  the interesting one. The same CHAR(8)+CHAR(4) values mean different absolute
  instants depending on a fact stored nowhere in this database.
------------------------------------------------------------------------------*/

-- Ordinary business-hours appointments. These are the control group: they must
-- round-trip through the migration completely unchanged.
INSERT INTO APPT (APPT_ID, PAT_ID, PROV_CD, OPER_CD, APPT_DT, APPT_TM, LEN_UNITS,
                  APPT_STAT, PROC_CD, NOTE_TXT, CREATE_DTM, CREATE_USER, DEL_FLG) VALUES
    (50001, 1001, 'HYG1', 'HYG1', '20260302', '0800', 6, 'C', 'D1110', 'reg cleaning',        '2026-02-01T09:14:00', 'FRONTDESK1', 'N'),
    (50002, 1002, 'DDS1', 'OP1 ', '20260302', '0900', 9, 'C', 'D2740', 'crown seat #14',      '2026-02-03T11:02:00', 'FRONTDESK1', 'N'),
    (50003, 1003, 'DDS2', 'OP2 ', '20260302', '1030', 3, 'C', 'D0120', NULL,                  '2026-02-10T14:33:00', 'FRONTDESK2', 'N'),
    (50004, 1004, 'HYG1', 'HYG1', '20260302', '1300', 6, 'B', 'd1110', 'lowercase code -- #3','2026-02-11T08:47:00', 'FRONTDESK2', 'N'),
    (50005, 1010, 'DDS2', 'OP2 ', '20260304', '1400', 9, 'C', 'D4341', 'SRP UR quad',         '2026-02-14T10:21:00', 'FRONTDESK1', 'N'),

-- LANDMINE #1a -- SPRING FORWARD. 02:30 on 2026-03-08 does not exist in any
-- DST-observing zone. A naive timestamp -> timestamptz conversion either throws
-- or silently shifts by an hour. In Phoenix it is a perfectly valid instant.
-- Realistically this is a data-entry error (transposed from 14:30), which is
-- precisely how these rows occur in production.
    (50006, 1005, 'DDS1', 'OP1 ', '20260308', '0230', 6, 'X', 'D0150', 'ENTRY ERR? was 1430', '2026-03-02T16:55:00', 'FRONTDESK1', 'N'),

-- LANDMINE #1b -- FALL BACK. 01:30 on 2026-11-01 happens TWICE. The row cannot
-- tell you which. This is not recoverable from the data; the honest cutover
-- answer is a documented deterministic tie-break plus a human exception report.
    (50007, 1007, 'DDS1', 'OP1 ', '20261101', '0130', 6, 'X', 'D0120', 'emerg - after hrs',   '2026-10-28T19:40:00', 'ONCALL',     'N'),

-- LANDMINE #1c -- the REAL production vector. The nightly SQL Agent job runs at
-- 02:00 local. On spring-forward that wall-clock time does not occur, so the job
-- either does not fire or fires twice on fall-back, double-posting claims. This
-- appointment sits on the boundary the batch job uses to decide "yesterday".
    (50008, 1010, 'HYG1', 'HYG1', '20260308', '2350', 3, 'C', 'D1206', 'late add',            '2026-03-07T17:12:00', 'FRONTDESK2', 'N'),

-- LANDMINE #6 -- 15-minute-grid practice storing 15-minute units in a column
-- documented as 10-minute units. LEN_UNITS=4 is 60 minutes here, 40 minutes by
-- the schema's own documentation. The disambiguating .INI is on a workstation.
    (50009, 1002, 'DDS1', 'OP3 ', '20260310', '0915', 4, 'S', 'D2391', '15-min grid -- #6',   '2026-03-01T13:05:00', 'FRONTDESK1', 'N'),

-- LANDMINE #8 -- string date math. 0930 + (6*10) = 990, which is not a time.
    (50010, 1003, 'HYG1', 'HYG1', '20260310', '0930', 6, 'S', 'D1110', 'rollover -- #8',      '2026-03-01T13:09:00', 'FRONTDESK1', 'N'),

-- LANDMINE #9 -- NULL FIRST_NM. Name concatenates to NULL, paints blank.
    (50011, 1006, 'HYG1', 'HYG1', '20260311', '1000', 3, 'S', 'D1206', 'pedo fluoride -- #9', '2026-03-02T09:30:00', 'FRONTDESK2', 'N'),

-- LANDMINE #10 -- DEL_FLG='' . Hidden from the production report, VISIBLE on the
-- schedule. This single row is why those two numbers have disagreed since 2011.
    (50012, 1009, 'DDS1', 'OP1 ', '20260312', '1100', 6, 'S', 'D0274', 'empty del flg -- #10','2026-03-02T15:44:00', 'FRONTDESK1', ''),
    (50013, 1008, 'DDS2', 'OP2 ', '20260312', '1330', 6, 'S', 'D0120', 'properly deleted',    '2026-03-02T15:51:00', 'FRONTDESK1', 'Y'),

-- Orphans: PROV_CD points at a hygienist who left in 2024, PAT_ID points at a
-- patient purged by a cleanup script in 2019. No FKs, so both persisted.
    (50014, 1001, 'HYG2', 'HYG1', '20251104', '0800', 6, 'C', 'D1110', 'inactive prov',       '2025-10-01T08:00:00', 'FRONTDESK2', 'N'),
    (50015, 9999, 'DDS1', 'OP1 ', '20251106', '0900', 6, 'C', 'D0120', 'orphaned patient',    '2025-10-02T08:00:00', 'FRONTDESK2', 'N'),

-- LANDMINE #3 -- letter O instead of zero. Joins under NO collation. Already
-- broken today; the migration will get blamed for it.
    (50016, 1004, 'DDS2', 'OP2 ', '20260313', '0930', 6, 'S', 'D111O', 'letter O -- #3',      '2026-03-03T10:15:00', 'FRONTDESK2', 'N');
GO

/*------------------------------------------------------------------------------
  LEDGER

  LANDMINE #2 -- FLOAT money. Patient 1002's charges are chosen so the FLOAT sum
  does NOT equal the exact decimal sum. Under numeric(12,2) the migrated total
  will be correct and will therefore DISAGREE with legacy.

  The parity harness must classify that as expected divergence with a known
  direction, not as a regression. Building that distinction is most of the
  harness's value -- an exact-match harness is useless on a real migration.
------------------------------------------------------------------------------*/
INSERT INTO LEDGER (TRAN_ID, PAT_ID, TRAN_DT, TRAN_TYPE, PROC_CD, PROV_CD,
                    AMT, INS_EST_AMT, PAID_AMT, APPLIED_TO, POST_DTM, DEL_FLG) VALUES
    (900001, 1002, '20260302', 'C', 'D2740', 'DDS1', 1487.33, 743.67,    0.00, NULL,   '2026-03-02T10:31:00', 'N'),
    (900002, 1002, '20260302', 'C', 'D0274', 'DDS1',   89.99,  71.99,    0.00, NULL,   '2026-03-02T10:31:00', 'N'),
    (900003, 1002, '20260302', 'C', 'D0120', 'DDS1',   65.00,  65.00,    0.00, NULL,   '2026-03-02T10:31:00', 'N'),
    (900004, 1002, '20260315', 'I', NULL,    'DDS1', -880.66,   0.00,  880.66, 900001, '2026-03-15T02:14:00', 'N'),
    (900005, 1002, '20260320', 'P', NULL,    NULL,   -343.38,   0.00,  343.38, 900001, '2026-03-20T16:05:00', 'N'),
    -- 1487.33 + 89.99 + 65.00 - 880.66 - 343.38 = 418.28 exactly in decimal.
    -- In FLOAT it is not. PAT_MSTR.BAL_AMT for 1002 says 418.28. It will drift.

    (900006, 1005, '20260225', 'C', 'D2740', 'DDS1', 1487.33,   0.00,    0.00, NULL,   '2026-02-25T11:00:00', 'N'),
    (900007, 1010, '20260304', 'C', 'D4341', 'DDS2',  312.10, 156.05,    0.00, NULL,   '2026-03-04T15:22:00', 'N'),
    (900008, 1007, '20260218', 'C', 'D2391', 'DDS2',  245.50, 171.85,    0.00, NULL,   '2026-02-18T09:48:00', 'N'),
    (900009, 1003, '20260115', 'C', 'D0120', 'DDS2',   65.00,  52.00,    0.00, NULL,   '2026-01-15T10:03:00', 'N'),
    (900010, 1003, '20260115', 'P', NULL,    NULL,     -2.90,   0.00,    2.90, 900009, '2026-01-15T10:04:00', 'N'),
    -- APPLIED_TO points at TRAN_ID 900099, which does not exist. No FK caught it.
    (900011, 1009, '20251222', 'C', 'D0274', 'DDS1',   89.99,  71.99,    0.00, 900099, '2025-12-22T14:10:00', 'N');
GO

/*------------------------------------------------------------------------------
  RECALL

  LANDMINE #5 -- DUE_YM is CHAR(4) YYMM with a 1999 pivot window (<50 -> 20xx,
  else 19xx). '5003' is meant to be March 2050. It resolves to March 1950.
  Not hypothetical: pediatric patient 1006, born 2018, on a perio recall
  interval, is already booked past the pivot.
------------------------------------------------------------------------------*/
INSERT INTO RECALL (RECALL_ID, PAT_ID, RECALL_TYPE, DUE_YM, LAST_SENT_DT, DEL_FLG) VALUES
    (7001, 1001, 'PROP', '2609', '20260210', 'N'),
    (7002, 1002, 'PROP', '2609', '20260302', 'N'),
    (7003, 1003, 'PERI', '2607', '20260115', 'N'),
    (7004, 1004, 'PROP', '2605', '20251118', 'N'),
    (7005, 1006, 'PROP', '5003', NULL,       'N'),   -- #5: reads as 1950-03
    (7006, 1010, 'PERI', '2606', '20260304', ''),    -- #10 again, in another table
    (7007, 1005, 'XRAY', '2612', '20260225', NULL);
GO

PRINT 'DENTASYS seed complete.';
PRINT 'Practice 000417 / Phoenix AZ / schema 07.02.11 / 16 appts / 11 ledger rows.';
GO
