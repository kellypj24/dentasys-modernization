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
    @until [ "$(docker inspect --format '{{{{.State.Health.Status}}}}' {{LEGACY}} 2>/dev/null)" = "healthy" ]; do sleep 5; done
    @until [ "$(docker inspect --format '{{{{.State.Health.Status}}}}' {{TARGET}} 2>/dev/null)" = "healthy" ]; do sleep 2; done
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

# Create the legacy schema and the hot-path proc
schema: (run "legacy/01_schema.sql")
    @just run legacy/procs/usp_GetScheduleForDay.sql -d DENTASYS

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
     for p in $ids; do \
        docker exec -i {{LEGACY}} /opt/mssql-tools18/bin/sqlcmd \
            -S localhost -U sa -P '{{SA_PASS}}' -C -b -d "DENTASYS_$p" \
            -i /dev/stdin < legacy/procs/usp_GetScheduleForDay.sql; \
    done
    @echo "usp_GetScheduleForDay installed fleet-wide"

[private]
_practice-ids:
    @docker exec -i {{LEGACY}} /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P '{{SA_PASS}}' \
        -C -b -d DENTASYS_FLEET -h -1 -W -Q "SET NOCOUNT ON; SELECT RTRIM(PRAC_ID) FROM FLEET_ROSTER ORDER BY SEQ;"

# Build everything from whatever state the server is currently in
build: schema seed fleet install-procs

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

# The pre-push gauntlet: rebuild both sides from source, then assert. Run before pushing.
check: rebuild test migrate parity
