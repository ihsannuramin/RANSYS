# ADR-006 — Fingerprint Version Encoding

**Status:** Accepted — recommended option approved by product owner, 2026-09-26

## Context
Canonical Data Model §110–111 recommends `TransactionFingerprint { Value, Version }`.

## Existing RANSYS rule
DDL v1.1 stores only `transactions.transaction_fingerprint varchar(128)` and `idempotency_records.fingerprint varchar(128)`.

## Technical issue
There is no column for the version, so a future algorithm change would make stored fingerprints uninterpretable.

## Options
1. Add a version column (schema change).
2. Encode the version in the value.

## Recommended option
Option 2. The persisted value is `v<version>:<lowercase sha256 hex>` (e.g. `v1:3f5a…`, 67 characters).

Algorithm v1: SHA-256 over a canonical, ordered serialization of merchant/channel, transaction type, product, stable source/destination identifiers, amount (canonical decimal string at currency scale) + currency code + currency definition version, and client reference. Timestamps, nonce, signature and trace identifiers are excluded.

## Consequences
No schema change. Fingerprints are only compared when computed with the same version.
