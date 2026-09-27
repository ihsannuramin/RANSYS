-- ADR-026: persist the business-facing provider response data (e.g. inquiry billAmount) of the latest resolved
-- attempt so a replay of an already-completed transaction can return it, instead of an empty object. This is a
-- projection distinct from `metadata` (product/provider extensions, ADR-018 overflow); expand only.
ALTER TABLE core.transaction_attempts ADD COLUMN response_data jsonb NULL;
