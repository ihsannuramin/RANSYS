# ADR-007 — Placeholder Ledger Account Codes

**Status:** Accepted — recommended option approved by product owner, 2026-09-26

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
| Provider payable | `SYSTEM:PROVIDER_PAYABLE:<providerId>:<currencyDefId>` | LIABILITY | C |
| Provider receivable | `SYSTEM:PROVIDER_RECEIVABLE:<providerId>:<currencyDefId>` | ASSET | D |
| Fee revenue | `SYSTEM:RANSYS_FEE_REVENUE:<currencyDefId>` | REVENUE | C |
| Tax payable | `SYSTEM:TAX_PAYABLE:<currencyDefId>` | LIABILITY | C |
| Cash clearing | `SYSTEM:CASH_CLEARING:<currencyDefId>` | ASSET | D |
| Adjustment clearing | `SYSTEM:ADJUSTMENT_CLEARING:<currencyDefId>` | CONTROL | D |

## Consequences
Final codes will come from Finance. Because codes are produced by a single `LedgerAccountCodes` factory, adopting them is a data migration, not a code change.
