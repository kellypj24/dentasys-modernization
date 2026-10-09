-- Codes are upper-cased here, once. The legacy server joins them
-- case-insensitively and DuckDB does not (LANDMINE #3); normalizing at the
-- boundary keeps every join downstream an ordinary equality.
select
    practice_id,
    ledger_entry_id,
    patient_id,
    entry_date,
    entry_type,
    upper(procedure_code) as procedure_code,
    upper(provider_code)  as provider_code,
    amount::decimal(12, 2)      as amount,
    paid_amount::decimal(12, 2) as paid_amount,
    is_deleted
from {{ source('dentasys', 'ledger_entry') }}
