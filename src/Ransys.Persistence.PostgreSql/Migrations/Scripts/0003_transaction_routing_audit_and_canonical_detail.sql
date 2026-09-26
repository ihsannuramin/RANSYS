-- ADR-013 (expand): columns required by the architecture but missing from DDL v1.1.
--
-- Routing audit (Architecture Spec §15): routing_rule_version, failover_count, failover_reason.
-- routing_decided_at keeps the RoutingDecision timestamp (Canonical Data Model §25).
--
-- canonical_detail holds first-class canonical objects that have no dedicated column
-- (customer, source, destination, merchant-facing references, external references;
-- Canonical Data Model §18-24). It is NOT the governed extension `metadata` column.

ALTER TABLE core.transactions
    ADD COLUMN routing_rule_version bigint NULL,
    ADD COLUMN failover_count integer NOT NULL DEFAULT 0,
    ADD COLUMN failover_reason varchar(64) NULL,
    ADD COLUMN routing_decided_at timestamptz NULL,
    ADD COLUMN canonical_detail jsonb NOT NULL DEFAULT '{}'::jsonb;

ALTER TABLE core.transactions
    ADD CONSTRAINT ck_tx_failover_count CHECK (failover_count >= 0),
    ADD CONSTRAINT ck_tx_routing_rule_version
      CHECK (routing_rule_version IS NULL OR routing_rule_version > 0),
    ADD CONSTRAINT ck_tx_failover_reason
      CHECK ((failover_count = 0 AND failover_reason IS NULL) OR (failover_count > 0 AND failover_reason IS NOT NULL));
