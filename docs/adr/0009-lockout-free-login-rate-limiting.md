# 0009. Rate-limit login without ever locking an account out

- **Status:** Accepted
- **Date:** 2026-09-08

## Context

#44 put a password on an internet-shaped surface, so repeated guessing has to be bounded. The
textbook answer is account lockout: after N failures, disable the account until an administrator or
a recovery flow reopens it.

That answer does not survive contact with this application, because of a decision #44 made
deliberately elsewhere: **there is no password recovery.** No reset e-mail — a homelab appliance
has no mail transport — no recovery token, no secret question. A LAN-reachable reset would be a
second authentication path weaker than the first.

Combine those two and lockout becomes an own goal. An attacker needs **no credential** to trigger
it; failing repeatedly is free and requires only reaching the port. The single operator of a
single-operator appliance would then be locked out of their own instance, permanently, with no
recovery path — a denial of service handed to anyone who can reach the port, defending against an
attack (online guessing) that a rate limit already defeats.

There is a second, quieter trap. The obvious way to make counters survive a restart is to persist
them, which means **an unauthenticated caller controls a database write on every failed login** —
trading a bounded in-memory DoS for an unbounded on-disk one.

## Decision

A fixed-window counter, held in memory, keyed per **username** *and* per **remote address**.
Within a 15-minute window: 5 failures per username, 20 per address. Exceeding either answers `429`.

Exceeding a limit **delays; it never disables.** The window drains on its own and the account is
never marked unusable. A successful login clears that username's counter, so an operator who
eventually remembers their password is not held out by the attempts that preceded it. The address
counter is deliberately *not* cleared by a success: one success from an address that has failed
nineteen times against nineteen different usernames is what a successful spray looks like, and
forgiving it would let an attacker reset their own budget by finally guessing right.

Both keys are needed and neither is sufficient. The per-username limit bounds guessing against one
account. The per-address limit bounds an attacker spraying one common password across many
usernames — an attack the per-username limit cannot see at all, because each username accrues only
one failure. The address limit sits well above the username limit so one fumbling operator never
trips it.

The check runs **before** credential verification, so a throttled attempt costs no KDF work.
Checking afterwards would bound guessing but leave CPU exhaustion open, since the expensive part
would already have run.

Counters are pruned as they expire. This is not tidiness: usernames come straight off an
unauthenticated request body, so without pruning an attacker posting a million distinct usernames
would pin a million counters in memory and turn the defence into the exhaustion vector it exists to
prevent.

## Alternatives rejected

- **Account lockout after N failures.** Rejected: with no recovery path it is a permanent,
  credential-free denial of service against the only operator. This is the standard answer and it
  is wrong *here* specifically because of the no-recovery decision.
- **Persisted counters that survive a restart.** Rejected: it gives an unauthenticated caller a
  database write per failed login. The restart-clears-counters limitation is real and accepted —
  an attacker cannot induce the restart, and an operator who can has better options than guessing
  their own password.
- **A sliding window.** Rejected: under a slow, persistent attacker the window creeps forward on
  every attempt and the budget never refills, which is lockout again by another name. A fixed
  window refills completely once it ends.
- **Per-username only.** Rejected: invisible to password spraying, which is the likelier attack
  against an appliance whose usernames are guessable.
- **Per-address only.** Rejected: trivially defeated from multiple addresses, and it punishes a
  shared NAT egress for one careless user.
- **A CAPTCHA or proof-of-work.** Rejected: disproportionate for a single-operator LAN appliance,
  and both add a dependency or a UX cost to defend an attack the counters already bound.

## Consequences

- A determined attacker gets ~20 guesses per username per hour. That defeats online guessing at any
  realistic password strength, which is what the 12-character minimum (the only other credential
  quality control) exists to guarantee.
- Counters reset on restart, so an attacker who could force restarts could reset their budget. They
  cannot, and an operator who can does not need to.
- The limiter is a singleton because the counters must outlive a request — a per-request limiter
  would count to one forever and defend nothing.
- **Behind a TLS-terminating reverse proxy the per-address budget collapses into one global
  counter**, because the address is the socket peer and every request then arrives from the proxy.
  One attacker can exhaust it and other operators see `429` until the window drains. This is a soft
  denial of service, not a lockout: the per-username limit still bounds guessing, and no account is
  ever disabled. Closing it requires `ForwardedHeaders` with `KnownProxies` populated — never
  unconditional trust in `X-Forwarded-For`, which would also let a remote caller present itself as
  local to the bootstrap and first-run-setup checks. README documents the trade for operators.
- Enforced by `AuthCsrfAndLogoutEndpointsTests`, which asserts both halves: the early failures answer `401` and
  the budget's exhaustion answers `429`. Asserting only that a `429` appears would pass against a
  limiter broken in the opposite direction — one that refused everything from the first attempt —
  so a companion test proves the correct password still works before the limit is reached.
