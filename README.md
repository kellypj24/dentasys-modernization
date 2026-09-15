# DENTASYS Modernization Lab

A simulation of a real modernization: a long-established company whose dental
practice-management product was built in the 1990s, moving off on-prem SQL
Server with the business logic in stored procedures, onto cloud PostgreSQL.

The company hosts the databases itself, in its own data center, and its client
practices reach them through a .NET desktop application. The databases are not
in the dental offices; the *practices* are. That distinction turns out to matter
more than anything else here.

**This is not an abandoned system.** The platform has been kept current — the
engine has been upgraded four times since 1997 and these databases run on a
supported version, in a maintained data center, with encryption at rest,
availability groups, tested restores and a DBA who tunes indexes. Assuming
otherwise is the fastest way to be wrong about a system like this, and to insult
the people who have kept it running.

What has *not* moved is the data model, and that is the whole problem.

It exists to answer one question honestly:

> **How do you prove the new system agrees with the old one?**

Everything else here is scaffolding for that.

---

## The problem, stated properly

The naive framing is "port T-SQL to PL/pgSQL." That's the easy part, and AWS SCT
does most of it. The actual problem:

> Fleet-migrate thousands of **divergent single-tenant** SQL Server databases —
> one per dental practice, each on its own upgrade schedule — into one
> multi-tenant PostgreSQL platform, carrying PHI, one practice at a time,
> without any office losing its schedule for an hour.

Four consequences that fall out of that framing:

**There is no "the" database.** Each client practice has its own database, and
they are not all on the same release — customers schedule their own upgrade
windows, and some sit on an old version for years because an upgrade means
revalidating a workflow or retraining staff. So the fleet is a *distribution* of
schema versions (`SCHEMA_VER`), plus per-practice customizations that shipped for
one customer and were never generalized. Parity output is a histogram across the
fleet, not a pass/fail against one golden DB.

**Tenancy is an assumption, and it is stated rather than assumed silently.** This
repo models **database-per-client, centrally hosted**. If the real estate is one
shared multi-tenant database instead, the fleet model collapses, version drift
disappears, and the interesting problem becomes per-tenant cutover out of a
shared database rather than fleet heterogeneity. Nearly everything else here —
the type changes, the collation behavior, the timezone derivation, the parity
classification — is unaffected either way.

**HIPAA is an architecture constraint.** You can't pull production PHI to a laptop
to debug a migration. So realistic synthetic fixtures aren't convenience, they're
the only lawful way to do the work. That's why this repo generates its seed data.

**Cutover is a fleet operation.** Thousands of unattended per-practice migrations,
each needing verification and a rollback path. Closer to a deployment pipeline
than to a database project.

**The scheduler has no timezone.** See below.

---

## The marquee bug

`APPT` stores appointments as local wall-clock time in two `CHAR` columns with no
offset and no zone.

The tempting version of this story is "the server was thirty feet from the chair,
so server time and local time were the same fact." That is not what happened, and
it is worth being precise about, because the true version is worse.

The databases have always been in the company's data center. A Phoenix practice's
data has never lived in Phoenix. What actually works is that the .NET client on
the practice workstation reads the workstation's clock and sends a local
wall-clock reading, and the database stores those four-plus-four characters
verbatim and never interprets them. The scheme survives because nothing ever asks
a question that spans two practices. Every read is scoped to one practice, and
within one practice every reading is in the same unstated zone, so the omission
never surfaces.

So the ambiguity is not *introduced* by the cloud. It is already there, and has
been for 29 years, held in check only by the fact that nobody has ever needed to
order, compare, or aggregate appointments across practices. A consolidation is
precisely the event that starts asking those questions — and anything touching
`GETDATE()` moves the moment the server does. Then add DST, in a product that
books in 15-minute increments: spring-forward gives you an hour that doesn't
exist, fall-back gives you an hour that happens twice, and Arizona doesn't
participate at all.

Schema conversion tooling reports **zero issues** on this. `CHAR(8)` maps cleanly
to `text`. Correctness requires knowing that `PRACTICE.ST_CD` is the only
geographic signal in the schema, and that state ≠ timezone — Florida, Indiana,
Tennessee, Oregon and Idaho all split.

