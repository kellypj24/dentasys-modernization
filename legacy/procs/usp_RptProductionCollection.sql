/*==============================================================================
  usp_RptProductionCollection

  The production / collection report: what each provider billed, wrote off and
  collected over a date range. Every practice owner reads it at month end, and
  most of them read it more often than that. Crystal Reports calls this proc
  against the live practice database, during business hours, while the front
  desk is booking into the same tables.

  MIGRATION DESTINATION: a dbt model on DuckDB (analytics/). No PL/pgSQL port,
  and not a C# rewrite either. Unlike the schedule proc this is honest set-based
  SQL, and it should stay SQL -- the problem with it is where it runs, not what
  language it is in.

  The proc stays here as the parity harness's ORACLE. The modern report is
  checked against it, provider by provider, across the fleet.

  Original author unknown. Modified 1999, 2006, 2011.
==============================================================================*/
CREATE PROCEDURE usp_RptProductionCollection
    @FROM_DT  CHAR(8),          -- YYYYMMDD, inclusive
    @TO_DT    CHAR(8)           -- YYYYMMDD, inclusive
AS
BEGIN
    SET NOCOUNT ON;

    /*  LANDMINE #4 -- NOLOCK again. On a report this means month-end totals can
        include a payment that the posting screen then rolls back. Nobody has
        ever reconciled the two closely enough to notice.  */

    SELECT
        l.PROV_CD,

        /*  LANDMINE #3 -- production only counts a charge whose PROC_CD exists
            in PROC_CODE. Under this server's CI collation 'D1110' on the ledger
            matches 'd1110' in the code table. A case-sensitive engine drops
            those charges from production without an error, and the provider's
            number simply goes down.  */
        SUM(CASE WHEN l.TRAN_TYPE = 'C' AND c.PROC_CD IS NOT NULL
                 THEN l.AMT ELSE 0 END)                      AS PRODUCTION,

        SUM(CASE WHEN l.TRAN_TYPE = 'A' THEN l.AMT ELSE 0 END) AS ADJUSTMENTS,

        SUM(CASE WHEN l.TRAN_TYPE = 'C' AND c.PROC_CD IS NOT NULL THEN l.AMT
                 WHEN l.TRAN_TYPE = 'A' THEN l.AMT
                 ELSE 0 END)                                 AS NET_PRODUCTION,

        /*  Patient and insurance payments both count as collections. They are
            attributed to the provider on the payment row, not followed through
            APPLIED_TO to the charge -- which is just as well, because APPLIED_TO
            is sometimes orphaned.

            A charge row can carry PAID_AMT too ("paid same day"). That money is
            not a collection here, because the row's TRAN_TYPE is 'C'. Owners who
            take payment at the chair have always seen collections run low. A
            faithful port reproduces this; fixing it is a separate, announced
            decision, not a side effect of migrating.  */
        SUM(CASE WHEN l.TRAN_TYPE IN ('P', 'I') THEN l.PAID_AMT ELSE 0 END)
                                                             AS COLLECTIONS

        /*  LANDMINE #2 -- every SUM above is FLOAT. The totals are the binary
            approximations of the cents, and they drift further as the range
            widens. Crystal formats them to two places, which hides it.  */

      FROM LEDGER l WITH (NOLOCK)
      LEFT JOIN PROC_CODE c WITH (NOLOCK) ON c.PROC_CD = l.PROC_CD

     /*  CHAR(8) YYYYMMDD sorts like a date, so BETWEEN works -- as long as
         nobody has typed a two-digit year into TRAN_DT, which nobody has, yet. */
     WHERE l.TRAN_DT BETWEEN @FROM_DT AND @TO_DT

       /*  LANDMINE #10 -- unlike usp_GetScheduleForDay, this predicate names
           both legacy "live" spellings explicitly: NULL from 07.00.09 clients
           and '' from 06.04.02 clients. Only 'Y' removes a row.  */
       AND (l.DEL_FLG IS NULL OR l.DEL_FLG IN ('N', ''))

     GROUP BY l.PROV_CD
     ORDER BY l.PROV_CD;
END
GO
