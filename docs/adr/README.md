# Architecture decision records

Short records of choices that were not obvious, kept because the reasoning is worth more than the
outcome — anyone can see *what* was built by reading the code; these say what the alternative was
and what it cost to turn it down.

| # | Decision | The trade |
| --- | --- | --- |
| [0001](0001-vertical-slices-over-layers.md) | Vertical slices, no repository or mediator | No seam to swap Postgres; the endpoint reads as one story |
| [0002](0002-the-ledger-is-the-truth.md) | Append-only ledger, stock on hand is a cache | An invariant the compiler can't enforce; reconciliation proves it held |
| [0003](0003-money-and-time.md) | `numeric(18,2)` and `timestamptz`, always UTC | `numeric` is slower than a float, and correct |
| [0004](0004-keyset-pagination.md) | Keyset cursors instead of `OFFSET` | No page numbers and no total count |
| [0005](0005-migrations-on-startup.md) | Migrate on startup only with one instance | The ECS path needs a pipeline step that does not exist yet |
| [0006](0006-authentication.md) | JWT with two roles, accounts from configuration | Not an identity provider, and deliberately not pretending to be |

Related measurements rather than decisions: [`../query-optimization.md`](../query-optimization.md)
(a reconciliation query taken from 37 ms to 2 ms, with the plans) and
[`../../infra/README.md`](../../infra/README.md) (what the AWS stack contains, what it would cost,
and how far it was actually taken).
