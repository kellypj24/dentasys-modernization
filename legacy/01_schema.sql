/*==============================================================================
  DENTASYS Practice Management System
  Schema DDL -- core tables originally authored 1997 for SQL Server 6.5.

  The PLATFORM has kept moving: ported 2003 (SQL 2000), 2011 (SQL 2008R2), 2019
  (SQL 2016), 2023 (SQL 2022). These databases run on a currently-supported
  engine, in a maintained data center, with TDE, availability groups, tested
  restores and a DBA who tunes indexes. This is not an abandoned system.

  The DATA MODEL has not moved at all. Every table below carries decisions made
  in 1997, because upgrading an engine never rewrites a column type -- an
  in-place upgrade from 6.5 to 2022 will carry CHAR(8) dates and FLOAT money
  forward without comment, five times in a row.

  That is the whole point. The problems in this schema survived five platform
  modernizations precisely BECAUSE those modernizations were competent and
  low-risk: none of them touched the data model, and touching it was always the
  thing nobody could justify.

  Tables added after about 2015 look completely different -- see APPT_REMINDER
  at the bottom. The schema is stratified by era, not uniformly old.

  DO NOT "CLEAN UP" NAMES. Field names are compiled into 400+ Crystal Reports.
                                                          -- DK, 2004
==============================================================================*/

IF DB_ID('DENTASYS') IS NULL
    CREATE DATABASE DENTASYS COLLATE SQL_Latin1_General_CP1_CI_AS;
GO
USE DENTASYS;
GO

/*------------------------------------------------------------------------------
  SCHEMA_VER
  Every client practice has its own database, hosted here in the DC, and they are
  not all on the same release. Customers schedule their own upgrade windows, and
  an upgrade can mean revalidating a workflow or retraining a front desk, so some
  practices sit on an old version for years. This is the single most important
  table in the modernization: the fleet is NOT on one schema version. It is on a
  distribution of them.
------------------------------------------------------------------------------*/
CREATE TABLE SCHEMA_VER (
    VER_NBR      CHAR(8)      NOT NULL,   -- '07.02.11'
    APPLIED_DT   CHAR(8)      NULL,       -- YYYYMMDD
    APPLIED_BY   VARCHAR(30)  NULL
);
GO

/*------------------------------------------------------------------------------
  PRACTICE
  Exactly ONE row here, because the database IS the practice. Multi-tenancy does
  not exist as a concept anywhere in DENTASYS -- it is one database per client,
  all of them hosted in our DC.

  Note there is no timezone column, and note that the usual excuse does not
  apply: the server has never been in the same building as the chair. The zone
  is absent because no query has ever needed it. Every read is scoped to this
  one practice, and this practice is all in one zone, so the omission has never
  cost anything.
------------------------------------------------------------------------------*/
CREATE TABLE PRACTICE (
    PRAC_ID      CHAR(6)      NOT NULL,
    PRAC_NM      VARCHAR(40)  NULL,
    ADDR_1       VARCHAR(40)  NULL,
    CITY         VARCHAR(30)  NULL,
    ST_CD        CHAR(2)      NULL,       -- the ONLY hint of geography in the system
    ZIP_CD       CHAR(10)     NULL,
    PHONE        CHAR(10)     NULL,
    TAX_ID       CHAR(9)      NULL
);
GO

/*------------------------------------------------------------------------------
  PAT_MSTR -- patient master
------------------------------------------------------------------------------*/
CREATE TABLE PAT_MSTR (
    PAT_ID       INT          NOT NULL,
    CHART_NBR    CHAR(10)     NULL,       -- practice-assigned; NOT unique in practice
    LAST_NM      VARCHAR(30)  NULL,
    FIRST_NM     VARCHAR(20)  NULL,
    MID_INIT     CHAR(1)      NULL,
    PAT_DOB      CHAR(8)      NULL,       -- YYYYMMDD as text. 1997 wanted it sortable.
    SEX_CD       CHAR(1)      NULL,
    SSN          CHAR(9)      NULL,       -- plaintext PHI. 1997 did not ask.
    ADDR_1       VARCHAR(40)  NULL,
    CITY         VARCHAR(30)  NULL,
    ST_CD        CHAR(2)      NULL,
    ZIP_CD       CHAR(10)     NULL,
    HOME_PHONE   CHAR(10)     NULL,
    PRIM_PROV    CHAR(4)      NULL,       -- -> PROV.PROV_CD  (no FK)
    INS_PLAN_ID  INT          NULL,       -- -> INS_PLAN      (no FK)
    BAL_AMT      FLOAT        NULL,       -- FLOAT. For money. See docs/LANDMINES.md
    LAST_VISIT   CHAR(8)      NULL,
    DEL_FLG      CHAR(1)      NULL,       -- 'Y'/'N'/NULL/''  -- all four occur
    -- Added in 2004 for one customer who threatened to leave, then shipped to
    -- everyone because branching the schema was deemed worse. Each practice uses
    -- these for something completely different, and nothing records what.
    CUSTOM_1     VARCHAR(50)  NULL,
    CUSTOM_2     VARCHAR(50)  NULL,
    CUSTOM_3     VARCHAR(50)  NULL,
    CUSTOM_4     VARCHAR(50)  NULL,
    CUSTOM_5     VARCHAR(50)  NULL
);
GO
CREATE CLUSTERED INDEX IX_PAT_MSTR_ID ON PAT_MSTR (PAT_ID);
GO

