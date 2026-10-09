select *
from {{ ref('fct_production_collection_daily') }}
where net_production <> production + adjustments
