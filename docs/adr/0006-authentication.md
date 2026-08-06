# 6. JWT with two roles, and no identity provider of my own

**Status:** accepted · **Date:** 2026-08-06

## Context

The API needs authorization: a cashier may sell, an admin may change the catalogue and import
stock. Something has to issue and verify credentials.

The tempting move is to build it out — a users table, registration, email confirmation, password
reset, refresh token rotation. That is how portfolio projects acquire real security holes, because
each of those flows has failure modes that take a specialist to get right, and none of them is what
this repository is trying to demonstrate.

The opposite move — no auth at all, or a hardcoded `if (apiKey == "secret")` — makes every endpoint
below it untrustworthy, and hides the parts that genuinely have to be right.

## Decision

A single endpoint, `POST /api/v1/auth/token`, exchanging credentials for a signed JWT with a role
claim. Two roles, `admin` and `cashier`, enforced with `RequireAuthorization`. Accounts come from
configuration, not from a users table. **In a real deployment this endpoint is replaced by the
client's identity provider** and everything downstream is unchanged, because downstream only ever
sees a bearer token and a role claim.

What is *not* skipped, because it has to be right whoever issues the tokens:

- **Passwords are stored as PBKDF2-SHA256 hashes**, 100,000 iterations, 16-byte random salt, in the
  form `iterations.salt.hash` so the work factor can be raised later without invalidating existing
  hashes. Never in clear — not even for a demo, where it would cost nothing and teach the wrong
  thing.
- **Verification is constant-time** (`CryptographicOperations.FixedTimeEquals`). A plain
  `SequenceEqual` leaks how many leading bytes matched, which is enough to reconstruct a hash one
  byte at a time.
- **An unknown username is verified against a decoy hash**, so a request for an account that does
  not exist costs the same time as one that does. Otherwise response latency answers "does this
  account exist?" for free.
- **The failure response says only "username or password is incorrect"**, and the log records the
  username but never the password.
- **The development signing secret is refused outside Development.** `JwtOptions` is validated with
  `ValidateOnStart()`: the app will not boot with the value shipped in `appsettings.json`, or with
  a secret shorter than 32 characters, in any environment other than Development and Testing. A
  misconfiguration that would let anyone forge an admin token becomes a startup crash instead of a
  silent hole.

The demo passwords are published in the README, deliberately. This is a public demo with a public
database; pretending otherwise would be theatre.

## Consequences

- Tokens are self-contained and expire in an hour. There is no refresh token and no revocation
  list: revoking early would need shared state, which is the identity provider's job.
- Adding a third role is a constant and an attribute.
- Reading the catalogue needs no token — a price list is not a secret, and it keeps the demo usable
  without signing in. Every write does.
- If this were to carry real accounts, the whole `Auth` section is deleted and replaced with the
  IdP's authority and audience. That is the seam this design is buying.
