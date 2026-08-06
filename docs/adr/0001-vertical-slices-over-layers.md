# 1. Vertical slices, and no abstraction that isn't paying rent

**Status:** accepted · **Date:** 2026-08-06

## Context

The default shape for a .NET API of this size is Controllers → Services → Repositories →
`IRepository<T>`, usually with MediatR on top. It is what most tutorials show and what most
reviewers expect.

The cost is easy to miss. Adding one field to a sale means touching a controller, a DTO, a service
interface, a service, a repository interface, a repository, and a handler — seven files, none of
which do anything except forward. And `IRepository<T>` in front of EF Core is an abstraction over
an abstraction: `DbSet<T>` already is a repository, and wrapping it in one that returns
`IEnumerable<T>` quietly removes the ability to compose a query, which is the main thing EF is for.

## Decision

Feature folders under `Features/`, one per resource, each holding its endpoints, its request and
response records, and its validators. `PosLedgerDbContext` is injected straight into the endpoint
handler. No service layer, no repository interface, no mediator.

Shared code lives in `Common/` only once something actually shares it — `Page<T>`, the correlation
id, the validation filter.

## Consequences

- A change to how sales work is a change to `Features/Sales/`. There is one place to look.
- The endpoint reads as the whole story: lock, check, write, commit, respond. In the layered shape
  that story is spread across four files and the transaction boundary becomes hard to see, which
  is exactly the kind of thing that goes wrong under concurrency.
- The trade: no seam to swap the persistence technology, and no way to unit-test a handler without
  a database. Both are accepted deliberately. This API will not be moved off Postgres — it uses
  `FOR UPDATE`, `ILIKE`, `FILTER` and `jsonb` — and the tests that matter here run against a real
  Postgres in a container anyway (`tests/PosLedger.IntegrationTests/`). A mocked repository would
  prove that the mock was called, not that stock cannot go negative.
- If a piece of logic ever needs to be shared by two endpoints, it becomes a class at that moment,
  not before.
