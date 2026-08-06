# 2. The ledger is the truth; stock on hand is a cache

**Status:** accepted · **Date:** 2026-08-06

## Context

An inventory system can store stock in two ways. Either `products.stock_on_hand` is the truth and
gets updated in place, or every movement is recorded and the current stock is their sum.

Storing only the current number is smaller and faster, and it loses the answer to every interesting
question: why is there one fewer than yesterday, who sold it, was it a sale or breakage or a
correction. When a count comes out wrong — and in a real shop it does — there is nothing to audit.
This is the failure mode the POS system this repository is modelled on actually had.

Summing an append-only ledger on every read is honest and does not scale: a million movements
recomputed per page view.

## Decision

Both, with an explicit direction of authority.

`stock_movements` is append-only and is the source of truth. Every row has a non-zero `delta`, a
`reason` (`Sale`, `Import`, `Adjustment`, `Return`), an optional `reference`, and the time it
happened. Nothing updates or deletes a movement, ever. Products are deactivated with `is_active`
rather than deleted, because the ledger references them forever.

`products.stock_on_hand` is a **cached projection** of that sum, maintained in the same transaction
that appends the movement, so it can never lag by more than the length of a transaction. A check
constraint (`stock_on_hand >= 0`) guards the invariant at the one level application code cannot
bypass.

`GET /api/v1/reconciliation` recomputes the sum from the ledger and reports the difference per
product. **Drift must be zero.** A non-zero drift means something wrote stock without writing a
movement — which is a bug, and this endpoint is how it gets found instead of discovered by a
customer.

## Consequences

- Every stock change has an audit trail with a reason and a reference, and reads stay a single
  indexed lookup.
- The rule "nothing writes `stock_on_hand` outside a transaction that also appends a movement" is
  a convention the compiler cannot enforce. Reconciliation is the check that it held, and
  `ImportAndReconciliationTests` deliberately writes stock without a movement to prove the endpoint
  catches it.
- Reconciliation over a wide window is the heaviest query in the system, which made it the right
  place to do a real optimization pass — see [`docs/query-optimization.md`](../query-optimization.md).
- Sale lines copy the SKU, name and unit price at the time of sale rather than joining to the
  product. Repricing a product must not silently rewrite what an old invoice says was charged.
