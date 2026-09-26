-- ADR-018: capability codes become the Provider Adapter Contract v1 catalog (uppercase).
-- Existing rows are renamed in place. If a provider already has the new spelling as well, the old row is removed
-- first so the primary key cannot collide (the new row wins; a disabled new row stays disabled: fail closed).

DELETE FROM integration.provider_capabilities old
 USING integration.provider_capabilities cur
 WHERE old.provider_id = cur.provider_id
   AND old.capability_code LIKE 'supports\_%'
   AND cur.capability_code = upper(substr(old.capability_code, 10));

UPDATE integration.provider_capabilities
   SET capability_code = CASE capability_code
        WHEN 'supports_inquiry'         THEN 'INQUIRY'
        WHEN 'supports_payment'         THEN 'PAYMENT'
        WHEN 'supports_purchase'        THEN 'PURCHASE'
        WHEN 'supports_transfer'        THEN 'TRANSFER'
        WHEN 'supports_void'            THEN 'VOID'
        WHEN 'supports_status_check'    THEN 'STATUS_CHECK'
        WHEN 'supports_reversal'        THEN 'REVERSAL'
        WHEN 'supports_refund'          THEN 'REFUND'
        WHEN 'supports_advice'          THEN 'ADVICE'
        WHEN 'supports_callback'        THEN 'CALLBACK'
        WHEN 'supports_balance_check'   THEN 'BALANCE_CHECK'
        WHEN 'supports_reconciliation'  THEN 'RECONCILIATION'
        WHEN 'supports_settlement_file' THEN 'SETTLEMENT_FILE'
        ELSE capability_code
       END
 WHERE capability_code LIKE 'supports\_%';

-- ADR-018: the adapter contract reports PROTOCOL_ERROR (unparseable / unmappable provider reply). Expand only: every
-- existing value stays valid. PROTOCOL_ERROR never proves non-delivery (ADR-005).
ALTER TABLE core.transaction_attempts DROP CONSTRAINT ck_attempt_transport_status;

ALTER TABLE core.transaction_attempts ADD CONSTRAINT ck_attempt_transport_status CHECK (
  transport_status IN ('NOT_SENT','SENT','RESPONSE','TIMEOUT','CONNECTION_ERROR','PROTOCOL_ERROR')
);
