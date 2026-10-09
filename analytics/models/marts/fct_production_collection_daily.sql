-- One row per practice, provider and day. Any date range the legacy report
-- accepts is a sum over this table.
--
-- Reproduces usp_RptProductionCollection, including its quirks:
--   * production counts a charge only if its code exists in the code table
--   * collections are P and I rows only; PAID_AMT on a charge row ("paid same
--     day") is not counted
--   * soft-deleted rows are excluded
-- Money is decimal, so totals differ from the proc's FLOAT sums by
-- representation error (LANDMINE #2). The parity harness bounds that.
with ledger as (
    select l.*, c.procedure_code is not null as has_known_code
    from {{ ref('stg_ledger_entry') }} l
    left join {{ ref('stg_procedure_code') }} c
        on  c.practice_id    = l.practice_id
        and c.procedure_code = l.procedure_code
    where not l.is_deleted
)

select
    practice_id,
    provider_code,
    entry_date,
    sum(case when entry_type = 'C' and has_known_code then amount else 0 end)::decimal(14, 2)
        as production,
    sum(case when entry_type = 'A' then amount else 0 end)::decimal(14, 2)
        as adjustments,
    sum(case when entry_type = 'C' and has_known_code then amount
             when entry_type = 'A' then amount
             else 0 end)::decimal(14, 2)
        as net_production,
    sum(case when entry_type in ('P', 'I') then paid_amount else 0 end)::decimal(14, 2)
        as collections
from ledger
group by practice_id, provider_code, entry_date
