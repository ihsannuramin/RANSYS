# ADR-007 — Placeholder Ledger Account Codes

**Status:** Accepted — recommended option approved by product owner, 2026-09-26; amended by ADR-015 (2026-09-27)

## Context
Postings need system accounts: cash clearing, provider payable, fee revenue, adjustment clearing, provider receivable.

## Existing RANSYS rule
Ledger Posting Rule Matrix §3 and §7 name the accounts; ERD v1.1 §52 leaves the exact Chart of Accounts codes open. `ledger_accounts.account_code` is UNIQUE and each account has exactly one `currency_definition_id`.

## Technical issue
There are no agreed codes, and accounts must be per currency definition.

## Recommended option
Placeholder convention, with accounts created lazily and idempotently:

| Account | account_code | class | normal side |
|---|---|---|---|
| Merchant available | `MERCHANT:<walletId>:AVAILABLE` | LIABILITY | C |
| Merchant reserved | `MERCHANT:<walletId>:RESERVED` | LIABILITY | C |
| Provider payable | `SYSTEM:PROVIDER_PAYABLE:<providerId>:<currency>` | LIABILITY | C |
| Provider receivable | `SYSTEM:PROVIDER_RECEIVABLE:<providerId>:<currency>` | ASSET | D |
| Fee revenue | `SYSTEM:RANSYS_FEE_REVENUE:<currency>` | REVENUE | C |
| Tax payable | `SYSTEM:TAX_PAYABLE:<currency>` | LIABILITY | C |
| Cash clearing | `SYSTEM:CASH_CLEARING:<currency>` | ASSET | D |
| Adjustment clearing | `SYSTEM:ADJUSTMENT_CLEARING:<currency>` | CONTROL | D |

**Implementation note (Milestone 5):** `<currency>` is rendered as `<code>-V<version>` (e.g. `IDR-V1`), which is unique per currency definition (`uq_currency_definition`). This keeps the domain independent of database UUIDs (Canonical Data Model §138) while keeping one account per currency definition.

## Consequences
Final codes will come from Finance. Because codes are produced by a single `LedgerAccounts` factory (`src/Ransys.Domain/Ledger/LedgerAccounts.cs`), adopting them is a data migration, not a code change.

## Amendment (ADR-015, 2026-09-27)
These internal codes are authoritative semantic identifiers, not placeholders for Finance codes. External GL account codes are configurable mappings from the semantic account type and are never hardcoded into domain or ledger logic.
