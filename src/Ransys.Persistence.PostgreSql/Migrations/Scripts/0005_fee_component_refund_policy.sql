-- ADR-014 (expand): refund policy per original fee component.
-- NONE (default) | PRO_RATA | FULL. The boolean `refundable` column is kept for compatibility
-- and is written as (refund_policy <> 'NONE').

ALTER TABLE core.transaction_fee_components
    ADD COLUMN refund_policy varchar(16) NOT NULL DEFAULT 'NONE';

ALTER TABLE core.transaction_fee_components
    ADD CONSTRAINT ck_fee_component_refund_policy
      CHECK (refund_policy IN ('NONE', 'PRO_RATA', 'FULL'));

-- Rows written before this migration only knew "refundable": treat them as fully refundable.
UPDATE core.transaction_fee_components SET refund_policy = 'FULL' WHERE refundable;

ALTER TABLE core.transaction_fee_components
    ADD CONSTRAINT ck_fee_component_refundable_matches_policy
      CHECK (refundable = (refund_policy <> 'NONE'));
