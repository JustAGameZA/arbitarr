# Standards — Architecture

Rules about project boundaries, the route surface, and the mechanisms that keep secrets out of
responses. Each carries its reason, because each has a plausible-looking "simplification" that
breaks it.

---

## Project boundaries

**`Arbitarr.Ai` and `Arbitarr.Media` never reference each other, in either direction.** Data that
must cross moves through `Arbitarr.Core` contracts. Enforced by
`tests/Arbitarr.Architecture.Tests/AiMediaIsolationTests.cs`.

*Why:* identity resolution is deterministic and testable; LLM arbitration is not. Keeping them
apart is what makes the deterministic layer assertable without a model. See
[ADR 0001](../adr/0001-separate-ai-and-media.md).

**`Arbitarr.Core.Identity` does not reference `Api`, `Ai`, or `Media`.** Enforced by
`DependencyDirectionTests`. Contracts do not depend on their consumers.

---

## Route surface

**Every endpoint declares an explicit `RouteClassification`.** There is deliberately no overload
that defaults it (`src/Arbitarr.Api/Routing/RouteClassification.cs`).

*Why:* a defaulted classification means a new endpoint can ship
unauthenticated-but-mutating because its author forgot to opt in. Requiring the declaration makes
that a compile-time obligation rather than a review-time hope.

**Classify routes by `RouteClassification` or path prefix — never by HTTP verb.**

*Why:* verb-based gating silently breaks the GET-only Search and Suppressions surfaces, which are
admin surfaces that happen to read. The frontend applies the same rule: `apiFetch` attaches the
admin key by path prefix (`/api/admin/`).

**Admin route bodies bind optionally:**

```csharp
[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] T? request
```

...and are null-checked in the handler.

*Why:* a required body is bound **before** endpoint filters run, so a missing or malformed one
short-circuits to `400` without `AdminApiKeyFilter` ever executing. That lets an unauthenticated
remote caller tell a malformed body (`400`) from a well-formed one (`503`) — i.e. probe whether a
route exists. Binding optionally keeps the gate strictly first, so an unauthorised caller learns
only that they are unauthorised.

**`AdminApiKeyRouteEnumerationTests` sends no body on purpose.** It lives in
`Arbitarr.Integration.Tests`, not `Arbitarr.Api.Tests`. Adding a body is the obvious "fix" and it
destroys the test's ability to detect the leak above.

**The sweep skips every `{`-containing route** — a templated route needs a real value to resolve.
Templated routes therefore need explicit by-name gating tests in the matching endpoint test class
(e.g. `AdminApiKeyEndpointsTests`). **The sweep passing is not evidence for them.**

---

## Secrets mechanisms

Five checkable rules. Each is a mechanism, not a coincidence, and each would survive a tidy-up
looking like an improvement. The reasoning behind the admin-key ones is in
[ADR 0004](../adr/0004-admin-key-write-only-with-bootstrap-bypass.md); the reasoning behind
clearing is in [ADR 0010](../adr/0010-secrets-clear-route.md) — do not restate either here.

- **Source API keys are write-only rows** under `source:{id}:api_key` — a colon-namespaced name no
  `SettingKey` enum value can produce.
- **Every `ReadApiKeyForUpstreamRequestAsync` has exactly one caller.** The guarantee *is* the
  call-site count; a second caller is a second place to audit. `SecretReaderSingleCallerTests`
  counts them, because the comments claiming "the one place" stayed correct-looking while arb-u1c
  added a second caller beside them and three reviews read the comments instead of counting.
  - `SourceRepository`'s is called from `AdminSourceEndpoints`.
  - `ArrInstanceRepository`'s is called from `SonarrCredentialProvider`, and from nowhere else.
    **Two consumers, one reader**: the admin connectivity probe and the search path's identity
    resolver both need an authenticated request against the configured Sonarr, and both take a
    `SonarrCredential` from that provider rather than reading the key themselves. It lives in
    `Arbitarr.Data` because `Arbitarr.Api` and `Arbitarr.Media` both reference that project and
    neither may reference the other — so a second consumer costs a new consumer, never a new caller.
    That is the shape to reach for when a third arrives.
