# ADR-018 — Capability Codes Follow the Provider Adapter Contract v1 Catalog

**Status:** Accepted — product owner decision, 2026-09-27 (Milestone 12a). Amends ADR-017.

## Context
Milestone 9 stored provider capabilities as lowercase `supports_*` codes (Architecture Spec §17 wording; ADR-017 added `supports_transfer` / `supports_void`). The Provider Adapter Contract v1 (§2) now defines the capability catalog that adapters report through `GetCapabilitiesAsync`, in uppercase:

`INQUIRY, PAYMENT, PURCHASE, TRANSFER, VOID, STATUS_CHECK, REVERSAL, REFUND, ADVICE, CALLBACK, BALANCE_CHECK, RECONCILIATION, SETTLEMENT_FILE`

The adapter contract also reports a transport status `PROTOCOL_ERROR` (the adapter could not parse or map the provider reply), which the v1.1 DDL CHECK `ck_attempt_transport_status` does not allow.

## Existing RANSYS rule
- Routing requires the capability returned by `ProviderCapabilities.RequiredFor(type)`; a provider without it is excluded (`CAPABILITY_UNSUPPORTED`).
- VOID is explicit and never mapped to REVERSAL or REFUND (ADR-017).
- Only an explicit "not sent" outcome proves non-delivery (ADR-005).

## Technical issue
Two spellings for the same capability (DB/config vs adapter contract) would require a translation layer at every boundary and invite silent mismatches, e.g. an adapter-reported `VOID` never matching a configured `supports_void`.

## Options
1. Keep `supports_*` in the database and translate at the adapter boundary.
2. Adopt the adapter catalog as the only capability vocabulary and migrate existing rows.

## Recommended option
Option 2 (decided).
- `ProviderCapabilities` constants (`src/Ransys.Domain/Routing/RoutingPolicy.cs`) are exactly the catalog; `ProviderCapabilities.All` lists it and a test checks it against the contract document.
- `RequiredFor` is unchanged in meaning: BALANCE_INQUIRY → `BALANCE_CHECK`, VOID → `VOID`, TRANSFER → `TRANSFER`, and so on. VOID still never resolves to REVERSAL or REFUND.
- Expand migration `0007_capability_catalog_and_protocol_error.sql` renames existing `integration.provider_capabilities` rows (`supports_payment` → `PAYMENT`, …). If a provider already has both spellings, the new row wins (a disabled new row stays disabled: fail closed).
- The same migration drops and recreates `ck_attempt_transport_status` with `PROTOCOL_ERROR` added. `TransportStatus.ProtocolError` may be recorded with `request_sent` true or false; it **never** proves non-delivery, so `AttemptResolution.Classify` returns IN_DOUBT and failover is not allowed.

## Consequences
- The v1.1 reference DDL (and the byte-identical baseline `0001`) do not contain `PROTOCOL_ERROR`; `CanonicalCodesTests` asserts the DDL values are a subset of the canonical transport codes and that `PROTOCOL_ERROR` is the only extra value.
- Configuration tooling and seed data must use the uppercase codes. Old `supports_*` codes no longer match any capability after `0007`.
