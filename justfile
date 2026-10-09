# DENTASYS modernization lab
#
# The DATA MODEL under simulation is from 1997; the engine it runs on is current,
# and so is the toolchain around it. The whole argument of this repo is that you
# cannot migrate what you cannot reproduce, and reproducing it has to be one
# command.

# bash rather than sh: several recipes use process substitution and pipefail.
set shell := ["bash", "-eo", "pipefail", "-c"]

SA_PASS := "Dentasys!1997"
LEGACY  := "dentasys-legacy"
TARGET  := "dentasys-target"

# List available commands
default:
    @just --list

# ---------------------------------------------------------------------------
# containers
# ---------------------------------------------------------------------------

# Start both engines and block until the legacy server accepts connections
up: && wait
    docker compose up -d

# Block until SQL Server reports healthy (slow on Apple Silicon -- Rosetta)
wait:
    @echo "waiting for {{LEGACY}} to report healthy (first boot under Rosetta takes ~90s)..."
    @until [ "$(docker inspect --format '{{{{.State.Health.Status}}' {{LEGACY}} 2>/dev/null)" = "healthy" ]; do sleep 5; done
    @until [ "$(docker inspect --format '{{{{.State.Health.Status}}' {{TARGET}} 2>/dev/null)" = "healthy" ]; do sleep 2; done
    @echo "both engines healthy"

# Stop the containers, keeping their volumes
down:
    docker compose down

# Stop the containers and destroy their volumes -- full cold start next time
nuke:
    docker compose down -v

# Show container status
ps:
    docker compose ps

# Tail the legacy server log
logs:
    docker compose logs -f {{LEGACY}}

# ---------------------------------------------------------------------------
# legacy database
# ---------------------------------------------------------------------------

# Run a .sql file against the legacy server (-b: any SQL error exits nonzero)
[private]
run FILE *ARGS:
    @docker exec -i {{LEGACY}} /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P '{{SA_PASS}}' -C -b {{ARGS}} -i /dev/stdin < {{FILE}}

# Run an ad-hoc query, e.g. just query "SELECT TOP 5 * FROM APPT" DENTASYS_000417
query SQL DB="DENTASYS_FLEET":
    @docker exec -i {{LEGACY}} /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P '{{SA_PASS}}' -C -b -W -s '|' -d {{DB}} -Q "{{SQL}}"

# Interactive sqlcmd session against the legacy server
shell DB="DENTASYS_FLEET":
    docker exec -it {{LEGACY}} /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P '{{SA_PASS}}' -C -d {{DB}}

# Interactive psql session against the target server
target-shell:
    docker exec -it {{TARGET}} psql -U dentasys -d dentasys