- **The admin key is session-only in Zustand** on the frontend — never `localStorage`, never
  `sessionStorage`, never a query string. CI rejects browser-storage references in the web project.
- **The admin key stays absent from `SettingsCatalog`**, which feeds both the settings PUT
  allow-list and its GET projection.
- **A secret is never readable and omission never clears it.** A secret is cleared only by
  deleting the thing that owns it, through a bodiless `DELETE` (e.g.
  `DELETE /api/admin/sources/{id}` removes a Sonarr-kind source's row and its stored key together).
  The admin key is the one secret with no owning row to delete, so it has no clear route at all —
  clearing it would reopen the LAN bootstrap bypass. Sources getting their own dedicated clear
  affordance (short of deleting the source) is deferred.

A related mechanism outside this list: **upstream redirect refusal** (SEC-M1, CONTEXT.md) refuses
any 3xx on the download path rather than following it, because following it would carry the
upstream key into a second, off-origin request. See
[ADR 0014](../adr/0014-refuse-upstream-download-redirects.md).

---

## Parsing user-supplied enums

**Do not use `Enum.TryParse` on a wire value that selects an authority level.**

*Why:* it accepts the numeric form, so `{"scope":"1"}` mints an `Admin` key through an input shape
no caller is documented to have.

Neither guard people reach for first closes it: `Enum.IsDefined` fails because `1` **is** defined,
and trimming fails because `" 1 "` and `"+1"` parse too. **Match the names explicitly**, so the
wire format is closed by construction.

---

## Logging

`IHttpClientFactory` attaches its own logging handler to every named client and logs the **full
absolute URI** at Information. Since #65 that lands in a persistent store served at
`/api/admin/logs`.

`LogMessageCleanser` scrubs credentials in **query strings**. A secret in a URL **path** (a webhook
token, say) is not covered — such registrations need `.RemoveAllLoggers()`.

*Why:* care taken inside a typed client cannot defend against a handler the container wraps around
it. The defence has to be at registration.

### Tier convention (arb-8ur7, from the #215 arch review)

**Debug per item; a rate-capped Warning per call; a Warning per batch; Information for a
whole-call outcome that is not a fault but would otherwise be invisible above Debug.** Each tier
has a worked instance rather than a copied snippet:

- **Debug per item, rate-capped Warning per call** —
  `ReleaseClassifier.TryClassifyAsync` (arb-s4lg): the caller-supplied `detailAtWarning` flag picks
  Warning or Debug per call; `ClassifierWorker` forwards it from `ClassifierPollingWorker`, which
  caps at `MaxDetailedFailuresPerCycle` (three) before demoting the rest to Debug.
- **Warning per batch** — `ClassifierPollingWorker.WarnOnFailures`: one Warning per cycle that had
  failures, carrying the by-type counts and the suppressed-detail count, none for a clean cycle.
- **Information for a whole-call outcome** —
  `SyncReleaseArbiter.LogBudgetExhaustionIfWholeSearchTimedOut`: one Information line when every
  candidate in a search returned `Unknown` with no transport failure counted, distinguishing "the
  whole search's budget was exhausted" from "nothing to report" — a case that is correctly Debug
  per candidate (see [Error handling](#error-handling-in-background-and-maintenance-work) below)
  but would otherwise carry zero signal above Debug in aggregate.

**#296 caveat:** that Information line fires identically for a single-candidate search that
legitimately times out. This is accepted on purpose — gating it on candidate count greater than
one would silently remove the only signal for a genuinely exhausted single-candidate ad-hoc
search.

*Why Information, not Warning:* everything at Information and above lands in the persistent SQLite
log store (CLAUDE.md's secrets section, `LogStore.DatabaseFileName`), so choosing a tier is also
choosing what accumulates there. A per-call or per-batch event promoted to Information would flood
that store; a whole-call outcome that happens once per search does not.

---

## Error handling in background and maintenance work

This convention governs REPEATING per-item work where a skipped item is recoverable on the next
pass. It does not cover one-shot startup work that establishes an invariant the process cannot
serve requests without: `Program.cs`'s startup migration catches only to wrap the raw EF/SQLite
exception before re-throwing ("A failure here is always fatal to startup — there is no safe way to
serve requests against a database that isn't at the expected schema version"), which is the
opposite of swallow-and-continue. `StagingSweepService` draws the same line from its own side: its
swallow is justified precisely because the sweep is not invariant-establishing ("an orphan left in
place for one more run is a much smaller problem than the host refusing to start").

**A failure that affects one item is caught broadly (`catch (Exception)`) and logged, so the
remaining items still run.** Only cancellation propagates, via a
`catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)` guard placed
*before* the broad catch. The rule exists because an unhandled exception out of `ExecuteAsync`
faults the `BackgroundService`, and `BackgroundServiceExceptionBehavior.StopHost` — the default
since .NET 6, and not overridden anywhere in `src/` — turns that fault into host shutdown, so the
consequence of not catching is a dead process, not a skipped item.

Reference shape (illustrative, not exhaustive — `NotificationHostedService.ExecuteAsync` and
`ClassifierPollingWorker.ExecuteAsync` follow the same shape and are not listed below):
`MaintenanceHostedService.ExecuteAsync`
wraps each of its three per-pass jobs — the main-database maintenance job, the log-database trim,
and the automatic backup — in its own OCE-guard-then-broad-catch pair, so a failure in one cannot
stop the other two. `RefreshWorker` applies the same shape twice: once per cycle
(`RefreshWorker.ExecuteAsync`) and once per source inside
`RefreshWorker.RefreshOneAsync`, using a bare
`catch (OperationCanceledException) when (...) { throw; }` ahead of the broad catch, since a
returned `bool` rather than `break` carries the per-entry failure.

`SqliteLoggerProvider.DrainAsync`'s swallow of the store write is the same convention applied
without an OCE guard — but not because the pump has no token to check: `DrainAsync` does hold a
`cancellationToken` and passes it straight through to `_store.WriteAsync(batch, cancellationToken)`.
The reason is the one its own comment gives: this is a log sink, and the one thing a log sink must
never do is turn "could not record a problem" into a new, louder problem. So the catch stays broad
and unconditional even though a cancellation token is in scope — the pump swallows cancellation
along with everything else rather than letting it propagate.

**A narrow type filter on per-item background work (e.g. `catch (IOException or
UnauthorizedAccessException)`) is the exception and needs a stated justification in a comment.**
`StagingSweep.Run`'s two per-file catches already use the broad `catch (Exception)` form, not a
narrow type filter — #168 widened them to match this convention, so `Run` is the "no token, so no
guard yet" example, not a narrow-filter example: it takes no cancellation token today, so it has no
OCE guard, and if one is ever threaded through, the guard must be added ahead of the existing broad
catches.

Where CONTRIBUTING.md or another standards doc states a general exception-handling rule, link to
it rather than repeating it here; none currently does, so this section is the only statement of the
background-work convention.

---

## Shutdown ordering

**.NET stops hosted services in the reverse of their registration order.** A service whose
`StopAsync` drains work to the database must therefore be registered *before* the DB-touching
background services it needs to outlive — registering it later would stop it first, while those
services are still writing.

The worked instance is `ThrottledApiKeyLastUsedRecorder` and `ThrottledSessionActivityRecorder`
(arb-acy9, #300): `Program.cs` registers both as hosted services ahead of
`NotificationHostedService`, `MaintenanceHostedService` and `StagingSweepService`, so their
`StopAsync` drain runs *after* those three have already stopped, once nothing else is contending
for SQLite's single writer — including the maintenance service's own backup job.

`ArbitarrDbContext` is registered `AddScoped`, not `AddDbContextPool`, so there is no pooled
connection teardown to race against the drain. Introducing `AddDbContextPool` later would need
this section revisited.

Each recorder's drain is bounded by its own `DrainTimeout` (5s), weighed against the host's
un-overridden 30s `ShutdownTimeout` that all hosted services share — a third such drain added to
the shutdown path spends from the same 30s budget.
