-- ADR-012: a reversal is a child transaction (transaction_type = 'REVERSAL', original_transaction_id = original).
-- At most one reversal per original may be in progress, in doubt or succeeded; a FAILED reversal does not block
-- a new one. ReversalService checks this under the original's row lock; this index is the database backstop.

CREATE UNIQUE INDEX ux_transactions_one_open_reversal
    ON core.transactions (original_transaction_id)
    WHERE transaction_type = 'REVERSAL' AND processing_status <> 'FAILED';
