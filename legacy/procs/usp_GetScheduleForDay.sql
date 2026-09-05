/*==============================================================================
  usp_GetScheduleForDay

  Paints the appointment book. Called every time the schedule screen refreshes,
  which the client does on a 30-second timer, per workstation, all day. On a
  ten-chair practice this is the most frequently executed statement in DENTASYS
  by two orders of magnitude.

  MIGRATION DESTINATION: stays in the database, as PL/pgSQL.

  Not everything should leave the DB. This is a tight, latency-sensitive read
  whose logic is genuinely relational. Hauling it into C# would add a network
  round trip to the hottest path in the product in exchange for testability we
  can get other ways. Port it, don't rewrite it.

  Original author unknown. Modified 2001, 2004, 2009, 2014, 2019.
==============================================================================*/
CREATE PROCEDURE usp_GetScheduleForDay
    @APPT_DT   CHAR(8),          -- YYYYMMDD, local wall-clock, caller's timezone
    @OPER_CD   CHAR(4) = NULL,
    @PROV_CD   CHAR(4) = NULL,
    @INCL_DEL  CHAR(1) = 'N'
AS
BEGIN
    SET NOCOUNT ON;

    /*  LANDMINE #1 -- there is no timezone anywhere in this procedure, because
        there is no timezone anywhere in the schema. @APPT_DT is whatever the
        workstation's clock said, and APPT.APPT_DT is whatever some other
        workstation's clock said when the appointment was booked. On-prem those
        two clocks were in the same building, so this worked for 29 years.

        The moment this database lives in us-east-1 and the workstation is in
        Phoenix -- which does not observe DST -- these are different facts.  */

    /*  LANDMINE #4 -- NOLOCK on every table. This proc was made "fast" in 2004
        by a consultant who added NOLOCK everywhere rather than fixing the
        indexes. SQL Server tolerates this; PostgreSQL's MVCC has no equivalent
        and needs none, but any behavior that depended on reading uncommitted
        rows will change. Front desk staff have learned to double-book through
        the resulting dirty reads.  */

    SELECT
        a.APPT_ID,
        a.APPT_DT,
        a.APPT_TM,

        /*  LANDMINE #6 -- appointment length. LEN_UNITS is documented as
            10-minute units. Practices configured to a 15-minute grid store
            15-minute units in the same column, with no flag distinguishing
            them. The client infers the grid from a .INI file on the
            workstation. That .INI file is not in the database, so it will not
            be migrated, and this arithmetic is wrong for an unknown fraction
            of the fleet.  */
        a.LEN_UNITS,
        a.LEN_UNITS * 10 AS APPT_MINUTES,

        /*  LANDMINE #8 -- string date math. Adding minutes to a CHAR(4) HHMM
            by casting to INT. 0930 + 45 minutes = 975, which is not a time.
            The client patches this up in VB. The report writer does not.  */
        RIGHT('0000' + CAST(CAST(a.APPT_TM AS INT) + (a.LEN_UNITS * 10) AS VARCHAR(4)), 4)
            AS APPT_END_TM,

        a.PAT_ID,
        RTRIM(p.LAST_NM) + ', ' + RTRIM(p.FIRST_NM) AS PAT_NM,

        /*  LANDMINE #9 -- '+' concatenation. If FIRST_NM is NULL the entire
            patient name becomes NULL under default SQL Server settings, and the
            row paints as a blank block on the schedule. PostgreSQL's || behaves
            the same way here, but CONCAT() does not -- so a "cleanup" during
            porting silently changes which rows go blank.  */

        p.HOME_PHONE,
        p.BAL_AMT,
        a.PROV_CD,
        v.PROV_NM,
        a.OPER_CD,
        o.OPER_NM,
        a.PROC_CD,

        /*  LANDMINE #3 -- the collation join. PROC_CODE.PROC_CD is entered by
            practice staff and is not case-consistent: 'D1110', 'd1110' and
            'D111O' (letter O) all exist in the wild. Under this server's
            CI collation the first two join fine. Under PostgreSQL's
            case-SENSITIVE default they do not, and the procedure description
            silently becomes NULL on the schedule. This does not error. It does
            not log. It just quietly stops working for some rows.  */
        c.PROC_DESC,

        a.APPT_STAT,
        a.NOTE_TXT
      FROM APPT a WITH (NOLOCK)
      LEFT JOIN PAT_MSTR  p WITH (NOLOCK) ON p.PAT_ID  = a.PAT_ID
      LEFT JOIN PROV      v WITH (NOLOCK) ON v.PROV_CD = a.PROV_CD
      LEFT JOIN OPER      o WITH (NOLOCK) ON o.OPER_CD = a.OPER_CD
      LEFT JOIN PROC_CODE c WITH (NOLOCK) ON c.PROC_CD = a.PROC_CD
     WHERE a.APPT_DT = @APPT_DT
       AND (@OPER_CD IS NULL OR a.OPER_CD = @OPER_CD)
       AND (@PROV_CD IS NULL OR a.PROV_CD = @PROV_CD)

       /*  LANDMINE #10 -- the soft delete. DEL_FLG is 'Y', 'N', NULL or ''
           depending on which version of the client wrote the row. This
           predicate catches NULL but not ''. usp_RptProductionCollection uses
           a different predicate and catches both. The schedule and the
           production report have therefore disagreed since 2011, and every
           practice has a folk explanation for why.  */
       AND (@INCL_DEL = 'Y' OR ISNULL(a.DEL_FLG,'N') <> 'Y')

     ORDER BY a.OPER_CD, a.APPT_TM;
END
GO
