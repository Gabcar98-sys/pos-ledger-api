# 3. Money is `decimal`, time is `timestamptz` in UTC

**Status:** accepted · **Date:** 2026-08-06

## Context

Two data-type choices cause more production incidents in commercial software than any amount of
architecture, and both are made once, at the schema, where changing them later means a migration
over live data.

`double` cannot represent 0.1 exactly. Sum enough prices and the invoice total stops matching the
sum of its lines by a cent, and reconciliation never closes. The error is small, silent, and
impossible to explain to an accountant.

`timestamp without time zone` stores a wall-clock reading with no record of which clock. When the
server, the database and the client disagree — or when the server moves region — the stored values
are ambiguous, and there is no way to repair them afterwards because the information was never
written down.

## Decision

**Money:** `numeric(18,2)` in Postgres, `decimal` in C#. Exact decimal arithmetic, two places,
enough range for any realistic total. Never `double`, never `float`, at any layer including the
JSON contract.

Prices are stored, not computed on read: a sale line copies the unit price charged at the time, and
line totals are computed from that copy.

**Time:** `timestamptz` in Postgres, `DateTimeOffset` in C#, always UTC at the boundary. Npgsql
maps these directly and refuses a `DateTime` with the wrong `Kind`, which turns a whole category of
mistake into a startup error instead of a wrong number.

Formatting into a local time zone is a presentation concern and belongs to whoever displays it.

Two check constraints back this up at the database level: `unit_price >= 0` and
`stock_on_hand >= 0`.

## Consequences

- Totals are exact. `SUM(quantity * unit_price)` in SQL and the same sum in C# agree to the cent,
  which is what makes reconciliation meaningful at all.
- `numeric` is slower than a float. For the volumes an inventory ledger sees this is irrelevant,
  and it would still be the right choice if it were not.
- The API always emits UTC with an offset. A client that wants Bogotá time converts it; the API
  does not guess.
- The container image installs ICU rather than running in globalization-invariant mode. Invariant
  mode silently changes currency formatting and date parsing, which is the same class of bug this
  ADR exists to prevent — the ~30 MB is the correct price. See the comment in the `Dockerfile`.
