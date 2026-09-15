# Landmines

Every defect below is deliberately planted in `legacy/`, is a real pattern from
real 1990s line-of-business software, and is **detectable by the parity harness**.
That last property is the point: the harness exists to find these before a
practice does.

Ordered by how much damage each does during a real cutover.

---

## #1 — The appointment book has no timezone *(the marquee bug)*

`APPT.APPT_DT CHAR(8)` + `APPT.APPT_TM CHAR(4)`. Local wall-clock, no offset,
no IANA zone, nowhere in the schema.

This was never correct. It has only ever been *unfalsified*.

The databases live in the company's data center, not in the practice, so server
time and practice-local time have differed for 29 years. The scheme holds because
the .NET client sends a wall-clock reading from the workstation and nothing ever
interprets it: every query is scoped to a single practice, and within one practice
every reading shares the same unstated zone. The ambiguity is real and always has
been — it simply has no way to surface while no question spans two practices.

Consolidation is the event that starts asking those questions, in four escalating
ways:

1. **Server moves.** The DB moves from the company DC to `us-east-1`. Practice is
   in Phoenix. Any code path that touches `GETDATE()` — "today's schedule", the
   nightly job's date boundary, `CREATE_DTM` — shifts. This one is a change of
   degree: those paths were already wrong relative to the practice, and now they
   are wrong by a different amount.
2. **Arizona.** Phoenix doesn't observe DST. Half the year it's UTC−7, half the
   year the rest of Mountain time moves and Phoenix doesn't. A fixed offset
   per practice is *not* sufficient; you need the IANA zone.
3. **Spring forward.** 2:30 AM doesn't exist on that date. A naive
   `timestamp → timestamptz` conversion either throws or silently shifts.
4. **Fall back.** 1:30 AM happens *twice*. Which one is the appointment? The
   data cannot tell you. This is genuinely unrecoverable from the row alone,
   and the honest answer at cutover is a documented, deterministic tie-break
   plus an exception report for humans.

**Why it's the best problem in the repo:** it's unfixable by schema conversion
tooling. AWS SCT will happily map `CHAR(8)`/`CHAR(4)` to `text` and report zero
issues. Correctness requires *domain* knowledge — that `PRACTICE.ST_CD` is the
only geographic signal in the schema, and that state is not a timezone (Florida,
Indiana, Tennessee, Oregon and Idaho all split). Deriving IANA zones for the
fleet is a data problem with no clean answer, which is exactly why it belongs to
whoever owns the data.

**Target design:** store `appt_start_utc timestamptz` **and** the original
`appt_local_wall` + `practice_tz`. Keep both. The local wall-clock value is the
business's actual intent; UTC is for ordering and querying. Throwing away the
wall-clock is the classic irreversible mistake.

---

## #2 — Money in `FLOAT`

`LEDGER.AMT`, `PAID_AMT`, `INS_EST_AMT`, `PAT_MSTR.BAL_AMT`, `PROC_CODE.DEFAULT_FEE`.

Binary floating point cannot represent `0.10`. Aging buckets drift by cents,
then dollars across a few hundred thousand transactions. Practices already know
their A/R report is "a little off" and have stopped reporting it.

Target is `numeric(12,2)`. The trap: this makes the migrated system **disagree
with the legacy system** — correctly. The parity harness must classify this as
*expected divergence with a known direction*, not a regression, or you'll spend
a week chasing your own fix. Getting that distinction right in the harness is
most of its value.

---

## #3 — Case-insensitive collation

Server is `SQL_Latin1_General_CP1_CI_AS`. PostgreSQL is case-**sensitive** by
default. Joins on `PROC_CD`, `PROV_CD` and `CHART_NBR` that have worked for 29
years start dropping rows.

It does not error. It does not log. `LEFT JOIN` turns the description NULL and
`INNER JOIN` silently loses the row from the production report.

This is the **#1 silent data corrupter** in SQL Server → PostgreSQL migrations
generally. Options: `citext`, `COLLATE "und-x-icu"` non-deterministic collations,
or normalizing the data on load. Have a defended opinion — this question gets
asked.

---

## #4 — `NOLOCK` on every table

Added by a consultant in 2004 instead of fixing indexes. PostgreSQL's MVCC has
no equivalent and needs none, so the *migration* is trivial — but any behavior
that depended on reading uncommitted rows changes. Front desk staff have
adapted to the resulting dirty reads and will report the corrected behavior as
a bug.

---

## #5 — Two-digit years, in 2026

`RECALL.DUE_YM CHAR(4)` is `YYMM`. The Y2K remediation in 1999 added a pivot
window (`< 50` → `20xx`, else `19xx`) rather than widening the column, because
widening it meant touching the Crystal Reports.

Everything past 2049 lands in the 1900s. That's not hypothetical for perio
recall intervals on pediatric patients already on the books.

---

## #6 — `LEN_UNITS` means two different things

Documented as 10-minute units. Practices on a 15-minute grid store 15-minute
units in the same column with no distinguishing flag. The client infers the
grid from a workstation `.INI` file.

**That `.INI` file is not in the database.** It will not be migrated. So the
appointment duration for an unknown fraction of the fleet is *not recoverable
from the database alone* — you have to go get configuration that lives on
endpoints. Every migration of a system this old has at least one of these, and
finding them is discovery work, not coding work.

---

## #7 — The undocumented trigger

`TR_APPT_AUDIT`, added 2004, in no documentation and no vendor upgrade script.
Present on roughly a third of the fleet. It writes to `PAT_MSTR.LAST_VISIT` as
a side effect of updating `APPT`.

Any migration that replays writes against the new system without reproducing
this will diverge — and only on the subset of practices that have it. This is
the concrete argument for why parity must run across a **fleet distribution**,
not against one golden database.

---

## #8 — String date arithmetic

`CAST(APPT_TM AS INT) + minutes`. `0930 + 45 = 975`, which is not a time. The VB
client patches it; Crystal Reports doesn't. Schedule and reports disagree.

---

## #9 — `NULL` propagation through `+`

`RTRIM(LAST_NM) + ', ' + RTRIM(FIRST_NM)` is `NULL` if either side is `NULL`.
PostgreSQL's `||` behaves identically — but "cleaning it up" to `CONCAT()`
during porting does **not**, and that changes which rows go blank on screen.
A cosmetic-looking edit with behavioral consequences.

---

## #10 — Soft delete with four truth values

`DEL_FLG` is `'Y'`, `'N'`, `NULL`, or `''`, depending on which client version
wrote the row. `usp_GetScheduleForDay` catches `NULL` but not `''`.
`usp_RptProductionCollection` catches both.

The schedule and the production report have disagreed since 2011. Every practice
has invented its own folk explanation. When you fix it, revenue numbers move,
and someone will have to explain that to a dentist.

---

## #11 — Plaintext SSN, and the HIPAA constraint

`PAT_MSTR.SSN CHAR(9)`, unencrypted, because 1997.

This is the reason this repo **generates** its seed data rather than shipping a
sample. Under HIPAA you cannot pull production PHI to a laptop to debug a
migration, which means realistic synthetic fixtures aren't a nice-to-have —
they're the only way the work can legally be done. That constraint shapes the
whole test strategy, and it's a data-engineering problem, not a compliance
checkbox.
