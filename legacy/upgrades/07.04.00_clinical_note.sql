/*==============================================================================
  07.04.00 -- CLINICAL_NOTE

  The chart's copy of every signed note and addendum from the ambient notetaker.
  The notes service is the only writer, and it writes only signed content;
  drafts never reach a practice database.

  Like APPT_REMINDER, this is a modern table in a 1997 database: real key,
  zoned timestamp, NVARCHAR, a check constraint. And like APPT_REMINDER it
  carries an APPT_ID it cannot enforce, because APPT has no primary key.

  Ships with release 07.04.00, which each customer installs in its own upgrade
  window, so for months part of the fleet has this table and part does not. The
  notes service holds signed notes for a practice until the table appears.

  (The real upgrade also records 07.04.00 in SCHEMA_VER. The lab leaves
  SCHEMA_VER alone so the migration fixtures keep their version histogram.)
==============================================================================*/
IF OBJECT_ID('dbo.CLINICAL_NOTE') IS NULL
BEGIN
    CREATE TABLE CLINICAL_NOTE (
        NOTE_ID         UNIQUEIDENTIFIER  NOT NULL,   -- minted by the notes service; the upsert key
        KIND            NVARCHAR(10)      NOT NULL,
        PARENT_NOTE_ID  UNIQUEIDENTIFIER  NULL,       -- an addendum's note
        PAT_ID          INT               NOT NULL,
        APPT_ID         INT               NULL,       -- -> APPT.APPT_ID, unenforceable
        PROV_CD         CHAR(4)           NOT NULL,
        NOTE_TEXT       NVARCHAR(MAX)     NOT NULL,   -- what the provider signed, rendered
        NOTE_JSON       NVARCHAR(MAX)     NULL,       -- the same, structured
        SIGNED_BY       NVARCHAR(40)      NOT NULL,
        SIGNED_AT       DATETIMEOFFSET(0) NOT NULL,
        WRITTEN_AT      DATETIMEOFFSET(0) NOT NULL CONSTRAINT DF_CLINICAL_NOTE_WRITTEN DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT PK_CLINICAL_NOTE PRIMARY KEY (NOTE_ID),
        CONSTRAINT CK_CLINICAL_NOTE_KIND CHECK (KIND IN (N'NOTE', N'ADDENDUM'))
    );
    CREATE INDEX IX_CLINICAL_NOTE_PAT ON CLINICAL_NOTE (PAT_ID, SIGNED_AT);
END
GO
