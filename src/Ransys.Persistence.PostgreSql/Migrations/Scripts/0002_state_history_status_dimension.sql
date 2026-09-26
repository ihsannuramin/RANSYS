-- ADR-004 (expand): each state history row records which status dimension changed
-- (processing, financial, reconciliation, settlement). Nullable for backward compatibility:
-- rows written before this migration are interpreted as PROCESSING.

ALTER TABLE core.transaction_state_history
    ADD COLUMN status_dimension varchar(32) NULL;

ALTER TABLE core.transaction_state_history
    ADD CONSTRAINT ck_state_history_dimension CHECK (
      status_dimension IS NULL
      OR status_dimension IN ('PROCESSING','FINANCIAL','RECONCILIATION','SETTLEMENT')
    );