/*------------------------------------------------------------------------------
  PROV -- providers (dentists, hygienists)
  OPER -- operatories, i.e. the physical chairs. This is the real constrained
          resource the scheduler books against.
------------------------------------------------------------------------------*/
CREATE TABLE PROV (
    PROV_CD      CHAR(4)      NOT NULL,
    PROV_NM      VARCHAR(40)  NULL,
    PROV_TYPE    CHAR(1)      NULL,       -- 'D'entist 'H'ygienist 'S'pecialist
    NPI          CHAR(10)     NULL,
    ACTIVE_FLG   CHAR(1)      NULL
);
GO

CREATE TABLE OPER (
    OPER_CD      CHAR(4)      NOT NULL,
    OPER_NM      VARCHAR(20)  NULL,       -- 'OP 1', 'HYG 2', 'Dr. Kim rm'
    ACTIVE_FLG   CHAR(1)      NULL
);
GO

/*------------------------------------------------------------------------------
  APPT -- the appointment book. The heart of the product.

  *** THIS TABLE IS THE MODERNIZATION. ***

  Wall-clock local time, split across two CHAR columns, with no timezone and no
  UTC offset anywhere.

  This was never right; it has only ever been unfalsifiable. The DB is in our DC
  and the chair is not, so these two clocks have always differed. What saves us
  is that the client sends the workstation's wall-clock reading and nothing here
  ever interprets it -- every query is scoped to one practice, and one practice
  is one zone.

  Consolidate the fleet and that stops being true on the first query that spans
  two practices. Then add DST, in a product that books in 15-minute increments.
  See docs/LANDMINES.md #1.
------------------------------------------------------------------------------*/
CREATE TABLE APPT (
    APPT_ID      INT          NOT NULL,
    PAT_ID       INT          NULL,
    PROV_CD      CHAR(4)      NULL,
    OPER_CD      CHAR(4)      NULL,
    APPT_DT      CHAR(8)      NOT NULL,   -- YYYYMMDD  local wall-clock date
    APPT_TM      CHAR(4)      NOT NULL,   -- HHMM      local wall-clock time
    LEN_UNITS    SMALLINT     NULL,       -- 10-minute units. Except where 15. See #6.
    APPT_STAT    CHAR(1)      NULL,       -- see docs/STATUS_CODES.DOC (lost, 2009)
    PROC_CD      CHAR(5)      NULL,       -- -> PROC_CODE (no FK, and see #3)
    NOTE_TXT     VARCHAR(255) NULL,
    CREATE_DTM   DATETIME     NULL,       -- DATETIME = no offset. Also server-local.
    CREATE_USER  VARCHAR(20)  NULL,
    DEL_FLG      CHAR(1)      NULL
);
GO
CREATE CLUSTERED INDEX IX_APPT_DT ON APPT (APPT_DT, APPT_TM);
GO

/*------------------------------------------------------------------------------
  PROC_CODE -- ADA CDT procedure codes.
  Populated by the vendor, but practices edit it, and data entry has never been
  case-consistent. Under CI collation nobody has ever noticed. See #3.
------------------------------------------------------------------------------*/
CREATE TABLE PROC_CODE (
    PROC_CD      CHAR(5)      NOT NULL,   -- 'D1110'
    PROC_DESC    VARCHAR(60)  NULL,
    DEFAULT_FEE  FLOAT        NULL,
    CATEGORY     CHAR(4)      NULL,
    ACTIVE_FLG   CHAR(1)      NULL
);
GO

/*------------------------------------------------------------------------------
  LEDGER -- charges, payments, adjustments. Must reconcile to the penny.
  Stored in FLOAT. It does not reconcile to the penny.
------------------------------------------------------------------------------*/
CREATE TABLE LEDGER (
    TRAN_ID      INT          NOT NULL,
    PAT_ID       INT          NULL,
    TRAN_DT      CHAR(8)      NULL,       -- YYYYMMDD
    TRAN_TYPE    CHAR(1)      NULL,       -- 'C'harge 'P'ayment 'A'djustment 'I'ns
    PROC_CD      CHAR(5)      NULL,
    PROV_CD      CHAR(4)      NULL,
    AMT          FLOAT        NULL,       -- #2
    INS_EST_AMT  FLOAT        NULL,
    PAID_AMT     FLOAT        NULL,
    APPLIED_TO   INT          NULL,       -- -> LEDGER.TRAN_ID, sometimes orphaned
    POST_DTM     DATETIME     NULL,
    DEL_FLG      CHAR(1)      NULL
);
GO
CREATE CLUSTERED INDEX IX_LEDGER_PAT ON LEDGER (PAT_ID, TRAN_DT);
GO

/*------------------------------------------------------------------------------
  INS_PLAN / RECALL
------------------------------------------------------------------------------*/
CREATE TABLE INS_PLAN (
    INS_PLAN_ID  INT          NOT NULL,
    CARRIER_NM   VARCHAR(40)  NULL,
    GROUP_NBR    VARCHAR(20)  NULL,
    ANN_MAX_AMT  FLOAT        NULL,
    DEDUCT_AMT   FLOAT        NULL,
    COV_PCT_PREV SMALLINT     NULL,       -- preventive  %
    COV_PCT_BASIC SMALLINT    NULL,
    COV_PCT_MAJOR SMALLINT    NULL
);
GO

CREATE TABLE RECALL (
    RECALL_ID    INT          NOT NULL,
    PAT_ID       INT          NULL,
    RECALL_TYPE  CHAR(4)      NULL,       -- 'PROP','PERI','XRAY'
    DUE_YM       CHAR(4)      NULL,       -- YYMM. Two-digit year. In 2026. See #5.
    LAST_SENT_DT CHAR(8)      NULL,
    DEL_FLG      CHAR(1)      NULL
);
GO

/*------------------------------------------------------------------------------
  Somebody added this in 2004. It is not in any documentation. It is not in the
  vendor's upgrade scripts. It exists on roughly a third of the fleet, and any
  migration that replays writes without accounting for it will diverge. See #7.
------------------------------------------------------------------------------*/
CREATE TRIGGER TR_APPT_AUDIT ON APPT AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE PAT_MSTR
       SET LAST_VISIT = i.APPT_DT
      FROM PAT_MSTR p
      JOIN inserted i ON i.PAT_ID = p.PAT_ID
     WHERE i.APPT_STAT = 'C';           -- completed
END
GO

/*------------------------------------------------------------------------------
  APPT_REMINDER -- added 07.01.04 (2021), for text and email reminders.

  Note that this table is nothing like the ones above it. Real IDENTITY primary
  key, DATETIMEOFFSET instead of split CHAR columns, NVARCHAR instead of CHAR,
  BIT instead of a four-valued CHAR(1) flag, and a check constraint. Whoever
  wrote it knew what they were doing.

  They still could not fix the join to APPT, because APPT HAS NO PRIMARY KEY --
  only a clustered index on (APPT_DT, APPT_TM) -- so there is nothing for a
  foreign key to reference. The 2021 developer did everything right on their own
  table and was still forced to carry an unenforceable integer reference into a
  1997 table. That is what "we'll fix the old stuff later" looks like 24 years
  on.

  There is one accidental gift here. SCHEDULED_AT is a DATETIMEOFFSET, so for
  any practice that has reminder rows, the stored offset is EVIDENCE of what the
  practice's real UTC offset was at that moment -- a timezone signal that exists
  nowhere else in the schema. It only covers practices on 07.01.04 or later, so
  it cannot resolve the whole fleet, but it is better evidence than ST_CD.
  See docs/LANDMINES.md #1.
------------------------------------------------------------------------------*/
CREATE TABLE APPT_REMINDER (
    REMINDER_ID   BIGINT           IDENTITY(1,1) NOT NULL,
    APPT_ID       INT              NOT NULL,   -- -> APPT.APPT_ID, and no FK is possible
    CHANNEL       NVARCHAR(10)     NOT NULL,
    SCHEDULED_AT  DATETIMEOFFSET(0) NOT NULL,  -- zoned. The only zoned column in DENTASYS.
    SENT_AT       DATETIMEOFFSET(0) NULL,
    IS_OPTED_OUT  BIT              NOT NULL CONSTRAINT DF_APPT_REMINDER_OPT DEFAULT (0),
    CONSTRAINT PK_APPT_REMINDER PRIMARY KEY (REMINDER_ID),
    CONSTRAINT CK_APPT_REMINDER_CHANNEL CHECK (CHANNEL IN (N'SMS', N'EMAIL'))
);
GO
CREATE INDEX IX_APPT_REMINDER_APPT ON APPT_REMINDER (APPT_ID);
GO
