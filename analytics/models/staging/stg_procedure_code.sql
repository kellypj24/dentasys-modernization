select
    practice_id,
    upper(procedure_code) as procedure_code,
    description,
    is_active
from {{ source('dentasys', 'procedure_code') }}