That's the whole thesis: the migration's hardest problems are invisible to the
migration tools, and they're data problems.

---

## What five platform upgrades did not fix

An in-place engine upgrade carries the schema forward without comment. It does
not rewrite a column type, split a `CHAR(8)` date, or add a foreign key — and it
should not, because doing any of that silently would be far more dangerous than
leaving it alone. So each upgrade was competent, low-risk, and completely
orthogonal to every problem in this repo.

| Modernized | Not touched by it |
|---|---|
| Engine 6.5 → 2000 → 2008R2 → 2016 → 2022 | `CHAR(8)` dates, `FLOAT` money, no foreign keys |
| Encryption at rest (TDE) | plaintext SSN, readable by anyone with `SELECT` |
| Availability groups, tested restores | a schema with no primary key on its busiest table |
| Index tuning, better query plans | `NOLOCK` on every table in the hot-path proc |
| Newer tables written with modern types | the 1997 tables the whole product is built on |

That last row is visible in the schema itself. `APPT_REMINDER` shipped in
07.01.04 with an `IDENTITY` primary key, `DATETIMEOFFSET`, `NVARCHAR`, `BIT` and a
check constraint — everything the 1997 tables lack, in the same database,
written by someone who knew exactly what they were doing. The engine was never
what stopped anyone.

It still cannot have a foreign key to `APPT`, because **`APPT` has no primary
key** for one to reference. That is what "we'll fix the old stuff later" looks
like 24 years on: new work done properly, still forced to carry an unenforceable
integer into a 1997 table.

So the schema is **stratified by era**, not uniformly ancient. 17 of the 24
practices have `APPT_REMINDER`; all 24 have `APPT` exactly as it was.

### One accidental gift

`APPT_REMINDER.SCHEDULED_AT` is a `DATETIMEOFFSET` — the only zoned column
anywhere in DENTASYS. For any practice with reminder rows, the stored offset is
direct evidence of that practice's real UTC offset, which exists nowhere else in
the schema.

It covers 17 of 24 practices, so it cannot resolve the fleet on its own, but it is
far better evidence than `ST_CD` and it is sitting there because a developer in
2021 picked a sensible column type without thinking of it as a migration asset.
Finding leverage like that is most of what discovery work actually is.

## The thesis: the database stops holding behavior

The system put its logic in stored procedures in 1997 because that was the only
place to put it, and it has stayed there ever since. A fat .NET client on every workstation opened its own connection
and called procs; the database was the application server.

The target inverts that. **No stored procedure logic survives the migration in
any form** — not as T-SQL, and deliberately not as PL/pgSQL either. Porting a
procedure to a different procedural dialect relocates the problem without solving
it: the rules stay untestable, unreviewable as a diff, and undebuggable with a
debugger.

| Legacy proc | Destination | Why |
|---|---|---|
| `usp_PostLedgerAndAging` — cursor applying payment/aging rules | **C# domain service** | Business rules belong in version control, code review and unit tests. |
| `usp_RptProductionCollection` — the report every owner stares at | **dbt model on DuckDB** | Good set-based SQL. Keep it SQL; add lineage and tests. Don't rewrite it into worse C#, and don't run it against the database the front desk is booking into. |
| `usp_GetScheduleForDay` — repaints every 30s per workstation | **C# read service behind an API** | The hot path is the one most worth making testable, not the one to make an exception of. |
| `usp_NightlyRecallAndClaims` — the 2 AM SQL Agent job | **.NET Worker Service** | Needs retries, observability, scheduling. A DB job agent gives you none of that. |

What is left in PostgreSQL is data and integrity: types, keys, constraints. No
triggers, no procedures, no computed business rules.

**Analytics does not live in PostgreSQL either.** The production/collection
report is the one every practice owner stares at, and in the legacy system it
runs against the live practice database — the same one the front desk is booking
into. That is the same failure this repo already argues about for reads: nothing
stands between an expensive query and the transactional store.

