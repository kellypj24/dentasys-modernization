# DENTASYS Modernization Lab

A simulation of a real modernization: a long-established company whose dental
practice-management product was built in the 1990s, moving off on-prem SQL
Server with the business logic in stored procedures, onto cloud PostgreSQL.

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

**There is no "the" database.** Practices upgrade when their office manager feels
like it, so the fleet is a *distribution* of schema versions (`SCHEMA_VER`), plus
per-practice drift from resellers who added columns and triggers directly. Parity
output is a histogram across the fleet, not a pass/fail against one golden DB.

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
offset and no zone. On-prem this was *correct* — the server was thirty feet from
the chair, so "server time" and "local time" were the same fact.

Move it to `us-east-1` and that assumption is silently false for every practice
in the country. Then add DST, in a product that books in 15-minute increments:
spring-forward gives you an hour that doesn't exist, fall-back gives you an hour
that happens twice, and Arizona doesn't participate at all.

Schema conversion tooling reports **zero issues** on this. `CHAR(8)` maps cleanly
to `text`. Correctness requires knowing that `PRACTICE.ST_CD` is the only
geographic signal in the schema, and that state ≠ timezone — Florida, Indiana,
Tennessee, Oregon and Idaho all split.

That's the whole thesis: the migration's hardest problems are invisible to the
migration tools, and they're data problems.

---

## The thesis: four procs, four destinations

Don't migrate everything the same way. Each destination is a defensible position:

| Legacy proc | Destination | Why |
|---|---|---|
| `usp_PostLedgerAndAging` — cursor applying payment/aging rules | **C# domain service** | Business rules belong in version control, code review and unit tests. Not in a DB. |
| `usp_RptProductionCollection` — the report every owner stares at | **dbt model** | Good set-based SQL. Keep it SQL; add lineage and tests. Don't rewrite it into worse C#. |
| `usp_GetScheduleForDay` — repaints every 30s per workstation | **stays PL/pgSQL** | Not everything should leave the database. Hottest path in the product. |
| `usp_NightlyRecallAndClaims` — the 2 AM SQL Agent job | **.NET Worker Service** | Needs retries, observability, scheduling. A DB job agent gives you none of that. |

Being able to defend *why each one goes where it goes* — including the one that
stays put — is the point. It signals judgment rather than rewrite zeal, which is
what a cautious team is actually scanning for.

---

## Status

- [x] Legacy schema with 11 planted, harness-detectable defects — `docs/LANDMINES.md`
- [x] `usp_GetScheduleForDay` (the hot path / PL/pgSQL case)
- [x] Single-practice seed — synthetic PHI, every row tied to a landmine
- [x] Fleet spawn — 24 practices, 5 schema versions, 9 IANA zones, DST edge dates
- [x] Reproducible toolchain — `justfile`, fleet assertions, determinism check
- [x] Target PostgreSQL schema — landing / target / fenced-ground-truth split
- [x] **Timezone resolution, scored against ground truth** — the marquee bug, solved and measured
- [ ] Remaining three stored procs
- [ ] **Parity harness** — Testcontainers, both engines, row-level diff *(the centerpiece)*
- [ ] The four proc migrations
- [ ] Minimal API — Npgsql, Dapper reads / EF Core writes
- [ ] .NET Aspire orchestration
- [ ] AWS: CDK in C#, RDS PostgreSQL, ECS Fargate, DMS, OTel → CloudWatch
- [ ] Blazor WASM parity dashboard
- [ ] Cutover runbook — strangler fig, dual-write, shadow reads, rollback triggers

---

## Setup

**Not yet installed on this machine:** .NET SDK, AWS CLI, Node (needed later —
the CDK CLI is a Node app even when you author infra in C#). Docker is installed
but the daemon isn't running.

```bash
brew install --cask dotnet-sdk
open -a Docker
```

**Apple Silicon:** there is no ARM64 SQL Server image, and Azure SQL Edge was
retired 2025-09-30. The compose file pins `platform: linux/amd64` and runs under
Rosetta, which is Microsoft's own recommendation. Enable it first:

> Docker Desktop → Settings → General → **Use Rosetta for x86/amd64 emulation**

It's slow. At these data volumes that's irrelevant.

```bash
docker compose up -d
```

---

## Layout

```
legacy/           SQL Server 1997-era schema and stored procs, defects intact
  01_schema.sql
  02_seed.sql        one practice, synthetic, every row tied to a landmine
  03_seed_fleet.sql  the fleet: N single-tenant DBs that disagree with each other
  procs/
target/           the modern side
  01_schema.sql      landing_ / dentasys / harness, and why they are separate
  02_tz_resolve.sql  ZIP3 -> IANA, state fallback, DST edge classification
  03_transform.sql   landing_ -> dentasys, refusing to guess
  04_score.sql       scored against ground truth; asserts the safety property
  export_fleet.sql   SQL Server -> psql COPY stream
tests/
  assert_fleet.sql   invariants the spawned fleet must satisfy
docs/
  LANDMINES.md    the 11 planted defects, why each matters, how each is caught
docker-compose.yml
justfile
```

## Running it

Everything is a `just` recipe. The system under simulation is from 1997; the
toolchain around it is not, because a migration you cannot reproduce on demand
is a migration you cannot verify.

```bash
just up        # start both engines, block until healthy
just build     # 1997 schema + hot-path proc + sandbox seed + 24-practice fleet
just test      # assert the fleet matches the roster
just migrate   # target schema, export, transform, score against ground truth
just verify    # human-readable tour of what the fixtures demonstrate
```

`just check` is the gauntlet — `reset`, rebuild everything from the `.sql`
files, then assert. That is the command to run before pushing, and the only
claim of "it works" this repo will make.

```
just rebuild       drop every DENTASYS database and rebuild from source
just determinism   spawn the fleet twice, compare checksums
just query "..."   ad-hoc SQL, e.g. just query "SELECT TOP 5 * FROM APPT" DENTASYS_000417
just shell         interactive sqlcmd
just nuke          stop the containers and destroy their volumes
```

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

### Verification

`tests/assert_fleet.sql` is the floor beneath the parity harness: proof that the
fixtures are the fixtures this repo claims to produce. It checks trigger
presence against the roster, column counts against version *and* reseller
drift, `LEN_UNITS` against each practice's grid, the DST edge rows, and the
version histogram — collecting every failure rather than throwing on the first,
so a broken seed reports all its problems in one run.

It is a real test, not a smoke test: drop `TR_APPT_AUDIT` from one practice and
it fails with `000418 roster=Y actual=0` and exits nonzero.

A harness built on a seed nobody checks reports your seed's bugs as your
migration's bugs. That is the failure mode this exists to prevent.

### What the fleet is for

`DENTASYS_FLEET.dbo.FLEET_ROSTER` is the answer key. Its `IANA_TZ` column is
**ground truth that does not exist anywhere in the product** — the migration has
to infer the zone from `PRACTICE.ST_CD`, and the roster is what you score that
inference against. Seven states in the roster (FL, IN, TN, OR, ID, TX, KY) appear
twice, in two different zones, with the same `ST_CD`.

Three kinds of divergence are modelled, because each breaks something different:

| Divergence | Source | What it breaks |
|---|---|---|
| Schema version | vendor releases, adopted whenever | code written against the current schema meets a 2014 database |
| Reseller drift | columns/indexes/triggers added in prod | two practices on the *same* release with physically different tables |
| Geography | physics | `ST_CD` is not a timezone, and nothing in the DB says so |
