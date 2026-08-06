# 5. Migrations run on startup only where there is exactly one instance

**Status:** accepted · **Date:** 2026-08-06

## Context

`db.Database.Migrate()` in `Program.cs` is the most convenient way to make an app come up against
an empty database, and the most common way to break a rolling deployment.

Two problems, both of which only appear in the environment where they hurt:

1. **Concurrency.** Two tasks rolling at the same time both start migrating. EF takes an advisory
   lock, so the second one waits rather than corrupting anything — but it waits with no health
   check answered, and on a slow migration the orchestrator kills it for failing to start.
2. **Blast radius.** A migration that fails takes down the application. It should fail a pipeline
   step, loudly, before any traffic is involved.

Against that: a reviewer who clones this repository must not have to run a migration command by
hand before `docker compose up` works. A quickstart with a manual step is a quickstart that people
abandon.

## Decision

Migration on startup is a configuration flag, `Database:MigrateOnStartup`, and it is **off by
default** in `appsettings.json`. It is switched on exactly where there is a single instance and no
release pipeline to hang the migration off:

| Environment | Flag | Why |
| --- | --- | --- |
| `docker compose` (local) | `true` | One container. `docker compose up` has to just work. |
| Render (hosted demo) | `true` | Free tier, one instance, no pipeline step available. |
| ECS Fargate (`infra/`) | `false` | Several tasks roll at once; they would race. |
| Integration tests | `false` | The test factory migrates once, explicitly, before any test runs. |

`Database:SeedOnStartup` follows the same rule and the same defaults, and the seed is idempotent —
it returns immediately if any product exists.

For the ECS path, migrations belong in a one-off ECS task run by the deploy pipeline before the
service is updated. That step does not exist yet, because the stack is validated rather than
deployed (`infra/README.md`); the flag being `false` is what keeps that from being an accident
waiting to happen.

## Consequences

- The clone-and-run path stays one command, which is the single most important thing the repository
  has to get right.
- The production path is the correct one, and the difference between them is a flag rather than a
  code path — nothing is compiled differently.
- The gap is named rather than hidden: until the pipeline gains a migration step, an ECS deploy
  against a fresh database would come up with no schema. That is the honest state of an
  infrastructure definition that has never been applied.
