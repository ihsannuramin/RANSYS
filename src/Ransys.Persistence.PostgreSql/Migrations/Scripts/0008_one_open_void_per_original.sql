-- ADR-019: a VOID is a child transaction (transaction_type = 'VOID', original_transaction_id = original).
-- At most one VOID per original may be in progress, in doubt or succeeded; a FAILED void does not block a new one.
-- ChildTransactionService checks this under the original's row lock; this index is the database backstop
-- (same pattern as 0006 for reversals).

CREATE UNIQUE INDEX ux_transactions_one_open_void
    ON core.transactions (original_transaction_id)
    WHERE transaction_type = 'VOID' AND processing_status <> 'FAILED';
