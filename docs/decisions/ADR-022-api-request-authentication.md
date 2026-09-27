# ADR-022 — API Request Authentication: Fail Closed Until the Signature Profile Exists

**Status:** Proposed — product owner decision (Milestone 12 plan), implemented in Milestone 12e (`src/Ransys.Api/Security`). The RANSYS signed HTTP message profile remains **TODO / Architecture Decision Required**.

**Implementation status:** the interim policy itself (fail closed by default, Development/Test-only authenticator, environment guard, in-memory replay protection) is fully implemented and tested (`tests/Ransys.Api.Tests/SecurityTests.cs`). Production request authentication is deliberately **not** implemented — `FailClosedRequestAuthenticationService`/`FailClosedSignatureVerifier` reject all merchant traffic in every environment other than the gated Development/Test path. This is the intended fail-closed behavior, not a regression; it stays this way until the signature profile, key/certificate registry, mTLS binding, and a restart-durable shared replay store (listed below) are decided and built.

## Context
OpenAPI v1 §3 requires mTLS plus a "RANSYS signed HTTP message profile" with the headers `X-Ransys-Client-Id`, `X-Ransys-Timestamp`, `X-Ransys-Nonce`, `Content-Digest`, `Signature-Input`, `Signature` (the GET operation omits `Content-Digest`). No document specifies the profile: covered components, algorithms, key identifiers, the client key store, the clock-skew window, nonce retention, or how a client identity maps to a channel.

## Existing RANSYS rule
- Handoff §11: prepare `IRequestAuthenticationService`, `IReplayProtectionService`, `ISignatureVerifier`; **do not fake successful authentication**; unimplemented security must fail closed or be restricted to Development/Test.
- Handoff §12: nonce (anti-replay), `Idempotency-Key` (optional retry identity), `clientReference` (business identity) and the fingerprint (duplicate-payload detector) are separate concepts.
- Transaction Core commands carry the authenticated `ChannelId` / `MerchantId` (M12d); idempotency is scoped by channel.

## Technical issue
Without a profile no signature can be verified, yet the API must be testable end to end and runnable locally.

## Options
1. Implement a guessed profile (e.g. RFC 9421 with Ed25519). Rejected: invents a security contract.
2. Accept any request in non-production. Rejected: a configuration mistake would expose production.
3. Fail closed by default, plus an explicitly enabled Development/Test-only authenticator that performs every check that is already specified.

## Recommended option
Option 3.
- **Abstractions:** `IRequestAuthenticationService.AuthenticateAsync(AuthenticationRequest)` → `AuthenticationResult` (a `ClientContext(ChannelId, MerchantId, ClientId, CertificateThumbprint?)` or a failure reason that is only logged), `ISignatureVerifier`, `IReplayProtectionService.TryRegister(clientId, nonce, expiresAt)`, and the port `IChannelDirectory` (ACTIVE channel of an ACTIVE merchant).
- **Middleware** (`RequestAuthenticationMiddleware`, every `/api/v1` request; `/health` is open): reads the six headers (a repeated header counts as missing), the mTLS client certificate (SHA-256 thumbprint into the context; not required yet), buffers the body once (max 1 MiB) so the digest covers the exact bytes the endpoint parses. Failure ⇒ **401 / 3001** without disclosing the reason; an exception from the identity store ⇒ **503 / 1001** (fail closed).
- **Production default:** `FailClosedRequestAuthenticationService` rejects every request (401); `FailClosedSignatureVerifier` returns false.
- **Development/Test authenticator** (`DevelopmentRequestAuthenticationService`), registered only when `IHostEnvironment` is `Development` or `Test` **and** `Ransys:Auth:AllowDevelopmentAuthentication=true`. The flag in any other environment stops the host at startup. Checks, in order:
  1. `X-Ransys-Client-Id` is a channel UUID;
  2. `X-Ransys-Timestamp` is an RFC 3339 date-time with offset within ±`TimestampToleranceSeconds` (default 300);
  3. `X-Ransys-Nonce` is 1–128 visible ASCII characters;
  4. `Content-Digest`, when present, is RFC 9530 with a `sha-256=:<base64>:` member equal to SHA-256 of the body (other algorithms ignored; missing / duplicate sha-256 or bad encoding ⇒ fail);
  5. signature: verified through `ISignatureVerifier` unless `Ransys:Auth:SkipSignatureVerification` (default `true`, logged as a warning at startup) — with `false` the fail-closed verifier rejects every request;
  6. the channel is ACTIVE and its merchant ACTIVE (`IChannelDirectory`, DB lookup);
  7. the nonce is registered last (`InMemoryReplayProtectionService`, keyed by the normalized channel UUID, retained until timestamp + tolerance): a nonce already seen ⇒ 401. A request rejected by an earlier check does not consume its nonce.
- The `Idempotency-Key` header is read by the endpoints only and passed to Transaction Core as the idempotency key; the nonce is never used for idempotency.

## Consequences
- Production cannot serve merchant traffic until the profile exists; this is intended (fail closed).
- The in-memory nonce store is per process and lost on restart: acceptable only for Development/Test. Production needs a shared store (PostgreSQL or Redis with a PostgreSQL fallback decision) and retention ≥ the accepted timestamp window.
- TODO / Architecture Decision Required: signature profile (covered components incl. `@method`, `@target-uri`, `content-digest`, the X-Ransys headers; algorithm; `keyid`), client key/certificate registry and rotation, mTLS certificate ↔ client binding, whether `Content-Digest` is mandatory for POST, 403 rules (IP allow-list `3002` etc.), and the timestamp format (RFC 3339 chosen here).