Separating it is the structural fix, and DuckDB is the right shape for it —
columnar, embedded, no server to run, and it reads Parquet and Postgres directly.
The OLTP store keeps serving the appointment book; the report runs somewhere it
cannot lock anything. Putting dbt on top of PostgreSQL would have kept the
reporting load exactly where the problem was.

## The other half: the stack, not just the database

Swapping SQL Server for PostgreSQL and keeping everything else is not a
modernization, it is a port. The shape of the deployment changes too:

```
LEGACY                                  MODERN

 workstation ─┐                          workstation ─┐
 workstation ─┼─► SQL Server             workstation ─┼─► API gate ─► PostgreSQL
 workstation ─┘   (stored procs)         workstation ─┘
                                                │
 every desk holds a connection            one process holds every connection
 and can run anything                     and nothing else can reach the DB
```

That is not architecture astronomy — it is the only place guard rails can be
*enforced* rather than requested. The API sets a server-side `statement_timeout`,
opens read-only transactions for reads, caps rows and reports truncation, and
pools connections. In the legacy stack "please don't run expensive queries" was a
memo; a mistyped date range on one front desk could lock a table for the whole
practice.

The client project references no database driver at all. It cannot open a
connection because it has nothing to open one with.

## Status

- [x] Legacy schema with 11 planted, harness-detectable defects — `docs/LANDMINES.md`
- [x] `usp_GetScheduleForDay`, defects intact — the oracle the new code is checked against
- [x] Single-practice seed, and a 24-practice fleet across 5 schema versions and 9 IANA zones
- [x] Reproducible toolchain — `justfile`, fleet assertions, determinism check
- [x] Target PostgreSQL schema — landing / target / fenced-ground-truth split
- [x] Timezone resolution, scored against ground truth
- [x] **The application** — C# domain rules, two data adapters, API gate, console UI
- [x] **Parity harness** — proc vs C#, and legacy stack vs modern stack, across the fleet
- [x] **Writes** — commands, invariants, domain events, transactional outbox, drainer worker
- [x] **Write parity** — quantifies the behavior change the hidden trigger was masking
- [ ] Remaining three procs and their destinations
- [ ] Analytics: DuckDB + dbt for the production/collection report, fed from PostgreSQL
- [ ] AWS: CDK in C#, RDS PostgreSQL, ECS Fargate, DMS, OTel → CloudWatch
- [ ] Cutover runbook — strangler fig, dual-write, shadow reads, rollback triggers

## Setup

Needed now: **Docker** and the **.NET 10 SDK**. Needed later, for the AWS work:
the AWS CLI and Node — the CDK CLI is a Node app even when you author infra in C#.

```bash
brew install --cask docker
brew install dotnet          # 10.0 or newer
```

**Apple Silicon:** there is no ARM64 SQL Server image, and Azure SQL Edge was
retired 2025-09-30. The compose file pins `platform: linux/amd64` and runs under
Rosetta, which is Microsoft's own recommendation. Enable it first:

> Docker Desktop → Settings → General → **Use Rosetta for x86/amd64 emulation**

It's slow. At these data volumes that's irrelevant.

```bash
just up        # brings up both engines and waits for the healthcheck
```

---

## Layout

```
legacy/              SQL Server. 1997 data model, current engine, defects intact
  01_schema.sql
  02_seed.sql          one practice, synthetic, every row tied to a landmine
  03_seed_fleet.sql    24 databases that disagree with each other
  procs/               the oracle, not a dependency

target/              the PostgreSQL side
  01_schema.sql        landing_ / dentasys / harness, and why they are separate
  02_tz_resolve.sql    ZIP3 -> IANA, state fallback, DST edge classification
  03_transform.sql     landing_ -> dentasys, refusing to guess
  04_score.sql         scored against ground truth; asserts the safety property
  05_write_model.sql   outbox, audit, and the constraints 1997 never had
  export_fleet.sql     SQL Server -> psql COPY stream

dotnet/
  src/Dentasys.Domain/          rules. No data-access dependency, by construction.
  src/Dentasys.Data.SqlServer/  legacy adapter + the fat-client path
  src/Dentasys.Data.Postgres/   target adapter + the guard rails
  src/Dentasys.Api/             the API gate -- the only thing holding a connection
  src/Dentasys.Client/          HTTP client. References no database driver.
  src/Dentasys.App/             console UI, renders from either stack
  src/Dentasys.Worker/          drains the outbox; replaces the 2 AM SQL Agent job
  src/Dentasys.Parity/          the classification model and the comparer
  tests/Dentasys.Parity.Tests/  read parity, write parity, and the booking rules

tests/assert_fleet.sql    invariants the spawned fleet must satisfy
docs/LANDMINES.md         the 11 planted defects
docker-compose.yml
justfile
```

