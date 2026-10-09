# infra

Terraform, not Bicep: the long-run target may be hybrid or AWS, and the provider
is the only part that should change. `azure/` is the first target; an `aws/`
sibling would expose the same outputs (`pg_host`, `pg_user`, `pg_password`).

## Cost rules

The lab must never produce a charge. Azure's free account guarantees that only
while the subscription's **spending limit is On**:

| State | Can the card be charged |
|---|---|
| First 30 days, $200 credit, spending limit On | No. Services are disabled when credit runs out. |
| Day 30, not upgraded | No. Subscription is disabled; resources are deleted later. |
| Upgraded to pay-as-you-go | Yes. The limit is gone; budgets alert but do not stop spend. When the 12-month free allowance ends, anything still running bills at list price. |

So:

1. **Never upgrade.** `just azure-up` reads `subscriptionPolicies.spendingLimit`
   and refuses to apply unless it is `On`.
2. **Work locally first.** `just infra-check` runs `terraform test` against
   mocked providers. Create the Azure account only when there is something
   ready to deploy, so the 30 days are spent deploying.
3. **One resource group, one teardown.** Everything lives in a single tagged
   group; `just azure-down` destroys it, and `just azure-leftovers` lists any
   group tagged `project=dentasys-modernization` that survived.
4. **Free-tier SKUs are asserted, not remembered.** `azure/tests/free_tier.tftest.hcl`
   fails if Postgres leaves B1ms / 32 GB, or turns on auto-grow, HA or
   geo-redundant backup. `just check` runs it.
5. **Cancel the subscription when done**, rather than leaving it to lapse.

Services that bill without an obvious resource, to keep out of this group
unless their cost is checked first: Container Registry (no free tier), a Log
Analytics workspace without a daily cap, public IPs, and backup storage beyond
the server's own allowance.

## What is deployed

| Resource | Sizing | Why it stays free |
|---|---|---|
| Postgres Flexible Server | B1ms, 32 GB, no auto-grow, HA or geo-backup | inside the 12-month allowance |
| Container App `api` | 0.25 vCPU, 0–1 replicas, ingress = operator IP only | scales to zero; nobody else can wake it |
| Container Apps job `outbox` | 0.25 vCPU, cron every 5 min, `--once` | runs seconds per hour, not all month |
| Container Apps environment | no Log Analytics workspace | logs go nowhere, so nothing is ingested |

Images come from a public GHCR package, not Azure Container Registry, which has
no free tier. Leave `api_image` / `worker_image` unset to deploy the database only.

## Use

```bash
just container-smoke   # local: both images against the compose Postgres
brew install azure-cli
az login
just azure-up          # guard, infra-check, then terraform apply
just azure-down        # destroy, then list leftovers
```

Terraform state is local and gitignored. It holds the generated Postgres
password; `terraform -chdir=infra/azure output -raw pg_password` reads it.
