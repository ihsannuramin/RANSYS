# ADR-015 — Semantic Accounts Are Authoritative; GL Codes Are Configurable Mappings

**Status:** Accepted — product owner decision, 2026-09-27

## Context
ADR-007 introduced placeholder ledger account codes and expected final codes from Finance.

## Decision
- RANSYS **semantic account types** (`MERCHANT_AVAILABLE`, `MERCHANT_RESERVED`, `PROVIDER_PAYABLE`, `PROVIDER_RECEIVABLE`, `RANSYS_FEE_REVENUE`, `TAX_PAYABLE`, `CASH_CLEARING`, `ADJUSTMENT_CLEARING`, …) and the internal account codes built from them (ADR-007) are **authoritative** for the ledger.
- External Finance **GL account codes are configurable mappings** from semantic account type (and currency definition where needed) to GL code. They are **never hardcoded** in domain or ledger logic, and never become the ledger's account identity.

## Consequences
- ADR-007's internal codes are no longer placeholders to be replaced by Finance codes; they remain internal identifiers. ADR-007 is amended accordingly.
- GL mapping belongs to configuration and to the finance export/integration (versioned and effective-dated like other configuration). The online posting path does not need it, and it is not implemented in Phase 1.
