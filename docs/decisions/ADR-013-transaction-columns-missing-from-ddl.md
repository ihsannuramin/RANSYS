# ADR-013 — Transaction Columns Missing from DDL v1.1 (Routing Audit and Canonical Detail)

**Status:** Proposed — implemented as recommended (migration `0003`); requires architecture review

## Context
Mapping the Transaction aggregate to `core.transactions` (Milestone 4) found data that the architecture requires to be stored but DDL v1.1 has no column for.

## Existing RANSYS rule
- Architecture Spec §15 / PRD: routing audit **must** store `routing_rule_version`, `initial_selected_provider`, `actual_provider`, `failover_count`, `failover_reason`. ERD v1.0 had `routing_rule_version`.
- Canonical Data Model §20–24: customer, source, destination and references (merchant reference, STAN, RRN, external references) are **first-class canonical objects**, not metadata. §39 forbids putting canonicalized fields into `metadata`.
- ERD v1.1 §36 mentions a "core transaction canonical/customer field" (varchar(256)) that the reference DDL does not define.

## Technical issue
DDL v1.1 `core.transactions` only has `initial_selected_provider_id` and `actual_provider_id`, and only the governed `metadata` jsonb. Without new columns, the routing audit would be lost and canonical objects would have to go into `metadata`, which violates §39.

## Options
1. Store everything in `metadata` under a reserved namespace.
2. Add a column per field (about 15 columns).
3. Add routing audit columns plus one `canonical_detail jsonb` column for the canonical sub-objects.

## Recommended option
Option 3, as an **expand** migration (additive, backward compatible):

| Column | Type | Purpose |
|---|---|---|
| `routing_rule_version` | `bigint NULL`, `> 0` | Routing rule version (`RoutingDecision.RuleVersion`) |
| `failover_count` | `integer NOT NULL DEFAULT 0`, `>= 0` | Number of pre-send failovers |
| `failover_reason` | `varchar(64) NULL` | Reason code of the latest failover; required iff `failover_count > 0` |
| `routing_decided_at` | `timestamptz NULL` | `RoutingDecision.DecisionTimestamp` |
| `canonical_detail` | `jsonb NOT NULL DEFAULT '{}'` | Customer, source, destination, merchant reference, STAN, RRN, provider references, external references |

`canonical_detail` is written once at creation and re-validated through domain factories on load. It is not indexed (ERD v1.1 §36: no broad indexes on Core); Backoffice builds searchable projections.

## Consequences
- Routing audit is complete and queryable.
- Canonical objects stay out of the governed `metadata` column.
- Customer data in `canonical_detail` still needs the masking/tokenization decision (ERD v1.1 §52, TODO).
- If the architecture review prefers dedicated columns, a later migrate/contract step can move data out of `canonical_detail` without changing the domain model.
