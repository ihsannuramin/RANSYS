# ADR-011 — Dapper + Npgsql Instead of EF Core; SQL Migrations

**Status:** Accepted — product owner decision, 2026-09-26

## Context
main.md §2 and §13 suggested "EF Core where appropriate", with raw SQL for row locking.

## Existing RANSYS rule
Financial operations must explicitly control the database transaction, row locking, posting idempotency and concurrency (main.md §13). The schema must match ERD v1.1 / DDL v1.1, which uses deferred constraint triggers and partial and expression indexes.

## Technical issue
EF Core migrations cannot express the DDL's triggers and expression indexes, and EF change tracking can hide wallet mutations.

## Options
1. EF Core for mappings and reads, raw SQL for financial writes.
2. Npgsql + Dapper everywhere.

## Decision
Option 2:
- **No EF Core.** Data access uses **Npgsql + Dapper** with explicit SQL and an explicit `NpgsqlTransaction`.
- The schema is applied from versioned SQL scripts derived from `RANSYS_PostgreSQL_Reference_DDL_v1.1.sql`, executed by a migration runner (DbUp for PostgreSQL, one journal table). Scripts that need non-transactional statements (e.g. `CREATE INDEX CONCURRENTLY`) are marked and run outside a transaction.
- Persistence row types stay separate from Domain aggregates (Canonical Data Model §89–90).

## Consequences
More hand-written SQL, but every lock, isolation level and statement in a financial path is visible and reviewable. Architecture tests forbid Dapper and Npgsql in `Ransys.Domain`.
