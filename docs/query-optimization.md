# Making reconciliation fast, and what the plan actually said

`GET /api/v1/reconciliation` aggregates the stock ledger over a date window. On a table with a
million movements the first version took **37 ms**; it now takes **2 ms** and reads **150× fewer
pages**. This is how that was measured and what it turned on.

Everything below is reproducible:

```bash
docker compose up -d db
dotnet ef migrations script --idempotent --project src/PosLedger.Api -o schema.sql
docker compose exec -T db psql -U posledger -d posledger < schema.sql
docker compose exec -T db psql -U posledger -d posledger < scripts/benchmark-reconciliation.sql
```

**Fixture** — 1,000 products, 1,000,000 movements spread over two years, table 211 MB, Postgres 16
in a container. The window queried is seven days, which matches ~9,600 rows.

---

## The query

```sql
SELECT product_id,
       COALESCE(SUM(-delta) FILTER (WHERE reason = 1), 0)::int AS units_sold,
       COALESCE(SUM(delta)  FILTER (WHERE delta > 0), 0)::int  AS units_received,
       COALESCE(SUM(delta), 0)::int                            AS net_change
FROM stock_movements
WHERE occurred_at >= $1 AND occurred_at < $2
GROUP BY product_id;
```

## Stage 1 — the index that existed did not apply

`stock_movements` already had `(product_id, occurred_at)`. That index serves the write path: a sale
looks up one product and its recent movements. It cannot serve this query, because the query does
not know a product id — it filters on time across every product, and a composite index is only
usable from its leading column.

```
Finalize GroupAggregate  (actual time=30.922..36.807 rows=700 loops=1)
  Buffers: shared hit=9360
  ->  Gather Merge  (Workers Launched: 2)
        ->  Partial HashAggregate
              ->  Parallel Seq Scan on stock_movements  (actual time=0.012..17.926 rows=3197 loops=3)
                    Filter: ((occurred_at >= …) AND (occurred_at < …))
                    Rows Removed by Filter: 330137
Execution Time: 36.950 ms
```

Postgres read the whole table across three parallel workers and threw away 99% of it —
`Rows Removed by Filter: 330137` per worker, to keep 3,197.

**The lesson worth keeping:** the index that makes writes fast and the index that makes the report
fast are different indexes, and adding the second one is not "adding an index we forgot".

## Stage 2 — the right leading column

```sql
CREATE INDEX ix_stock_movements_occurred_at_reason
    ON stock_movements (occurred_at, reason)
    INCLUDE (product_id, delta);
```

```
HashAggregate  (actual time=6.232..6.310 rows=700 loops=1)
  Buffers: shared hit=6425
  ->  Bitmap Heap Scan on stock_movements  (actual time=1.606..4.794 rows=9590 loops=1)
        Heap Blocks: exact=6364
        ->  Bitmap Index Scan on ix_stock_movements_occurred_at_reason  (actual time=1.051 rows=9590)
Execution Time: 6.452 ms
```

37 ms → 6.5 ms. But look at the plan rather than the number: it is a **Bitmap Heap Scan**, and
`Heap Blocks: exact=6364`. The index found the 9,590 rows immediately (1 ms) and then Postgres went
to the heap for all of them anyway. The `INCLUDE` columns did nothing.

## Stage 3 — why the covering index was not covering

An index-only scan is only allowed when Postgres knows the rows are visible to every transaction,
and it learns that from the **visibility map**, which is populated by `VACUUM`. The million rows had
just been inserted, so the map was empty and every row needed a heap check to confirm it was not a
dead tuple.

```sql
VACUUM (ANALYZE) stock_movements;
```

```
HashAggregate  (actual time=1.816..1.894 rows=700 loops=1)
  Buffers: shared hit=62
  ->  Index Only Scan using ix_stock_movements_occurred_at_reason  (actual time=0.023..0.680 rows=9590)
        Index Cond: ((occurred_at >= …) AND (occurred_at < …))
        Heap Fetches: 0
Execution Time: 2.016 ms
```

`Heap Fetches: 0` — the heap is never touched. Buffers drop from 9,360 to **62**.

| | Plan | Time | Buffers |
| --- | --- | --- | --- |
| No reporting index | Parallel Seq Scan | 36.95 ms | 9,360 |
| Index, cold visibility map | Bitmap Heap Scan | 6.45 ms | 6,425 |
| Index, after VACUUM | Index Only Scan | **2.02 ms** | **62** |

## What this costs

The index is **47 MB** against a 211 MB table, and every insert into `stock_movements` maintains it.
That is the trade being made: the ledger is written once per sale line and read by a report that
scans a window, so paying on write to save on read is the right direction here. It would be the
wrong direction on a table that is written constantly and read never.

## What I would have got wrong by trusting the number

Stage 2 is a 5.7× improvement. It is tempting to stop there and write "added an index, 5.7× faster"
in the commit message. The plan says something different: the index was doing half its job, and the
`INCLUDE` clause — the part that makes it a covering index — was inert until autovacuum happened to
run. On a table that is only ever appended to, autovacuum's insert-triggered threshold is what
decides when that happens, so a benchmark run right after a bulk load measures the slow shape and a
production table measures the fast one.

**`EXPLAIN` without `ANALYZE` would not have shown this, and neither would a stopwatch.**