The dependency direction is the design. `Dentasys.Domain` references nothing, so
the rules cannot reach a database even by accident. `Dentasys.Client` references
no driver, so a workstation cannot open a connection. `Dentasys.App` has no
reference to `Dentasys.Data.Postgres` at all — the modern path reaches PostgreSQL
through the API or not at all.

## Running it

Everything is a `just` recipe. The data model under simulation is from 1997; the
toolchain around it is not, because a migration you cannot reproduce on demand is
a migration you cannot verify.

```bash
just up            # start both engines, block until healthy
just build         # legacy schema + procs + sandbox seed + 24-practice fleet
just migrate       # target schema, export, transform, score against ground truth
just test          # assert the fleet matches the roster
just parity        # the harness: proc vs C#, and legacy stack vs modern stack
```

`just check` is the gauntlet — rebuild both engines from source, then assert
everything. It is the command to run before pushing, and the only claim of "it
works" this repo will make.

### Seeing it work

```bash
just api                                   # terminal 1: the API gate
just demo 000417 2026-03-08                # terminal 2: both stacks, side by side
```

`just demo` renders the same day twice — once from a fat client talking straight
to SQL Server, once from a client that goes through the API to PostgreSQL — and
diffs the two. They are identical:

```
================ diff of the rendered book =======================
  identical
```

Individually:

```bash
just show 000417 2026-03-08 --legacy     # direct connection, the fat-client way
just show 000417 2026-03-08 --modern     # through the API gate
```

### Watching a discovery unblock the screen

