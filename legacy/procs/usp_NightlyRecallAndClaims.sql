/*==============================================================================
  usp_NightlyRecallAndClaims

  The 2 AM SQL Agent job. Finds every patient due for a recall, stamps the
  recall as sent, and returns the list the letter merge prints from. No retries,
  no backoff, no record of whether it ran: a practice finds out it failed when
  patients stop calling to book.

  MIGRATION DESTINATION: Dentasys.Worker (`--recall`). The selection rules move to
  RecallPolicy in C#; the "stamp and hand off" step becomes a transaction that
  writes last_sent and the outbox together.

  Only the recall half is simulated. The claims half batches insurance claims
  out of tables this lab does not model, and inventing them would be scaffolding.

  The proc stays here as the parity harness's ORACLE, run inside a transaction
  that is rolled back so it never stamps the fixtures.

  Original author unknown. Modified 1999 (Y2K), 2003, 2012.
==============================================================================*/
CREATE PROCEDURE usp_NightlyRecallAndClaims
    @RUN_DT  CHAR(8) = NULL     -- YYYYMMDD. NULL means today, on the SERVER's clock.
AS
BEGIN
    SET NOCOUNT ON;

    /*  LANDMINE #1, again. "Today" is the data center's date at 2 AM, not the
        practice's. For a practice three time zones west it is still yesterday.
        It has never mattered because recall is month-grained -- except on the
        last night of the month.  */
    IF @RUN_DT IS NULL SET @RUN_DT = CONVERT(CHAR(8), GETDATE(), 112);

    DECLARE @RUN_YM     CHAR(6);
    DECLARE @RESEND_DT  CHAR(8);
    SET @RUN_YM    = LEFT(@RUN_DT, 6);
    -- Overdue recalls are re-sent every 30 days until the patient books.
    SET @RESEND_DT = CONVERT(CHAR(8), DATEADD(day, -30, CONVERT(DATETIME, @RUN_DT, 112)), 112);

    SELECT r.RECALL_ID,
           r.PAT_ID,
           r.RECALL_TYPE,
           r.DUE_YM,

           /*  LANDMINE #5 -- the Y2K remediation. DUE_YM is YYMM, and in 1999
               the fix was a pivot window rather than a wider column: below 50 is
               20xx, otherwise 19xx. A pediatric recall due in March 2051 is
               '5103', which this reads as March 1951. It is 75 years overdue, so
               it goes out every 30 days, to a family whose child is seven.  */
           CASE WHEN LEFT(r.DUE_YM, 2) < '50' THEN '20' ELSE '19' END + r.DUE_YM
               AS DUE_YYYYMM
      INTO #DUE
      FROM RECALL   r WITH (NOLOCK)
      JOIN PAT_MSTR p WITH (NOLOCK) ON p.PAT_ID = r.PAT_ID
     WHERE ISNULL(p.DEL_FLG, 'N') <> 'Y'

       /*  RECALL.DEL_FLG arrived in 07.02.11. This proc predates it and was
           never updated to read it -- referencing it would not even compile on
           the older practices -- so a recall cancelled at the front desk still
           goes out once it falls due.  */

       AND CASE WHEN LEFT(r.DUE_YM, 2) < '50' THEN '20' ELSE '19' END + r.DUE_YM <= @RUN_YM
       AND (r.LAST_SENT_DT IS NULL OR r.LAST_SENT_DT < @RESEND_DT);

    UPDATE r
       SET LAST_SENT_DT = @RUN_DT
      FROM RECALL r
      JOIN #DUE d ON d.RECALL_ID = r.RECALL_ID;

    SELECT RECALL_ID, PAT_ID, RECALL_TYPE, DUE_YM, DUE_YYYYMM
      FROM #DUE
     ORDER BY RECALL_ID;
END
GO
