-- ADR-027: persist the latest provider evidence reported for a transaction from any source (sync response, callback,
-- status check, advice, reconciliation), so a later/final async report is never silently dropped once an earlier
-- attempt outcome is already immutable (ADR-005). Always-overwritable projection, distinct from
-- `transaction_attempts` (immutable per-attempt history) and from `response_data` (ADR-026, per-attempt); expand only.
ALTER TABLE core.transactions
    ADD COLUMN latest_result_provider_reference varchar(128) NULL,
    ADD COLUMN latest_result_provider_stan varchar(32) NULL,
    ADD COLUMN latest_result_provider_rrn varchar(64) NULL,
    ADD COLUMN latest_result_data jsonb NULL,
    ADD COLUMN latest_result_source varchar(32) NULL,
    ADD COLUMN latest_result_recorded_at timestamptz NULL;
