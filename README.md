# DENTASYS Modernization Lab

A simulation of a real modernization: a long-established company whose dental
practice-management product was built in the 1990s, moving off on-prem SQL
Server with the business logic in stored procedures, onto cloud PostgreSQL.

The company hosts the databases itself, in its own data center, and its client
practices reach them through a .NET desktop application. The databases are not
in the dental offices; the *practices* are. That distinction turns out to matter
more than anything else here.

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

## The thesis: the database stops holding behavior

The 1997 system put its logic in stored procedures because that was the only
place to put it. A fat .NET client on every workstation opened its own connection
and called procs; the database was the application server.

The target inverts that. **No stored procedure logic survives the migration in
any form** — not as T-SQL, and deliberately not as PL/pgSQL either. Porting a
procedure to a different procedural dialect relocates the problem without solving
it: the rules stay untestable, unreviewable as a diff, and undebuggable with a
debugger.

| Legacy proc | Destination | Why |
|---|---|---|
| `usp_PostLedgerAndAging` — cursor applying payment/aging rules | **C# domain service** | Business rules belong in version control, code review and unit tests. |
| `usp_RptProductionCollection` — the report every owner stares at | **dbt model** | Good set-based SQL. Keep it SQL; add lineage and tests. Don't rewrite it into worse C#. |
| `usp_GetScheduleForDay` — repaints every 30s per workstation | **C# read service behind an API** | The hot path is the one most worth making testable, not the one to make an exception of. |
| `usp_NightlyRecallAndClaims` — the 2 AM SQL Agent job | **.NET Worker Service** | Needs retries, observability, scheduling. A DB job agent gives you none of that. |

What is left in PostgreSQL is data and integrity: types, keys, constraints. No
triggers, no procedures, no computed business rules.

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
- [ ] Writes, and the event-driven path that goes with them
- [ ] Remaining three procs and their destinations
- [ ] dbt model for the production/collection report
- [ ] AWS: CDK in C#, RDS PostgreSQL, ECS Fargate, DMS, OTel → CloudWatch
- [ ] Cutover runbook — strangler fig, dual-write, shadow reads, rollback triggers

Everything so far is the **read** path. Events belong with writes — an
appointment change publishing a domain event, the 2 AM job becoming a worker that
consumes them — and building an event bus for a read-only schedule screen would
be scaffolding with nothing to hold up.

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
legacy/              SQL Server, 1997, defects intact
  01_schema.sql
  02_seed.sql          one practice, synthetic, every row tied to a landmine
  03_seed_fleet.sql    24 databases that disagree with each other
  procs/               the oracle, not a dependency

target/              the PostgreSQL side
  01_schema.sql        landing_ / dentasys / harness, and why they are separate
  02_tz_resolve.sql    ZIP3 -> IANA, state fallback, DST edge classification
  03_transform.sql     landing_ -> dentasys, refusing to guess
  04_score.sql         scored against ground truth; asserts the safety property
  export_fleet.sql     SQL Server -> psql COPY stream

dotnet/
  src/Dentasys.Domain/          rules. No data-access dependency, by construction.
  src/Dentasys.Data.SqlServer/  legacy adapter + the fat-client path
  src/Dentasys.Data.Postgres/   target adapter + the guard rails
  src/Dentasys.Api/             the API gate -- the only thing holding a connection
  src/Dentasys.Client/          HTTP client. References no database driver.
  src/Dentasys.App/             console UI, renders from either stack
  src/Dentasys.Parity/          the classification model and the comparer
  tests/Dentasys.Parity.Tests/  the two comparisons, across the fleet

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

Everything is a `just` recipe. The system under simulation is from 1997; the
toolchain around it is not, because a migration you cannot reproduce on demand is
a migration you cannot verify.

```bash
just up            # start both engines, block until healthy
just build         # 1997 schema + procs + sandbox seed + 24-practice fleet
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
diffs the two. They are identical, which is the whole point:

```
================ diff of the rendered book =======================
  identical
```

Individually:

```bash
just show 000417 2026-03-08 --legacy     # direct connection, the 1997 way
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
