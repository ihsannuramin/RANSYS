# ADR-024 — Refund Authorization: Merchant API Refund vs Manual Refund

**Status:** Proposed — implemented in Milestone 12c (`Ransys.Ledger.RefundAuthorization`). This is the ledger-contract/authorization-shape decision only; it is not a claim that the Backoffice maker-checker workflow itself is implemented (see the trust boundary below).

## Context
ADR-023 makes a refund a child transaction that a merchant starts through `POST /api/v1/refunds`. When the provider confirms the child, Transaction Core posts the refund (`PostRefundAsync`, key `TX:<original>:REFUND:<child>`). Until now `RefundRequest` required a non-empty `ApprovalRequestId`, because the Ledger Posting Service only knew manual (Backoffice) refunds.

## Existing RANSYS rule
- Maker-checker is mandatory for **manual** top-up, **manual** refund, manual reversal and limit changes (Architecture Spec §31, CLAUDE.md). The maker can never be the checker.
- Refund postings go only through `ILedgerPostingService.PostRefundAsync`; total refunds never exceed the posted amount; the fee part comes only from `RefundFeeCalculator` (ADR-014).

## Technical issue
A merchant API refund has no maker-checker request: the authenticated merchant *is* the requester, and the provider's confirmation of the child is the execution evidence. Passing a fake approval id would weaken the manual rule (a journal would look approved without an approval), and dropping the check would let any caller post refunds without authorization.

## Options
1. Keep `Guid ApprovalRequestId` and pass `Guid.NewGuid()` for API refunds. Rejected: forges maker-checker evidence.
2. Make the approval optional. Rejected: a missing value would silently mean "authorized".
3. Replace the id with an explicit authorization with exactly two forms.

## Recommended option
Option 3. `RefundRequest.Authorization` is a `RefundAuthorization`:
- `ApprovedRequest(ApprovalRequestId)` — manual/Backoffice refund. The id must be non-empty and is written to `ledger_transactions.approval_request_id` (unchanged behavior; the old `Guid` constructor of `RefundRequest` maps to this form).
- `MerchantApiRequest(RefundTransactionId, ChannelId, ClientReference)` — merchant API refund. It is valid only when `RefundRequest.RefundTransactionId` equals `RefundTransactionId` (the posting is bound to its own REFUND child), the child differs from the original, and the client reference is present. The journal has no approval id; its `ransys_transaction_id` is the child, whose authenticated channel, idempotency record, attempts and history are the evidence.
- Anything else (including an empty approval id or an unbound merchant authorization) returns `APPROVAL_REQUIRED`.

Only `TransactionFinalizationService` creates `MerchantApiRequest`, and only after the refund child reached SUCCESS in the same database transaction (parent → child lock order).

## Consequences
- Manual refunds keep maker-checker exactly as before; the ledger contract change is source compatible for them.
- A merchant API refund is traceable through its child transaction rather than an approval request.
- Refund limits are unchanged: `REFUND_EXCEEDS_POSTED` in the ledger, plus the child-level cap (sum of non-failed refund children ≤ original principal) checked when the child is created.
- Backoffice manual refunds (later milestone) must use `ApprovedRequest`.

## Trust boundary (important before opening the manual Backoffice path)
- At the ledger, `ApprovedRequest` only checks that `ApprovalRequestId` is a non-empty GUID. It does **not** prove the approval was actually stored, is in an APPROVED state, or that its maker differs from its checker — that validation does not exist yet anywhere in this codebase. Do not read this ADR's "implemented" status as a maker-checker workflow being enforced.
- The ledger also does not independently verify merchant/channel ownership against the database for either authorization form; it trusts the caller (Transaction Core) to have already authenticated the request. A production manual-refund endpoint must validate a real, stored, approved request (with maker ≠ checker) before calling `PostRefundAsync` with `ApprovedRequest`, and the merchant API path depends on ADR-022's production authentication being completed first.
