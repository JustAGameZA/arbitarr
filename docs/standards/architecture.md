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

Four checkable rules. Each is a mechanism, not a coincidence, and each would survive a tidy-up
looking like an improvement. The reasoning behind the admin-key ones is in
[ADR 0004](../adr/0004-admin-key-write-only-with-bootstrap-bypass.md) — do not restate it here.

- **Source API keys are write-only rows** under `source:{id}:api_key` — a colon-namespaced name no
  `SettingKey` enum value can produce.
- **`SourceRepository.ReadApiKeyForUpstreamRequestAsync` has exactly one caller.** The guarantee
  *is* the call-site count; a second caller is a second place to audit.
- **The admin key is session-only in Zustand** on the frontend — never `localStorage`, never
  `sessionStorage`, never a query string. CI rejects browser-storage references in the web project.
- **The admin key stays absent from `SettingsCatalog`**, which feeds both the settings PUT
  allow-list and its GET projection.

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
