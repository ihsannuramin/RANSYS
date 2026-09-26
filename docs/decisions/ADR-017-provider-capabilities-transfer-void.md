# ADR-017 — TRANSFER and VOID Provider Capabilities

**Status:** Accepted — product owner decision, 2026-09-27

## Context
Architecture Spec §17 lists no capability for TRANSFER or VOID; Milestone 9 introduced `supports_transfer` and `supports_void` as TODO codes.

## Decision
- `supports_transfer` and `supports_void` are official capabilities.
- VOID is an **explicit** capability. It must never be implicitly mapped to the REVERSAL or REFUND capabilities, or vice versa.

## Consequences
- Routing requires `supports_void` for VOID and `supports_transfer` for TRANSFER; a provider without the explicit capability is excluded (`CAPABILITY_UNSUPPORTED`).
- Tests assert that VOID never resolves to the reversal or refund capability.
