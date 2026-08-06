# pos-ledger-api

**Inventory and invoicing for a point of sale, built so that stock cannot go negative and a retried
payment cannot charge twice.** .NET 8, PostgreSQL 16, an append-only ledger, and the tests that
prove both guarantees under concurrency.

[![CI](https://github.com/Gabcar98-sys/pos-ledger-api/actions/workflows/ci.yml/badge.svg)](https://github.com/Gabcar98-sys/pos-ledger-api/actions/workflows/ci.yml)
[![Infrastructure](https://github.com/Gabcar98-sys/pos-ledger-api/actions/workflows/infra.yml/badge.svg)](https://github.com/Gabcar98-sys/pos-ledger-api/actions/workflows/infra.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![PostgreSQL 16](https://img.shields.io/badge/PostgreSQL-16-336791)

## Why this exists

A point-of-sale system has two rules that are easy to state and hard to keep. It must not sell what
it does not have, and it must not charge twice for one purchase. Both fail under exactly the same
condition — several tills hitting the same product at the same moment, on a network that drops
responses — and both fail silently, showing up days later as an inventory that does not match the
shelf.

This is the shape of a system I modernized in production: a POS written in Basic and Java 2, whose
integrations were rewritten as REST services and whose invoicing and inventory model was rebuilt.
This repository is the part of that work that can be shown: the same domain, the same two rules,
with the concurrency behaviour written down as tests instead of described in prose.

The CSV import endpoint is the server-side twin of
[IngestaWeb](https://github.com/Gabcar98-sys/Portafolio/tree/main/IngestaWeb), a browser-side
ingestion tool aimed at the same family of problems.

## Stack

| | |
| --- | --- |
| **API** | .NET 8, minimal APIs, vertical slices ([ADR-0001](docs/adr/0001-vertical-slices-over-layers.md)) |
| **Data** | PostgreSQL 16, EF Core 8 + Npgsql, snake_case schema, migrations |
| **Auth** | JWT bearer, roles `admin` / `cashier`, PBKDF2-SHA256 ([ADR-0006](docs/adr/0006-authentication.md)) |
| **Validation** | FluentValidation, RFC 9457 `ProblemDetails`, correlation id on every response and log line |
| **Logging** | Serilog — human-readable locally, compact JSON everywhere else |
| **Tests** | xUnit, FluentAssertions, **Testcontainers** running a real PostgreSQL |
| **Container** | Multi-stage Dockerfile, non-root, `docker compose up` for the whole stack |
| **CI/CD** | GitHub Actions: build, test, CodeQL, image to GHCR with a smoke test, tag-triggered deploy |
| **IaC** | Terraform — ECS Fargate, RDS, ALB, Secrets Manager, OIDC deploy role; tflint + checkov in CI |

## Run it in 60 seconds

```bash
git clone https://github.com/Gabcar98-sys/pos-ledger-api && cd pos-ledger-api
docker compose up
```

That is the whole setup — database, migrations, a seeded catalogue and the API. Nothing to install,
no connection string to edit. Then open **<http://localhost:8080>** for the Swagger UI, or:

```bash
curl localhost:8080/health
```

> If the host already runs PostgreSQL on 5432, start it as `POSTGRES_PORT=55432 docker compose up`
> — otherwise the container's port mapping loses to the local instance and the errors make no sense.

Demo accounts, published on purpose because this is a public demo (the stored values are PBKDF2
hashes either way — see [ADR-0006](docs/adr/0006-authentication.md)):

| User | Password | May |
| --- | --- | --- |
| `admin` | `admin-demo-2026` | everything, including the catalogue and imports |
| `cashier` | `cashier-demo-2026` | sell |

[`src/PosLedger.Api/PosLedger.Api.http`](src/PosLedger.Api/PosLedger.Api.http) walks through the
entire API in 18 requests, including the ones that are supposed to fail.

## Architecture

```mermaid
flowchart LR
    till([POS terminal])

    subgraph api["ASP.NET Core 8 · vertical slices"]
        auth["Auth<br/>JWT · admin / cashier"]
        products["Products<br/>keyset pagination"]
        sales["Sales<br/>idempotent · row-locked"]
        imports["Imports<br/>CSV, per-row rules"]
        recon["Reconciliation<br/>ledger vs. cache"]
    end

    subgraph pg[("PostgreSQL 16")]
        movements[["stock_movements<br/><b>append-only · the truth</b>"]]
        prod[["products<br/>stock_on_hand = cached sum"]]
        salet[["sales · sale_lines<br/>price copied at sale time"]]
        idem[["idempotency_records<br/>key IS the primary key"]]
    end

    till --> auth
    till --> products
    till --> sales
    till --> imports
    till --> recon

    sales -->|"SELECT … FOR UPDATE"| prod
    sales --> movements
    sales --> salet
    sales --> idem
    imports --> movements
    imports --> prod
    products --> prod
    recon -->|"SUM(delta) FILTER (…)"| movements
    recon -.compares.-> prod
```

Stock only ever moves by appending to `stock_movements`. `products.stock_on_hand` is a cached
projection of that sum, written in the same transaction, and `GET /api/v1/reconciliation` recomputes
the sum to prove the two never drift apart ([ADR-0002](docs/adr/0002-the-ledger-is-the-truth.md)).

## What's interesting here

### Fifty tills, ten units, zero oversells

The test this repository exists to be able to point at:

```csharp
created.Should().Be(stock,               "exactly the available units may be sold");
conflicted.Should().Be(attempts - stock, "every other attempt must be refused, not queued");
```

Fifty concurrent requests race for the last ten units. Exactly **10 succeed with 201 and 40 are
refused with 409** — not "roughly ten", and never eleven. The guarantee comes from
`SELECT … FOR UPDATE` inside the transaction, with rows locked in a deterministic order so two
multi-line sales cannot deadlock against each other.

The test runs against a real PostgreSQL in a container, because there is nothing to test otherwise:
an in-memory provider has no row locks, so this exact test passes against a completely broken
implementation. That is the difference between a test suite and a decorative one.

### A retry is not a second sale

`POST /api/v1/sales` requires an `Idempotency-Key`. The key is the **primary key** of the
idempotency table, so claiming it *is* the insert — there is no check-then-act window for two
concurrent retries to slip through. A unique-violation on that insert means someone got there
first, and the stored response is replayed with an `Idempotent-Replay: true` header.

Reusing a key with a *different* basket returns 422 rather than quietly replaying. That is a client
bug, and hiding it helps nobody.

### 37 ms → 2 ms, with the plans to show for it

The reconciliation query over a million movements, taken apart with `EXPLAIN (ANALYZE, BUFFERS)`:

| | Time | Buffers |
| --- | ---: | ---: |
| Parallel sequential scan | 36.95 ms | 9,360 |
| After the right leading column | 6.45 ms | 6,425 |
| Covering index, after `VACUUM` | 2.02 ms | 62 |

The third row is the one worth reading about: the covering index does **not** produce an index-only
scan until `VACUUM` has populated the visibility map, so straight after a bulk load it is still a
bitmap heap scan. Measuring it is how that turned up.

Full plans, the fixture that reproduces them, and what each stage actually taught:
[**docs/query-optimization.md**](docs/query-optimization.md).

### Imports that report why a row was rejected

`POST /api/v1/imports` takes a CSV and applies the good rows while telling you precisely what was
wrong with the rest — `sku_unknown`, `quantity_not_a_number`, `insufficient_stock`,
`product_inactive`, `sku_duplicated` and so on, grouped by rule with sample line numbers. Syntactic
problems are reported before relational ones, so a row with a malformed quantity is not reported as
a duplicate.

## Tests

```bash
dotnet test
```

48 tests — 10 unit, 38 integration. The integration tests boot the real HTTP pipeline with
`WebApplicationFactory` against a PostgreSQL 16 container started by Testcontainers — real row locks, real constraints, real `ILIKE`. They need a
running Docker daemon and nothing else; CI uses the runner's.

What they cover, beyond the happy paths: the 50-way concurrency race, idempotent replay and the
rejected key reuse, stock left untouched after a failed sale, prices frozen onto sale lines when the
catalogue is repriced afterwards, role enforcement (401 vs. 403), every import rule, and
reconciliation catching stock deliberately written without a movement.

## Infrastructure

[`infra/`](infra/README.md) describes the AWS deployment in Terraform: ECS Fargate behind an ALB,
RDS PostgreSQL in private subnets, secrets in Secrets Manager under a customer-managed KMS key, and
a GitHub Actions deploy role that assumes through **OIDC** so no AWS keys are ever stored.

**It is validated in CI, not deployed to a live AWS account** — the stack costs roughly $120/month
and this repository is not funded. Every pull request runs `fmt`, `validate`, `tflint`, `checkov`
(0 failures, 17 skips each justified inline) and a full `terraform plan` that renders all 65
resources **with no AWS credentials at all**, which is possible because the configuration uses no
data sources that read live AWS state.

[`infra/README.md`](infra/README.md) has the cost breakdown, the reasoning behind the trade-offs,
and a list of what was deliberately left out.

## Decisions

| # | Decision |
| --- | --- |
| [0001](docs/adr/0001-vertical-slices-over-layers.md) | Vertical slices; no `IRepository<T>`, no mediator |
| [0002](docs/adr/0002-the-ledger-is-the-truth.md) | Append-only ledger; stock on hand is a cache |
| [0003](docs/adr/0003-money-and-time.md) | `numeric(18,2)` and `timestamptz`, always UTC |
| [0004](docs/adr/0004-keyset-pagination.md) | Keyset cursors instead of `OFFSET` |
| [0005](docs/adr/0005-migrations-on-startup.md) | Migrate on startup only where there is one instance |
| [0006](docs/adr/0006-authentication.md) | JWT with two roles, and no identity provider of my own |

## Endpoints

| | |
| --- | --- |
| `POST /api/v1/auth/token` | Exchange credentials for a bearer token |
| `GET /api/v1/products?q=&after=&limit=` | Catalogue, keyset paginated, case-insensitive search |
| `POST` `PUT` `/api/v1/products` | Admin only. Stock is never set here — it moves through the ledger |
| `POST /api/v1/sales` | Requires `Idempotency-Key`. 409 on insufficient stock, 422 on key reuse |
| `GET /api/v1/sales` | Recent sales |
| `POST /api/v1/imports` | Admin only. CSV upload, per-row error report |
| `GET /api/v1/reconciliation?from=&to=` | Ledger against cached stock. Drift must be zero |
| `GET /health` · `GET /ready` | Liveness without a database; readiness with one |

## License

MIT — see [LICENSE](LICENSE).
