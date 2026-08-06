-- Reproduces the measurement in docs/query-optimization.md.
--
--   docker compose up -d db
--   dotnet ef database update --project src/PosLedger.Api
--   docker compose exec -T db psql -U posledger -d posledger -f - < scripts/benchmark-reconciliation.sql
--
-- Seeds a million movements over two years across a thousand products, then runs the
-- reconciliation window query with the reporting index dropped and again with it in place.

\timing off
\set ON_ERROR_STOP on

-- ── Seed ──────────────────────────────────────────────────────────────────────
DELETE FROM stock_movements WHERE reference = 'bench';
DELETE FROM products WHERE sku LIKE 'BENCH-%';

INSERT INTO products (id, sku, name, unit_price, stock_on_hand, is_active, created_at, updated_at)
SELECT gen_random_uuid(), 'BENCH-' || g, 'Bench item ' || g, 1000.00, 0, true, now(), now()
FROM generate_series(1, 1000) g;

CREATE TEMP TABLE pids AS
SELECT row_number() OVER (ORDER BY sku) AS n, id
FROM products
WHERE sku LIKE 'BENCH-%';

-- Deterministic rather than random, so two runs of this script are comparable.
INSERT INTO stock_movements (product_id, delta, reason, reference, occurred_at)
SELECT pids.id,
       CASE WHEN g % 10 < 7 THEN -(1 + (g % 3)) ELSE (1 + (g % 20)) END,
       CASE WHEN g % 10 < 7 THEN 1 ELSE 2 END,
       'bench',
       timestamptz '2024-01-01 00:00:00+00'
           + ((g % 730) || ' days')::interval
           + ((g % 86400) || ' seconds')::interval
FROM generate_series(1, 1000000) g
JOIN pids ON pids.n = 1 + (g % 1000);

ANALYZE stock_movements;

SELECT count(*) AS movements, pg_size_pretty(pg_total_relation_size('stock_movements')) AS table_size
FROM stock_movements;

-- ── Before: no index that starts with occurred_at ─────────────────────────────
DROP INDEX IF EXISTS ix_stock_movements_occurred_at_reason;
ANALYZE stock_movements;

\echo '=== BEFORE (no reporting index) — warm-up run, ignore ==='
EXPLAIN (ANALYZE, BUFFERS, TIMING)
SELECT product_id,
       COALESCE(SUM(-delta) FILTER (WHERE reason = 1), 0)::int AS units_sold,
       COALESCE(SUM(delta)  FILTER (WHERE delta > 0), 0)::int  AS units_received,
       COALESCE(SUM(delta), 0)::int                            AS net_change
FROM stock_movements
WHERE occurred_at >= timestamptz '2025-06-01' AND occurred_at < timestamptz '2025-06-08'
GROUP BY product_id;

\echo '=== BEFORE (no reporting index) — measured run ==='
EXPLAIN (ANALYZE, BUFFERS, TIMING)
SELECT product_id,
       COALESCE(SUM(-delta) FILTER (WHERE reason = 1), 0)::int AS units_sold,
       COALESCE(SUM(delta)  FILTER (WHERE delta > 0), 0)::int  AS units_received,
       COALESCE(SUM(delta), 0)::int                            AS net_change
FROM stock_movements
WHERE occurred_at >= timestamptz '2025-06-01' AND occurred_at < timestamptz '2025-06-08'
GROUP BY product_id;

-- ── After: covering index on (occurred_at, reason) ────────────────────────────
CREATE INDEX ix_stock_movements_occurred_at_reason
    ON stock_movements (occurred_at, reason)
    INCLUDE (product_id, delta);

ANALYZE stock_movements;

\echo '=== AFTER (covering reporting index) — warm-up run, ignore ==='
EXPLAIN (ANALYZE, BUFFERS, TIMING)
SELECT product_id,
       COALESCE(SUM(-delta) FILTER (WHERE reason = 1), 0)::int AS units_sold,
       COALESCE(SUM(delta)  FILTER (WHERE delta > 0), 0)::int  AS units_received,
       COALESCE(SUM(delta), 0)::int                            AS net_change
FROM stock_movements
WHERE occurred_at >= timestamptz '2025-06-01' AND occurred_at < timestamptz '2025-06-08'
GROUP BY product_id;

\echo '=== AFTER (covering reporting index) — measured run ==='
EXPLAIN (ANALYZE, BUFFERS, TIMING)
SELECT product_id,
       COALESCE(SUM(-delta) FILTER (WHERE reason = 1), 0)::int AS units_sold,
       COALESCE(SUM(delta)  FILTER (WHERE delta > 0), 0)::int  AS units_received,
       COALESCE(SUM(delta), 0)::int                            AS net_change
FROM stock_movements
WHERE occurred_at >= timestamptz '2025-06-01' AND occurred_at < timestamptz '2025-06-08'
GROUP BY product_id;

SELECT pg_size_pretty(pg_relation_size('ix_stock_movements_occurred_at_reason')) AS reporting_index_size;
