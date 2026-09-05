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
- [ ] Seed generator — synthetic PHI, multiple schema versions, DST edge dates
- [ ] Remaining three stored procs
- [ ] **Parity harness** — Testcontainers, both engines, row-level diff *(the centerpiece)*
- [ ] Target PostgreSQL schema + the four migrations
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
  procs/
docs/
  LANDMINES.md    the 11 planted defects, why each matters, how each is caught
docker-compose.yml
```
