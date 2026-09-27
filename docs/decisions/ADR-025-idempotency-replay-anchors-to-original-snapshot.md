# ADR-025 — Idempotent Replay Anchors to the Original Transaction's Own Snapshot

**Status:** Accepted — correctness bug fix following the idempotency rules already established by ADR-006/ADR-009, not a new open decision.

## Context
`TransactionProcessingService.PrepareOriginalAsync` (M12d) resolves the product (ACTIVE only), the currency definition (ACTIVE, highest version effective now) and, for reserving types, the merchant's main wallet, then builds the candidate transaction and its `TransactionFingerprint` from that freshly-resolved data — **before** checking whether an idempotency claim for the same channel + client reference already exists. Only after all of that does `IdempotencyService.ClaimAsync` look up the active claim and compare fingerprints.

## Existing RANSYS rule
- Idempotency (PRD §11, ADR-009): same channel + client reference + same fingerprint within the 24h window returns the existing transaction; a different fingerprint is `DUPLICATE_REFERENCE_CONFLICT`.
- The fingerprint (ADR-006) covers product id, amount, currency code, currency definition version and currency scale, computed once per request from the fields that define "the same request".
- `IReferenceDataReader.FindActiveProductByCodeAsync`/`FindActiveCurrencyAsync` intentionally answer "what is usable *now*" for a **new** transaction (Architecture Spec §12): an inactive product or an expired currency version must not be usable to start new financial activity.

## Technical issue
Reference data can legitimately change between an original request and a merchant's later retry of the exact same payload within the 24h idempotency window: a product can be deactivated, or a new currency definition version can become ACTIVE. When that happens, resolving "currently active" reference data before checking for an existing claim has two failure modes:
1. The product/currency/wallet resolution itself fails first (`PRODUCT_NOT_AVAILABLE`, `CURRENCY_NOT_SUPPORTED`, `WALLET_NOT_FOUND`) — the retry never even reaches the idempotency check, even though the original transaction is still there and unchanged.
2. If resolution happens to succeed (e.g. the currency code is still supported, just at a new version), the fingerprint is computed from the *new* product/currency snapshot, which differs from the fingerprint stored on the original claim (currency definition version and scale are part of it) — the retry is wrongly reported as `DUPLICATE_REFERENCE_CONFLICT` for a payload the merchant never changed.

Both are a replay-correctness bug, not a duplicate-detection bug: the merchant sent the identical request twice; RANSYS must return the first result, not depend on what reference data currently says.

## Options
1. Leave the fingerprint version-sensitive, but always resolve reference data with an "as of the original request time" query. Rejected: request time is attacker/clock controlled and there is no persisted "as of" audit trail to make this a reliable replacement for the original's own recorded product/currency.
2. Drop currency version/scale from the fingerprint so it is stable across currency version changes. Rejected: ADR-006 explicitly includes them, and removing them changes what "same payload" means for every existing fingerprint without a compatibility plan.
3. Look up the active idempotency claim by channel + client reference **first**. If one exists, compare the request against the *original transaction's own persisted snapshot* (its `ProductId`, its `Amount.Currency` — the exact hydrated `CurrencyDefinition` it was created with) instead of live reference data, and only compute a candidate fingerprint from that snapshot. Reference-data eligibility (ACTIVE product, ACTIVE currency version, usable wallet) is checked only for genuinely **new** transactions, which never had an anchor to begin with.

## Recommended option
Option 3.
- `IdempotencyService` gets a new public `PeekActiveAsync(session, channelId, clientReference)`, returning an `ExistingClaim(TransactionId, Fingerprint)` for an active, unexpired claim (or `null`), with the same lazy-expiry semantics ADR-009 already defines. `ClaimAsync`'s public signature and behavior are unchanged; its private `ResolveExistingAsync` now calls this method internally instead of duplicating the lookup.
- `TransactionProcessingService.PrepareOriginalAsync` calls `PeekActiveAsync` immediately after the request-shape checks that need no reference data (amount required, transfer source/destination, productCode required), and **before** `FindActiveProductByCodeAsync`/`FindActiveCurrencyAsync`/`FindMerchantMainWalletAsync`.
- When a claim exists, the request is compared against the *loaded original transaction*, never against current reference data:
  - Product: `IReferenceDataReader.FindProductIdByCodeAsync` (new; any status, existence only) resolves the request's `productCode` to an id, compared against the original's `Transaction.ProductId`.
  - Currency: the request's currency code is compared against the original's own `Amount.CurrencyCode`; the candidate `Money` is built with the original's own hydrated `Amount.Currency` (its exact captured version + scale), never a fresh currency lookup.
  - A mismatch on either (or an unknown product code) is `DUPLICATE_REFERENCE_CONFLICT` — a real payload change must still be rejected.
  - Otherwise a candidate fingerprint is computed with `TransactionFingerprint.Compute`, mirroring `BuildOriginal`, and compared against the claim's stored fingerprint. Equal ⇒ replay the original (`ReplayAsync`), with no product/currency/wallet resolution and no new claim. Different ⇒ `DUPLICATE_REFERENCE_CONFLICT`.
- When no active claim exists, the path is completely unchanged: ACTIVE product/currency/wallet resolution, `BuildOriginal`, `IdempotencyService.ClaimAsync`.

No schema change, no change to `TransactionFingerprint`'s algorithm or version, no change to `ClaimAsync`'s or `ReplayAsync`'s public behavior.

## Consequences
- A legitimate retry of an unchanged payload now always returns the original transaction, regardless of product status or currency definition version drift in between — matching the idempotency contract's actual promise.
- A genuinely different payload (different product, different currency, different amount, different endpoints) is still `DUPLICATE_REFERENCE_CONFLICT`, computed from the same fields as before.
- New transactions are unaffected: they still fail closed on an inactive product, unsupported currency or missing wallet exactly as before.
- Children (refund/reversal/void) are out of scope here: `PrepareChildAsync` already loads the original transaction before doing anything reference-data-dependent, and does not resolve product/currency/wallet for the child at all.
