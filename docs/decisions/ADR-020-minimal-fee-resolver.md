# ADR-020 — Minimal Fee Resolver

**Status:** Accepted (interim) — product owner decision (Milestone 12 plan), implemented in Milestone 12d (`Ransys.TransactionCore.Fees.FeeResolver`, `PostgresFeeRuleReader`).

## Context
`TransactionProcessingService` must capture fee components when a transaction is validated, before the reservation (Architecture Spec §10: reserve = principal + guaranteed merchant fee). The DDL has `config.fee_rules` (`FIXED` / `PERCENTAGE`, `fee_value numeric(30,12)`, optional `minimum_fee` / `maximum_fee`, optional `merchant_id`, per product + transaction type + currency definition, versioned by `config_version_id`). The Configuration Schema v1 that would define fee semantics precisely does not exist yet.

## Existing RANSYS rule
- Fees are captured on the transaction with the configuration version used and never recalculated from current configuration (Ledger Posting Rule Matrix §48–49, State Transition Matrix §68).
- Refund fee amounts come only from the captured components and their refund policy (ADR-014); the default policy is NONE.
- Money is `decimal`, rejected when it exceeds the currency scale.

## Technical issue
The documents do not say whether `PERCENTAGE` is a rate or percent points, how rounding works, how min/max interact with rounding, which rule wins when both a generic and a merchant rule exist, or what happens without an active FEE version.

## Options
1. Fail closed on anything not specified (no FEE version ⇒ reject every payment).
2. A minimal, deterministic resolver with documented choices, replaced by the Configuration Schema v1 later.

## Recommended option
Option 2:
- Rules come from the **ACTIVE FEE configuration version** effective now (`ConfigurationService`, domain `FEE`). **No active version or no matching rule ⇒ zero fee** (no component). A missing fee can neither overdraw nor double-charge a merchant; it is a revenue risk only, visible because `fee_config_version_id` is then null.
- Matching: same config version, product, transaction type code, currency definition (code + version) and `effective_from <= now < effective_until`, and `merchant_id` NULL (generic) or equal to the merchant.
- Precedence: a **merchant-specific rule wins** over generic rules. Two or more rules at the winning precedence ⇒ **fail closed** (`FEE_RULE_AMBIGUOUS`, no transaction is created).
- Amount: `FIXED` ⇒ `fee_value`; `PERCENTAGE` ⇒ `amount × fee_value`, where **`fee_value` is a decimal rate** (`0.015` = 1.5 %), consistent with rates being `numeric(30,12)`.
- Then clamp to `minimum_fee` / `maximum_fee` (when present), then round **half away from zero** (`MidpointRounding.AwayFromZero`) to the currency scale.
- Result: one `MERCHANT_SERVICE_FEE` component, charged = accounting amount, beneficiary `RANSYS`, refund policy **NONE**, `calculation_rule_version` = FEE config `version_no`; `fee_config_version_id` is captured in the transaction's configuration snapshot (with the routing config version).
- A zero result produces no component.

## Consequences
- Fee rule data must use rates, not percent points, for `PERCENTAGE`; a value of `1.5` would mean 150 %.
- Refunds of transactions priced by this resolver return no fee (policy NONE) until configuration can set PRO_RATA / FULL per component.
- Provider cost, tax and margin splits (Ledger Matrix §13) are not produced; the baseline payment split applies.
- TODO / Architecture Decision Required: Configuration Schema v1 (fee semantics, refund policy per rule, tiers, maker-checker on fee changes).
