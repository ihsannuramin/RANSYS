-- ADR-005 (expand): explicit marker that an attempt's outcome was actually recorded.
--
-- An attempt row is written BEFORE the provider call and stored pessimistically as possibly sent
-- (request_sent = true, transport_status = 'SENT'). outcome_recorded_at stays NULL until Transaction Core
-- records the adapter-reported outcome. A NULL marker after a restart means "result unknown":
-- the transaction goes IN_DOUBT and is never failed over.

ALTER TABLE core.transaction_attempts
    ADD COLUMN outcome_recorded_at timestamptz NULL;

-- Recovery scan for attempts without outcome (small partial index; table is new in Phase 1).
CREATE INDEX ix_attempts_outcome_pending
    ON core.transaction_attempts (created_at)
    WHERE outcome_recorded_at IS NULL;