End times render as `--` because `LEN_UNITS` is 10-minute units on part of the
fleet and 15 on the rest, and the grid lives in a workstation `.INI` file that
will not be migrated (LANDMINE #6). Nothing in the database can tell you which.

Record the answer once somebody actually asks the practice:

```bash
just set-grid 000417 10
just show 000417 2026-03-08 --modern
```

```
  OP     TIME    END     PATIENT              PROVIDER          PROCEDURE                ST
  HYG1   23:50   00:20+  Nakamura, Nadia      Ibarra, I. RDH    Topical fluoride varnish C
  OP1    02:30   03:30   Mbeki, Marisol       Guerrero, G. DDS  Comprehensive oral eval… X
```

Note `23:50 → 00:20+`. The legacy procedure renders that same appointment as
`"2380"`, because it adds minutes to `HHMM` cast to an integer (LANDMINE #8).

## The marquee bug, solved and measured

`just migrate` runs the whole modernization path: target schema, export from
SQL Server, transform, score.

The migration has to derive each practice's IANA zone from what the product
actually contains — `ST_CD`, `CITY`, `ZIP_CD` — and is then scored against
`harness.fleet_roster`, which it is never allowed to read.

```
timezone inference vs ground truth
  tz_source      practices  correct  wrong    pct
  zip_prefix            20       20      0  100.0
  state_default          4        2      2   50.0
  fleet                 24       22          91.7

the misses
  000804  Ontario, OR        97914  inferred America/Los_Angeles  actual America/Boise
  000906  Coeur d'Alene, ID  83814  inferred America/Boise        actual America/Los_Angeles
```

Both misses are split states where the ZIP3 reference had no coverage and the
resolver fell back to `ST_CD`. That is the thesis in two rows: a valid IANA
zone, a successful conversion, no error anywhere, and an entire office an hour
off.

### The number that actually matters

Accuracy is the headline, not the point. This is the point:

```
flagged_unsafe  flagged_and_wrong  flagged_but_right  unflagged_and_wrong
             4                  2                  2                    0
```

`unflagged_and_wrong` has to be zero, and it is asserted, not reported — the
score step raises and exits nonzero if a practice is wrong without having been
flagged first. The migration cannot know it got Ontario wrong. It *can* know
that resolving a split state from `ST_CD` alone is untrustworthy, and it marks
that at resolution time, before anyone knows the answer. Refusing to launder a
guess into a fact is the whole job.

`flagged_but_right` is 2: Fargo and Anchorage were flagged and turned out fine.
That is the correct trade. Being conservative about scheduling data costs two
phone calls; being confident costs an office a day of appointments.

### What it refuses to do

```
nonexistent_local_times  ambiguous_local_times  invented_an_instant
                     21                     21                    0
```

Spring-forward 02:30 has no UTC instant, so `start_utc` is NULL and a CHECK
constraint forbids inventing one. Fall-back 01:30 happens twice; the tie-break
is the later occurrence, documented and deterministic, and every affected row
goes on the exception report. Both counts are 21 rather than 24 because Phoenix,
Tucson and Honolulu don't observe DST — the *identical row* is unambiguous there:

```
 practice_id  practice_tz       booked_local      utc                 nonexistent  ambiguous
 000417       America/Phoenix   2026-11-01 01:30  2026-11-01 08:30    f            f
 001010       America/New_York  2026-11-01 01:30  2026-11-01 06:30    f            t
```

Appointment duration is NULL for all 264 rows, flagged `duration_unrecoverable`.
`LEN_UNITS` is 10-minute units on part of the fleet and 15 on the rest, and the
grid lives in a workstation `.INI` that will never be migrated. Writing
`len_units * 10` would produce a column that is right for most of the fleet and
quietly 50% short for the rest — worse than NULL, because a NULL stops someone
and a plausible wrong number does not.

### The manual queue

```
appointment_duration_not_in_database         blocker  24
local_time_does_not_exist                    blocker  21
tz_resolved_by_state_default_in_split_state  blocker   4
local_time_is_ambiguous                      review   21
```

Not a defect count — the size of the human queue before cutover. Knowing it in
advance is the difference between a migration and a surprise.

## The parity harness

*How do you prove the new system agrees with the old one?* — the question the
repo exists to answer.

Because the same C# rules run over both databases, the question splits in two,
and the split is what makes a red run useful:

| Comparison | Both sides read | Proves |
|---|---|---|
| Stored procedure **vs** C#, over SQL Server | the same database | the **logic** was ported faithfully |
| Legacy stack **vs** modern stack, end to end | different databases | the **data** migrated faithfully |

If only the first is red, the port is wrong. If only the second is red, the
migration is wrong. One number could not tell you which.

Both run across **24 practices × 10 dates = 240 screens**, including both DST
edges, the midnight-crossing appointment, a patient with no first name, and two
dates with nothing booked.

### Producing the diff is the easy half

Deciding what a difference *means* is the hard half, and getting it wrong in
either direction is expensive. Call a real defect expected and it ships. Call an
intended improvement a regression and you spend a week chasing your own fix, then
start ignoring the report — which is worse, because a report nobody reads catches
nothing.

Every difference is classified:

| | |
|---|---|
| `Agree` | byte-for-byte identical |
| `ExpectedDivergence` | a named rule predicted it **and bounded it** |
| `BlockedOnDiscovery` | the answer isn't in the database; somebody has to go and find it |
| `OmittedByPolicy` | deliberately not carried forward, for compliance |
| `Regression` | nothing claims it — **the only disposition that fails a run** |

The default is `Regression`. A field with no rule fails. That direction matters:
a harness that defaults to "probably fine" tells you what you already believed.

### Rules are claims, not mutes

A rule that merely named a field would excuse anything in it — "money may differ"
would wave through a balance out by four hundred pounds. Each rule carries a
predicate that *bounds* the claim, so a difference inside the bound is expected
and a difference outside it is a regression even though a rule for that field
exists:

```csharp
Name      = "float_money_to_numeric",
Field     = nameof(ScheduleSlot.Balance),
Rationale = "Source stores money in FLOAT, which cannot represent 0.10. The target
             uses numeric(12,2), so the migrated value is the correctly rounded one
             and the legacy value is the drifted one. Bounded at half a cent:
             anything larger is a real discrepancy, not representation error.",
Claims    = (legacy, modern) => Math.Abs((decimal)legacy - (decimal)modern) < 0.005m,
```

Current run:

```
  parity: 240 screens, 240 appointments, 480 field differences

    ExpectedDivergence     240      end_time_string_arithmetic_fixed
    BlockedOnDiscovery     240      duration_awaiting_grid_discovery

  no unexplained differences
```

```
  parity: 240 screens, 240 appointments, 0 field differences
```

The second run is the two stacks against each other. Zero differences, not zero
regressions — the test asserts `Findings` is empty, not just `Regressions`. If the
rule book written for procedure-vs-C# quietly started absorbing differences
between the two *databases*, that would be the migration drifting behind a ruleset
meant for something else.

### A harness that cannot fail is decorative

Both directions are tested. Change `procedure_code` from `citext` to `text` in the
target — the case-sensitivity change that makes `d1110` stop joining to `D1110`
(LANDMINE #3), which produces no error and no log — and the harness names every
affected row:

```
  24 UNEXPLAINED difference(s):
    000418 2026-03-02 appt 418001 ProcedureDescription:
        legacy="Prophylaxis - adult" modern=(null) -> Regression
    000513 2026-03-02 appt 513001 ProcedureDescription:
        legacy="Prophylaxis - adult" modern=(null) -> Regression
    ...
```

### One bug the harness found in itself

An early version of the oracle parsed the procedure's `APPT_END_TM` and stored
`null` when parsing failed — which is exactly what `"0860"` does. The target also
returns `null` when the grid is unknown. So null met null, and the harness
reported **agreement** on every row where the legacy system emits a value that is
not a time: 44 differences where there should have been 240.

The raw text is now carried through unparsed and compared as text. A harness that
launders the defect it exists to find is worse than no harness, and the only
reason this surfaced was a count that looked implausible.

## Writes

A read that disagrees is embarrassing. A write that disagrees is permanent.

### The invariants the 1997 schema never had

There are no foreign keys, no check constraints and no unique indexes anywhere in
the legacy database. The fat client composed its own INSERT and whatever it sent,
the database took. So every rule the new system enforces is a **behavior change**,
and each one has to be a decision rather than a tidy-up — staff have spent 29
years adapting to their absence.

Booking into the spring-forward gap, in Brooklyn:

```console
$ just book 001010 101001 2026-03-08 02:30
{
    "title": "The command was rejected by a business rule.",
    "status": 400,
    "errors": {
        "local_time_does_not_exist": [
            "2026-03-08 02:30 does not exist in America/New_York -- the clocks skip it."
        ]
    }
}
```

The identical command in Phoenix, which does not observe DST:

```console
$ just book 000417 41701 2026-03-08 02:30
{
    "appointmentId": 900000000,
    "events": ["AppointmentBooked"],
    "skippedChecks": ["operatory_overlap_check (appointment grid not discovered)"]
}
```

Same request, valid or invalid depending on a column the legacy schema does not
have. The legacy system cannot refuse either one — it has no timezone anywhere, so
the booking goes in and the appointment simply never happens.

### Checks the system cannot perform, reported rather than skipped

Note `skippedChecks` in that response. A chair holds one patient at a time, which
is about as solid as a domain invariant gets — and it is **uncheckable** without
knowing the practice's appointment grid, because overlap needs durations and a
duration is `LEN_UNITS` × a number that lives in a workstation `.INI` file.

So the acceptance is narrower than it looks, and the response says so. It is not a
constraint in the database either, because a constraint that can only be applied
to a third of the rows is not a constraint. Supply the grid and the check starts
working; until then the front desk remains the only overlap detector, exactly as
it has been since 1997.

### The hidden trigger, made visible and counted

`TR_APPT_AUDIT` updates `PAT_MSTR.LAST_VISIT` as a side effect of completing an
appointment. It was added in 2004, it is in no documentation and no upgrade
script, and it exists on **8 of the 24 practices**.

For 22 years, completing an appointment has updated the last-visit date at a third
of the fleet and done nothing at the rest. Recall lists and "patients due" queries
have therefore meant two different things depending on which server answered, and
no report says which.

Making it an explicit policy forces the question the trigger let everyone avoid.
The write-parity harness answers it with a number:

```
  completing an appointment, 24 practices probed

    legacy updated LAST_VISIT        8
    modern updated last_visit       24
    diverge                         16

    practices WITH TR_APPT_AUDIT      8  -> both systems update, agree
    practices WITHOUT it             16  -> only the modern system updates
```

Both halves are asserted, not merely reported. Where the trigger exists the port
must match it exactly — that is what makes the policy a faithful replacement
rather than a plausible one. Where it does not, the modern system does more, and
16 practices are about to see their recall numbers move. That is the right
outcome, and it is still a change somebody has to be told about before it happens
rather than after.

Both sides run inside transactions that are rolled back, so the probe exercises
the real code path and leaves the fixtures exactly as it found them.

### Events, and why there is an outbox

"Save the appointment, then publish the event" is two systems, and anything
between them can fail. Crash after the commit and the event is lost; publish first
and fail to commit and you have announced something that did not happen. Neither
is acceptable for a recall list that drives letters to patients.

The event is written to `dentasys.outbox` in the **same transaction** as the data
change, so they succeed or fail together, and a worker drains the table afterwards:

```console
$ just worker
info: outbox drainer started, polling every 00:00:02
info: AppointmentBooked practice=000417 appointment=900000000
info: published 1 event(s)
```

`FOR UPDATE SKIP LOCKED` lets several workers drain the same table without
coordinating and without processing a row twice — the reason a queue can live in
a relational database rather than needing a broker on day one. The broker becomes
a delivery detail rather than a correctness dependency, which is also why this
repo can demonstrate the pattern honestly without pretending to run Kafka.

Delivery is **at least once**, not exactly once. A crash between handling an event
and marking it published replays it. That is not a defect to engineer away —
exactly-once across two systems is not available at any price — so consumers must
be idempotent, and `attempts` and `last_error` on the outbox row are what turn
"the recall letters never went out" into something a dashboard can show. The 2 AM
SQL Agent job this replaces had no retries, no backoff and no way to know it had
failed except a practice noticing.

### Not everything that reacts to an event should be async

The `LAST_VISIT` update commits **with** the appointment change, not via the
outbox. It is a consistency requirement inside one practice's own data — a
completed appointment and a stale last-visit date must never both be visible. The
outbox is for consumers outside that boundary, which can tolerate arriving a
second later.

Making everything asynchronous because events are fashionable turns an invariant
into a race.

## Fixture verification

`tests/assert_fleet.sql` is the floor beneath the parity harness: proof that the
fixtures are the fixtures this repo claims to produce. It checks trigger
presence against the roster, column counts against version *and* customization
drift, `LEN_UNITS` against each practice's grid, the DST edge rows, and the
version histogram — collecting every failure rather than throwing on the first,
so a broken seed reports all its problems in one run.

It is a real test, not a smoke test: drop `TR_APPT_AUDIT` from one practice and
it fails with `000418 roster=Y actual=0` and exits nonzero.

A harness built on a seed nobody checks reports your seed's bugs as your
migration's bugs. That is the failure mode this exists to prevent.

## What the fleet is for

`DENTASYS_FLEET.dbo.FLEET_ROSTER` is the answer key. Its `IANA_TZ` column is
**ground truth that does not exist anywhere in the product** — the migration has
to infer the zone from `PRACTICE.ST_CD`, and the roster is what you score that
inference against. Seven states in the roster (FL, IN, TN, OR, ID, TX, KY) appear
twice, in two different zones, with the same `ST_CD`.

Three kinds of divergence are modelled, because each breaks something different:

| Divergence | Source | What it breaks |
|---|---|---|
| Schema version | vendor releases, adopted whenever | code written against the current schema meets a 2014 database |
| Customization drift | one-off columns/indexes/triggers, never generalized | two practices on the *same* release with physically different tables |
| Geography | physics | `ST_CD` is not a timezone, and nothing in the DB says so |
