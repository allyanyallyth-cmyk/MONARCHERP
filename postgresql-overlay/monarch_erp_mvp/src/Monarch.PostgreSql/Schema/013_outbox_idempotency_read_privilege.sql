-- 013: INSERT ... ON CONFLICT inference and idempotent outbox checks require read access
-- to the conflict key columns. RLS continues to scope visible outbox rows to the signed tenant.
GRANT SELECT ON public.monarch_outbox TO monarch_api_runtime;
