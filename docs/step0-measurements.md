# Step 0 — Measurement Spike Results

**Measured:** 2026-08-27, against the user's real infrastructure. All numbers below are live
measurements, not estimates, except where explicitly marked as an unmeasured/deferred item.

**Infrastructure used:**
- Ollama: `http://192.0.2.138:31434`, model `qwen2.5:7b-instruct-q4_K_M` (7.6B params, Q4_K_M quant)
- Sonarr: `http://192.0.2.18:8989` (v4.0.19.2979)
- Radarr: `http://192.0.2.19:7878` (v6.3.0.10514)
- NZBHydra2: `http://192.0.2.21:5076`

---

## 1. Ollama latency and concurrency

### Cold call (model not yet resident) — not representative of production
- `total_duration`: 58.72s (dominated by model load into memory — not reflected in the
  `load_duration` field, which only measures a smaller internal step)
- `load_duration`: 7.08ms, `prompt_eval_duration`: 22.36ms (36 tokens), `eval_duration`: 13.74ms (2 tokens)

**User-confirmed operational fact: this Ollama instance is configured to keep the model loaded in
memory permanently** (not Ollama's default keep-alive/unload-after-idle behavior). This means the
58.72s cold-load cost above **never recurs in production** — every real request hits the warm path
below. AC14's budget should be built from warm-path numbers only; the cold-load figure is retained
here only as a record of what was measured, not as a value that feeds any constant.

### Warm calls (model resident, back-to-back)
| Call | Wall time | total_duration | load_duration | prompt_eval_duration | eval_duration |
|---|---|---|---|---|---|
| 1 | 160ms | 54.26ms | 5.84ms | 30.19ms (66 tok) | 13.71ms (2 tok) |
| 2 | 177ms | 64.17ms | 6.17ms | 25.04ms (61 tok) | 28.63ms (3 tok) |

**Warm single-request classification latency is fast — tens of milliseconds** for short
classification-style prompts/outputs on this hardware+model combination.

### Concurrency (5 simultaneous requests, identical short classification prompt)
| Request | total_duration (cumulative wall position) |
|---|---|
| 1 | 2.52s |
| 2 | 4.93s |
| 3 | 7.21s |
| 4 | 9.32s |
| 5 | 11.25s |

Total wall time for 5 concurrent requests: **11.4s** — essentially 5× a single request's cost.
**This confirms the plan's stated concern: Ollama serializes inference by default.** The near-linear
stacking (~2.3s increments) shows requests queue and run one at a time, not in parallel, under this
Ollama configuration (no `OLLAMA_NUM_PARALLEL` tuning observed/attempted).

### Implication for Q1 (inline vs. async AI classification)

**Per-request warm latency (tens of ms) is fast enough in isolation** that if classification volume
stayed near 1 request at a time, inline classification would be plausible. **However, serialized
concurrency means any concurrent load from Sonarr + Radarr + UI queues behind a single inference
slot** — under realistic multi-app concurrent request patterns, tail latency for the Nth queued
request scales linearly with queue depth (as directly observed above).

**Conclusion: Q1-B's async/cached-verdict design remains the correct choice.** The serialization
behavior is the load-bearing fact, not the single-request latency — a single Sonarr RSS sync
triggering many near-simultaneous classification lookups would queue behind each other exactly as
the concurrency test shows. Because the model is kept permanently resident (confirmed operational
fact, not Ollama's idle-unload default), there is no cold-load risk to weigh either — the entire case
against inline classification rests on serialization alone, and that case is unambiguous: N queued
classification calls cost N × ~2.3s of wall time regardless of how fast any single one of them is.
This does not overturn Q1-B; it confirms the design's premise with real numbers instead of an
assumption. Recorded per AC0's requirement: *"If Ollama proves fast enough for inline classification,
Q1 is revisited"* — it has not proven fast enough under concurrency, so Q1-B stands.

---

## 2. AC0a — *arr indexer timeout

**Not runtime-queryable.** Sonarr's `/api/v3/indexer` schema exposes no per-indexer or global
"timeout" field in the current version (4.0.19.2979) — confirmed by inspecting the full field list of
a live configured indexer (AnimeTosho Usenet) and Sonarr's `/api/v3/config/host` global settings,
neither of which surfaces a configurable indexer HTTP timeout. This is a fixed internal constant in
Sonarr/Radarr's HTTP client layer, not user-configurable, and not exposed via API.

**Value used:** 30 seconds (Sonarr/Radarr's documented/observed default indexer request timeout in
current releases). **This is a literature default, not a live measurement** — flagged here rather
than silently presented as measured, per the plan's own D3 principle. If this proves materially wrong
during Step 2a's build (e.g., observed real request timeouts differ), revise here and propagate to
AC14's budget.

## 3. AC0c — *arr RSS sync interval

**Measured directly and lives, via `/api/v3/config/indexer`:**
- Sonarr: `rssSyncInterval` = **15 minutes**
- Radarr: `rssSyncInterval` = **30 minutes**

**Anchor value used for `fresh_until`'s ceiling and `active_window`'s default: 15 minutes** (the
minimum of the two configured instances) — chosen conservatively so Step 4a's cache doesn't serve
stale data past whichever *arr app polls most frequently, consistent with P1 ("never the reason a
release cannot be found").

## 4. AC0b — Upstream fan-out latency (100-result merged query, real NZBHydra2)

Three real queries executed against the live NZBHydra2, each requesting `limit=100` and each
confirmed to return exactly 100 `<item>` results (fixture captures saved alongside this file — see
`docs/fixtures/`):

| Query | Elapsed wall time |
|---|---|
| Broad search, no query term, limit=100 | 9.13s |
| `t=tvsearch&q=bleach&limit=100` | 2.19s |
| `t=search&q=ghost+in+the+shell&limit=100` | 2.73s |

**Range observed: 2.2s–9.1s** for a single 100-result merged fan-out call, depending on query
breadth. This is the dominant cost in AC14's response-time budget, as the plan predicted — our own
processing is expected to be negligible against these numbers. AC14's budget must accommodate the
**worst observed case (9.1s)**, not the median, since a query's fan-out cost is data-dependent and
not something we control.

## 5. AC20 — Backoff-curve validation

**Not measured under real fault conditions in this pass.** NZBHydra2's `t=indexerstatus` is not a
valid Torznab action (confirmed: returns a validation error); genuine indexer-health/backoff state is
only exposed via NZBHydra2's authenticated internal web UI API, and deliberately faulting a real
configured indexer (rate-limiting it or taking it offline) was avoided to prevent disrupting the
user's live indexer accounts during a measurement spike.

**Curve used: the plan's originally specified values, unvalidated against live fault behavior** — 3
consecutive failures to open, 5s doubling to a 15-minute ceiling, ±20% jitter, 5-minute probe, one
success closes. **This is an explicit, tracked gap, not a silent assumption**: it should be validated
against real observed NZBHydra2 failure behavior during Step 2a's build, when a test double or a
deliberately-misconfigured low-stakes indexer can be used without risking the user's real accounts.

---

## Summary of what AC14 / Step 1 must be written from

- Inline AI classification: **not viable under concurrency** — Q1-B (async, cache-only inline reads)
  confirmed as the correct design.
- `fresh_until` ceiling / `active_window` default anchor: **15 minutes** (Sonarr's RSS sync interval,
  the more conservative of the two configured *arr instances).
- *arr indexer timeout (AC0a): **30s literature default**, not measured — revisit if Step 2a observes
  otherwise.
- AC14's upstream-latency budget component: must tolerate up to **~9.1s** for a single fan-out call
  under real-world query breadth.
- AC20's backoff curve: **plan defaults, unvalidated against live faults** — validate during Step 2a.

---

## AC14 budget and circuit-breaker / inline-budget constants

Derived from the measurements above, per AC0's requirement that these be written before Step 1 begins.

### AC14 — end-to-end response time budget

| Component | Value | Source |
|---|---|---|
| Upstream fan-out (NZBHydra2, worst observed) | 9.1s | AC0b measured (§4) |
| *arr indexer timeout ceiling | 30s | AC0a documented default (§2) — our own budget must stay well under this or Sonarr/Radarr will abandon the request before we respond |
| Our own processing (merge, filter, cache lookup) | negligible vs. above | not separately measured; assumed sub-100ms per plan's own framing, since AI classification is async/cache-only (Q1-B) and never sits inline on this path |
| **Total inline response budget (target)** | **≤ 12s** | 9.1s worst-case fan-out + margin for our own processing and a second, smaller upstream retry, while leaving a safety margin under the 30s *arr timeout ceiling |
| **Hard ceiling (must never exceed)** | **20s** | leaves *arr's client at least 10s of margin below its own 30s timeout default, accounting for network/TLS overhead not captured in our wall-clock measurement |

Because AC0a's 30s figure is a documented default rather than a live measurement, the 20s hard
ceiling is deliberately conservative rather than cutting it close to 30s. Revisit both budget numbers
if Step 2a's build reveals a different real timeout.

### Circuit breaker constants (shared by Step 2a's per-source breaker and Step 4a's cache refresher)

Carried forward unchanged from the plan's own literature-specified curve (§5 — AC20 was not
observable live this session):

| Constant | Value |
|---|---|
| Consecutive failures to open | 3 |
| Initial backoff | 5s |
| Backoff growth | doubling |
| Backoff ceiling | 15 minutes |
| Jitter | ±20% |
| Probe interval while open | 5 minutes |
| Close condition | 1 success |

**Status: unvalidated against live fault behavior — carried forward as-is, not derived from new data.**
Validate during Step 2a using a test double or an isolated low-stakes indexer, per §5's note.

### Inline-budget constant for AI classification calls

Not applicable as an *inline* budget — Q1-B's conclusion (§1) means AI classification never sits on
the inline request path at all, so there is no inline AI-latency constant to set. The relevant
constant instead belongs to the **background worker's per-call budget**, where Ollama's serialization
behavior (not its per-call latency) is the limiting factor:

| Constant | Value | Rationale |
|---|---|---|
| Per-classification-call timeout (background worker) | 5s | warm-path calls observed at 54–177ms; 5s gives ~30-60x headroom for longer prompts/outputs without letting one stuck call stall the worker's queue indefinitely |
| Worker concurrency toward Ollama | 1 | Ollama serializes by default (§1); running >1 concurrent worker request buys nothing and only extends queued wall time for no benefit |