# Create the legacy schema and the procs
schema: (run "legacy/01_schema.sql")
    @for f in legacy/procs/*.sql; do just run "$f" -d DENTASYS; done

# Seed the single-practice sandbox (DENTASYS)
seed: (run "legacy/02_seed.sql")

# Spawn the fleet: DENTASYS_FLEET plus one database per practice
fleet: (run "legacy/03_seed_fleet.sql")

# Install the product's stored procs into every practice database. The parity
# harness runs them as the ORACLE it checks the new C# against -- the app itself
# never calls them.
install-procs:
    @ids="$(just _practice-ids)"; \
     [ -n "$ids" ] || { echo "no practices found -- is the fleet spawned?" >&2; exit 1; }; \
     for p in $ids; do for f in legacy/procs/*.sql; do \
        docker exec -i {{LEGACY}} /opt/mssql-tools18/bin/sqlcmd \
            -S localhost -U sa -P '{{SA_PASS}}' -C -b -d "DENTASYS_$p" \
            -i /dev/stdin < "$f"; \
    done; done
    @echo "procs installed fleet-wide: $(cd legacy/procs && ls *.sql | sed 's/.sql//' | tr '\n' ' ')"

[private]
_practice-ids:
    @docker exec -i {{LEGACY}} /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '{{SA_PASS}}' \
        -C -b -d DENTASYS_FLEET -h -1 -W -Q "SET NOCOUNT ON; SELECT RTRIM(PRAC_ID) FROM FLEET_ROSTER ORDER BY SEQ;"

# The notes service's store, plus release 07.04.00 (CLINICAL_NOTE) at the two
# practices that have installed it. The other 22 have not had their upgrade window yet.
notes-setup:
    @just run legacy/notes/01_notes_store.sql
    @for p in 001204 001505; do just run legacy/upgrades/07.04.00_clinical_note.sql -d "DENTASYS_$p"; done
    @echo "DENTASYS_NOTES ready; CLINICAL_NOTE at 001204 001505"

# Build everything from whatever state the server is currently in
build: schema seed fleet install-procs notes-setup

# Drop every DENTASYS database. Destructive, and only ever touches DENTASYS*.
reset:
    @echo "dropping all DENTASYS* databases..."
    @docker exec -i {{LEGACY}} /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '{{SA_PASS}}' -C -b -Q " \
        DECLARE @n SYSNAME, @s NVARCHAR(MAX); \
        DECLARE d CURSOR LOCAL FAST_FORWARD FOR \
          SELECT name FROM sys.databases WHERE name = 'DENTASYS' OR name LIKE 'DENTASYS[_]%'; \
        OPEN d; FETCH NEXT FROM d INTO @n; \
        WHILE @@FETCH_STATUS = 0 BEGIN \
          SET @s = N'ALTER DATABASE '+QUOTENAME(@n)+N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE '+QUOTENAME(@n)+N';'; \
          EXEC sp_executesql @s; FETCH NEXT FROM d INTO @n; END \
        CLOSE d; DEALLOCATE d;"

# Drop everything and rebuild from the .sql files. The honest starting point.
rebuild: reset build

# ---------------------------------------------------------------------------
# target database
# ---------------------------------------------------------------------------

# Run a .sql file against the target Postgres server
[private]
prun FILE:
    @docker exec -i {{TARGET}} psql -U dentasys -d dentasys -v ON_ERROR_STOP=1 -q -f - < {{FILE}}

# Run an ad-hoc query against the target, e.g. just pquery "SELECT * FROM dentasys.practice"
pquery SQL:
    @docker exec -i {{TARGET}} psql -U dentasys -d dentasys -v ON_ERROR_STOP=1 -c "{{SQL}}"

# Create the target schema (landing_, dentasys, harness) and the tz resolver
target-schema: (prun "target/01_schema.sql") (prun "target/02_tz_resolve.sql") (prun "target/05_write_model.sql")

# Move the fleet from SQL Server into landing_, plus ground truth into harness
export:
    @docker exec -i {{LEGACY}} /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P '{{SA_PASS}}' -C -b -d DENTASYS_FLEET -h -1 -W \
        -i /dev/stdin < target/export_fleet.sql \
      | docker exec -i {{TARGET}} psql -U dentasys -d dentasys -v ON_ERROR_STOP=1 -q

# landing_ -> dentasys, resolving timezones and refusing to guess at DST edges
transform: (prun "target/03_transform.sql")

# Score the timezone inference against ground truth, and assert the safety property
score: (prun "target/04_score.sql")

# The whole modernization path: target schema, export, transform, score
migrate: target-schema export transform score

# ---------------------------------------------------------------------------
# analytics (dbt on DuckDB)
# ---------------------------------------------------------------------------

# Copy the ledger out of PostgreSQL into DuckDB and build the report models + tests
analytics:
    @cd analytics && uv run --quiet dbt build --profiles-dir . --quiet

# Production/collection report from DuckDB, e.g. just report 000418 2026-03-01 2026-03-31
report PRACTICE FROM TO:
    @duckdb -readonly analytics/dentasys.duckdb "SELECT provider_code, sum(production) AS production, \
        sum(adjustments) AS adjustments, sum(net_production) AS net_production, sum(collections) AS collections \
        FROM fct_production_collection_daily WHERE practice_id = '{{PRACTICE}}' \
        AND entry_date BETWEEN '{{FROM}}' AND '{{TO}}' GROUP BY ALL ORDER BY ALL"

# ---------------------------------------------------------------------------
# notetaker (docs/NOTETAKER.md)
# ---------------------------------------------------------------------------

# Draft all synthetic visits with a local model and score them, e.g. just notetaker-eval gemma3:4b --pause 10
# One run at a time; stop the containers first (`just down`) to give the model the memory.
notetaker-eval MODEL *ARGS:
    @ollama list | grep "^{{MODEL}}" > /dev/null || { echo "{{MODEL}} is not pulled -- ollama pull {{MODEL}}" >&2; exit 1; }
    @dotnet run --project dotnet/src/Dentasys.Notetaker.Eval -- --drafter ollama --model {{MODEL}} {{ARGS}}

# Re-score a saved run against the current visits and scorer. No model, no GPU.
notetaker-rescore RESULT:
    @dotnet run --project dotnet/src/Dentasys.Notetaker.Eval -- --rescore {{RESULT}}

# Same, with Claude via the Anthropic API (synthetic visits only; costs money). Needs ANTHROPIC_API_KEY.
notetaker-eval-claude *ARGS:
    @[ -n "${ANTHROPIC_API_KEY:-}" ] || { echo "ANTHROPIC_API_KEY is not set" >&2; exit 1; }
    @dotnet run --project dotnet/src/Dentasys.Notetaker.Eval -- --drafter claude {{ARGS}}

# A few visits through the whole notes service with a real local model and the
# cloud unreachable on purpose: capture -> local fallback -> sign -> CLINICAL_NOTE.
# Paced and small (3 visits, ~20 s of GPU); stop any other model run first.
notes-smoke MODEL="gemma3:4b" *ARGS:
    @ollama list | grep "^{{MODEL}}" > /dev/null || { echo "{{MODEL}} is not pulled -- ollama pull {{MODEL}}" >&2; exit 1; }
    @! pgrep -f '[D]entasys.Notetaker.Eval|[D]entasys.Notes.Smoke' > /dev/null || { echo "another model run is in progress" >&2; exit 1; }
    @dotnet run --project dotnet/src/Dentasys.Notes.Smoke -- --model {{MODEL}} {{ARGS}}

# Lab-only token signing key. Like the SA password above: fine for a lab on one
# laptop, never a deployment, which authenticates against Notes:Auth:Authority.
NOTES_LAB_KEY := "dentasys-lab-only-signing-key-0123456789"

# The notes service: API on :5181 plus the job and chart workers. CLOUD/LOCAL are
# drafter specs (claude[:model] | ollama:model | none). The default drafts with a
# local model only, as if the cloud were unreachable.
notes-api CLOUD="none" LOCAL="ollama:gemma3:4b":
    @cd dotnet && ASPNETCORE_URLS=http://localhost:5181 Notes__CloudDrafter={{CLOUD}} Notes__LocalDrafter={{LOCAL}} \
        Notes__Auth__LabSigningKey='{{NOTES_LAB_KEY}}' dotnet run --project src/Dentasys.Notes.Api

# Mint a lab token, e.g. just notes-token clinician 001204 dr.lee DDS1 | just notes-token capture_agent 001204 ws-op1
notes-token ROLE PRACTICE SUB PROV="":
    @cd dotnet && Notes__Auth__LabSigningKey='{{NOTES_LAB_KEY}}' dotnet run --project src/Dentasys.Notes.Api -- \
        issue-token --role {{ROLE}} --practice {{PRACTICE}} --sub {{SUB}} --prov '{{PROV}}'

# Workstation side: record a synthetic visit into the encrypted spool, then upload it
# with that workstation's own token
capture VISIT PRACTICE="001204" PATIENT="41701":
    @cd dotnet && dotnet run --project src/Dentasys.CaptureAgent -- record {{VISIT}} --practice {{PRACTICE}} --patient {{PATIENT}}
    @cd dotnet && DENTASYS_AGENT_TOKEN="$(just notes-token capture_agent {{PRACTICE}} ws-{{PRACTICE}}-op1)" \
        dotnet run --project src/Dentasys.CaptureAgent -- drain

# Review screen as a signed-in clinician, e.g. just notes-review dr.lee DDS1 001204 queue 001204
notes-review USER PROV PRACTICE *ARGS:
    @cd dotnet && DENTASYS_NOTES_URL=http://localhost:5181/ \
        DENTASYS_NOTES_TOKEN="$(just notes-token clinician {{PRACTICE}} {{USER}} {{PROV}})" \
        dotnet run --project src/Dentasys.App -- notes {{ARGS}}

# Notes service against DENTASYS_NOTES: lifecycle, disconnects, resync, chart outbox.
notes-test:
    @cd dotnet && dotnet test tests/Dentasys.Notes.Tests --nologo -v q

# Scorer and visit-fixture tests. No model, no database; part of `just check`.
notetaker-test:
    @cd dotnet && dotnet test tests/Dentasys.Notetaker.Tests --nologo -v q

# ---------------------------------------------------------------------------
# the application
# ---------------------------------------------------------------------------

# Build the .NET solution
dotnet-build:
    @cd dotnet && dotnet build

# Run the API gate (the only process that can reach PostgreSQL)
api:
    @cd dotnet && ASPNETCORE_URLS=http://localhost:5179 dotnet run --project src/Dentasys.Api

# Show a practice's day, e.g. just show 000417 2026-03-08 --modern
show PRACTICE DATE *FLAGS:
    @cd dotnet && dotnet run --project src/Dentasys.App -- {{PRACTICE}} {{DATE}} {{FLAGS}}

# Drain the outbox -- the worker that replaces the 2 AM SQL Agent job
worker:
    @cd dotnet && dotnet run --project src/Dentasys.Worker

# Book an appointment through the API. Try 001010 on 2026-03-08 at 02:30.
book PRACTICE PATIENT DATE TIME UNITS="6" OPER="OP1" PROV="DDS1":
    @curl -s -X POST http://localhost:5179/practices/{{PRACTICE}}/appointments \
        -H 'Content-Type: application/json' \
        -d '{"actorId":"frontdesk1","patientId":{{PATIENT}},"operatoryCode":"{{OPER}}",\
             "providerCode":"{{PROV}}","date":"{{DATE}}","time":"{{TIME}}","lengthUnits":{{UNITS}}}' \
      | python3 -m json.tool

# Mark an appointment completed -- the write that used to fire a hidden trigger
complete PRACTICE APPT:
    @curl -s -X POST http://localhost:5179/practices/{{PRACTICE}}/appointments/{{APPT}}/complete \
        -H 'Content-Type: application/json' -d '{"actorId":"frontdesk1"}' | python3 -m json.tool

# What is sitting in the outbox
outbox:
    @just pquery "SELECT event_type, practice_id, occurred_at, \
        CASE WHEN published_at IS NULL THEN 'pending' ELSE 'published' END AS state, attempts \
        FROM dentasys.outbox ORDER BY occurred_at DESC LIMIT 20"

# Render the same day down both stacks, side by side, and diff them
demo PRACTICE="000417" DATE="2026-03-08":
    @echo "================ LEGACY: fat client -> SQL Server ================"
    @just show {{PRACTICE}} {{DATE}} --legacy
    @echo "================ MODERN: client -> API gate -> PostgreSQL ========"
    @just show {{PRACTICE}} {{DATE}} --modern
    @echo "================ diff of the rendered book ======================="
    @diff <(just show {{PRACTICE}} {{DATE}} --legacy | tail -n +4) \
          <(just show {{PRACTICE}} {{DATE}} --modern | tail -n +4) \
      && echo "  identical" || echo "  ^ the two stacks disagree"

# Record that a practice's appointment grid has been discovered (the .INI answer)
set-grid PRACTICE GRID SOURCE="workstation .INI":
    @just pquery "INSERT INTO dentasys.practice_config (practice_id, appointment_grid_minutes, source, collected_at) \
        VALUES ('{{PRACTICE}}', {{GRID}}, '{{SOURCE}}', CURRENT_DATE) \
        ON CONFLICT (practice_id) DO UPDATE SET appointment_grid_minutes = EXCLUDED.appointment_grid_minutes, \
        source = EXCLUDED.source, collected_at = CURRENT_DATE"

# ---------------------------------------------------------------------------
# azure (Terraform; see infra/README.md for the cost rules)
# ---------------------------------------------------------------------------

AZ_DIR := "infra/azure"

# fmt, validate and the free-tier test suite. Mocked providers: no account, no cost.
infra-check:
    @cd {{AZ_DIR}} && terraform fmt -check -recursive
    @cd {{AZ_DIR}} && terraform init -backend=false -input=false > /dev/null
    @cd {{AZ_DIR}} && terraform validate -no-color
    @cd {{AZ_DIR}} && terraform test -no-color

# Build the API and worker images for linux/amd64, the only platform Container Apps runs
images:
    @cd dotnet && docker build --platform linux/amd64 --target api    -t dentasys-api:local -q .
    @cd dotnet && docker build --platform linux/amd64 --target worker -t dentasys-worker:local -q .

# Run the images against the local target: book through the API container, then
# drain with the worker exactly as the Azure job will (--once)
container-smoke: images
    #!/usr/bin/env bash
    set -euo pipefail
    conn='Host=host.docker.internal;Port=15432;Database=dentasys;Username=dentasys;Password=dentasys'
    docker run -d --rm --name dentasys-api-smoke -p 5179:8080 \
        -e DENTASYS_TARGET_CONNECTION="$conn" dentasys-api:local > /dev/null
    appt=""
    # Remove the booking on the way out: left in place, the parity harness would
    # (correctly) report it as a row the legacy stack does not have.
    cleanup() {
        docker stop dentasys-api-smoke > /dev/null
        [ -z "$appt" ] || just smoke-cleanup "$appt"
    }
    trap cleanup EXIT
    for _ in $(seq 30); do curl -fs localhost:5179/health > /dev/null && break; sleep 1; done
    appt="$(just book 000417 41701 2026-03-10 10:00 | python3 -c 'import json,sys; print(json.load(sys.stdin)["appointmentId"])')"
    docker run --rm -e DENTASYS_TARGET_CONNECTION="$conn" dentasys-worker:local --once
    pending="$(docker exec {{TARGET}} psql -U dentasys -d dentasys -tAc \
        "SELECT count(*) FROM dentasys.outbox WHERE published_at IS NULL")"
    [ "$pending" = "0" ] || { echo "outbox still has $pending pending event(s)" >&2; exit 1; }
    echo "container smoke: outbox drained"

# Delete one appointment and everything its booking wrote (audit, outbox)
[private]
smoke-cleanup APPT:
    @docker exec -i {{TARGET}} psql -U dentasys -d dentasys -v ON_ERROR_STOP=1 -q -c " \
        DELETE FROM dentasys.outbox WHERE payload->>'AppointmentId' = '{{APPT}}'; \
        DELETE FROM dentasys.appointment_audit WHERE appointment_id = {{APPT}}; \
        DELETE FROM dentasys.appointment WHERE appointment_id = {{APPT}};"

# Refuse to continue unless the subscription still has its spending limit.
# Upgrading to pay-as-you-go turns it off, and with it the guarantee of zero charges.
[private]
azure-guard:
    @az account show > /dev/null 2>&1 || { echo "not logged in -- run: az login" >&2; exit 1; }
    @limit="$(az rest --method get \
        --url "https://management.azure.com/subscriptions/$(az account show --query id -o tsv)?api-version=2022-12-01" \
        --query subscriptionPolicies.spendingLimit -o tsv)"; \
     [ "$limit" = "On" ] || { echo "spending limit is '$limit', not 'On' -- refusing. See infra/README.md." >&2; exit 1; }
    @echo "spending limit: On"

# Create the lab. Firewall opens to this machine's public IP only; expires_on is today + 30.
azure-up: azure-guard infra-check
    @cd {{AZ_DIR}} && terraform init -input=false > /dev/null
    @cd {{AZ_DIR}} && ARM_SUBSCRIPTION_ID="$(az account show --query id -o tsv)" terraform apply \
        -var "operator_ip=$(curl -fsS https://api.ipify.org)" \
        -var "expires_on=$(python3 -c 'import datetime as d; print(d.date.today() + d.timedelta(days=30))')"

# Destroy the lab, then list anything tagged for this project that survived
azure-down:
    @cd {{AZ_DIR}} && ARM_SUBSCRIPTION_ID="$(az account show --query id -o tsv)" terraform destroy \
        -var "operator_ip=0.0.0.0" -var "expires_on=1970-01-01"
    @just azure-leftovers

# Every resource group tagged for this project. Should be empty when you are done.
azure-leftovers:
    @az group list --query "[?tags.project=='dentasys-modernization'].{name:name, expires_on:tags.expires_on}" -o table

# ---------------------------------------------------------------------------
# verification
# ---------------------------------------------------------------------------

# Assert the fleet matches what the roster says it should be
test: (run "tests/assert_fleet.sql")

# Parity harness: proc-vs-C# (is the logic faithful?) and legacy-vs-modern stack
parity: dotnet-build
    @cd dotnet && dotnet test --no-build --logger "console;verbosity=detailed" \
        | grep -E "parity:|Expected|Blocked|Regression|Omitted|claimed by|-> |unexplained|Passed!|Failed!" || true
    @cd dotnet && dotnet test --no-build --nologo -v q

# Prove the seed is deterministic: spawn twice, compare checksums
determinism:
    @echo "checksum after first spawn:"
    @just _checksum
    @just fleet > /dev/null
    @echo "checksum after respawn:"
    @just _checksum
    @echo "(the two values above must be identical)"

[private]
_checksum:
    @just query "DECLARE @p CHAR(6), @s NVARCHAR(MAX); \
        DECLARE @t TABLE (V BIGINT); \
        DECLARE k CURSOR LOCAL FAST_FORWARD FOR SELECT PRAC_ID FROM FLEET_ROSTER ORDER BY SEQ; \
        OPEN k; FETCH NEXT FROM k INTO @p; \
        WHILE @@FETCH_STATUS = 0 BEGIN \
          SET @s = N'SELECT CAST(CHECKSUM_AGG(CHECKSUM(APPT_ID, APPT_DT, APPT_TM, LEN_UNITS, PAT_ID)) AS BIGINT) \
                     FROM ' + QUOTENAME(N'DENTASYS_' + @p) + N'.dbo.APPT;'; \
          INSERT INTO @t EXEC sp_executesql @s; FETCH NEXT FROM k INTO @p; END \
        CLOSE k; DEALLOCATE k; \
        SELECT SUM(V) AS FLEET_CHECKSUM FROM @t;"

# Human-readable landmine report -- what the fixtures actually demonstrate
verify:
    @echo ""
    @echo "#6  the same 60-minute appointment, on two different grids"
    @just query "SELECT '000417 10-min grid' AS PRACTICE, LEN_UNITS FROM DENTASYS_000417.dbo.APPT WHERE APPT_ID % 1000 = 1 \
                 UNION ALL SELECT '000418 15-min grid', LEN_UNITS FROM DENTASYS_000418.dbo.APPT WHERE APPT_ID % 1000 = 1"
    @echo ""
    @echo "#6  the 45-minute appointment truncating on a 10-minute grid"
    @just query "SELECT '000417 10-min' AS PRACTICE, LEN_UNITS, LEN_UNITS * 10 AS IMPLIED_MIN FROM DENTASYS_000417.dbo.APPT WHERE APPT_ID % 1000 = 7 \
                 UNION ALL SELECT '000418 15-min', LEN_UNITS, LEN_UNITS * 15 FROM DENTASYS_000418.dbo.APPT WHERE APPT_ID % 1000 = 7"
    @echo ""
    @echo "#10 DEL_FLG carries a different truth value depending on client version"
    @just query "SELECT '06.04.02' AS VER, '[' + ISNULL(DEL_FLG, '?') + ']' AS DEL_FLG, COUNT(*) AS N FROM DENTASYS_000605.dbo.APPT GROUP BY DEL_FLG \
                 UNION ALL SELECT '07.00.09', '[' + ISNULL(DEL_FLG, '?') + ']', COUNT(*) FROM DENTASYS_000702.dbo.APPT GROUP BY DEL_FLG \
                 UNION ALL SELECT '07.02.11', '[' + ISNULL(DEL_FLG, '?') + ']', COUNT(*) FROM DENTASYS_000417.dbo.APPT GROUP BY DEL_FLG"
    @echo ""
    @echo "#1  the same row, in a DST zone and a non-DST zone"
    @just query "SELECT 'America/Phoenix  (no DST)' AS ZONE, APPT_DT, APPT_TM FROM DENTASYS_000417.dbo.APPT WHERE APPT_ID % 1000 IN (4,8) \
                 UNION ALL SELECT 'America/New_York (DST)', APPT_DT, APPT_TM FROM DENTASYS_001010.dbo.APPT WHERE APPT_ID % 1000 IN (4,8)"
    @echo ""
    @echo "#1  states where ST_CD -- the only geography in the schema -- is not enough"
    @just query "SELECT ST_CD, COUNT(DISTINCT IANA_TZ) AS ZONES FROM FLEET_ROSTER GROUP BY ST_CD HAVING COUNT(DISTINCT IANA_TZ) > 1 ORDER BY ST_CD"
    @echo ""

# The pre-push gauntlet: infra tests, rebuild both sides from source, build analytics, then assert. Run before pushing.
check: infra-check notetaker-test rebuild test migrate analytics parity
