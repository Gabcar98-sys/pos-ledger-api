# 4. Keyset pagination, not `OFFSET`

**Status:** accepted · **Date:** 2026-08-06

## Context

`?page=7&size=50` is the familiar shape, and it is wrong in two ways that only show up once there
is data and traffic.

It gets slower the deeper you go. `OFFSET 10000` makes Postgres read and discard ten thousand rows
before returning anything; the last page of a catalogue costs the most to fetch.

Worse, it is inconsistent under writes. If a product is inserted while a client is between page 2
and page 3, every subsequent row shifts by one: the client silently skips a product it never saw.
On a catalogue that is being imported into — which is the entire point of the imports endpoint —
that is not a theoretical risk.

The usual companion, a `totalCount`, means a second `COUNT(*)` over the same predicate on every
single page request, to render a number nobody acts on.

## Decision

Keyset ("seek") pagination. The client sends `?after=<cursor>&limit=<n>`; the cursor is the sort
key of the last row it received. The query becomes `WHERE sku > @after ORDER BY sku LIMIT @n`,
which is an index range scan — the same cost on page 200 as on page 1.

`Page<T>` carries `items` and `nextCursor`, and nothing else. No page number, no total count.
`nextCursor` is `null` exactly when there are no more rows, which is determined by fetching
`limit + 1` rows and discarding the extra — no second query.

The sort key is `sku`, which is unique, so no tie-break column is needed and the unique index that
already enforces the business key also serves the page. No extra index.

## Consequences

- Constant-time pages, and a client that walks the whole catalogue never skips or repeats a row
  even while imports are running.
- No "jump to page 47" and no "1,204 results". Both would need a different mechanism; neither is
  something a POS terminal does. A UI that needs a result count should ask for one explicitly, on
  an endpoint that is honest about costing a full scan.
- The cursor is a real column value, so it is readable and debuggable — `?after=FLT-100` says what
  it means. It is not opaque, and it does not need to be: the catalogue is public.
- Sorting by anything other than `sku` would need the cursor to carry that column plus a unique
  tie-break. Not supported, because nothing asks for it yet.
