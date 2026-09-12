# 0018. One credential-provider type per secret family, and the provider is the single caller

- **Status:** Accepted
- **Date:** 2026-09-12

## Context

CLAUDE.md §1 requires that `SourceRepository.ReadApiKeyForUpstreamRequestAsync` have exactly one
caller. That rule has held so far by accident of there being exactly one *consumer*: the admin
connectivity probe. The search path never read a per-source key at all, because `SourceSeeder`
resolved the single configured source's key **once at startup** into `ResolvedSourceConfiguration`,
and `NzbHydraSource` was handed the resolved value.

That startup-resolution trick does not survive N indexers. Once an operator can add, re-key or
remove an indexer from the admin UI at runtime, "restart to pick up the key" is not an acceptable
answer, so the search path must read a per-source key on the request path. A second consumer of the
stored key is therefore unavoidable.

Wiring that consumer the obvious way — having it call the repository's reader itself — produces two
callers of `ReadApiKeyForUpstreamRequestAsync`, which is precisely the count the guarantee is made
of. The guarantee *is* the call-site count: a second caller is a second place to audit, and
`SecretReaderSingleCallerTests` exists because in arb-u1c the comments claiming "the one place"
stayed word-for-word correct-looking while a second caller was added beside them, and three reviews
read the comments instead of counting.

So the rule had to be **deliberately extended rather than quietly broken**, and the precedent for
extending it was already in the repository: `ArrInstanceRepository.ReadApiKeyForUpstreamRequestAsync`
has its own single caller, `SonarrCredentialProvider` — a provider *type* interposed between the
repository and its two consumers (the Sonarr connectivity probe and the search path's identity
resolver), neither of which reads the key itself.

## Decision

The single-caller rule is generalised from "one caller" to **exactly one caller per
credential-provider type**: one provider type per secret family, and that provider is the sole
caller of its family's reader.

Concretely:

- `SourceCredentialProvider` (`src/Arbitarr.Data/Sources/SourceCredentialProvider.cs`) is the only
  production call site of `SourceRepository.ReadApiKeyForUpstreamRequestAsync`, mirroring what
  `SonarrCredentialProvider` already is for `ArrInstanceRepository`'s reader.
- It returns `SourceCredential(BaseUrl, ApiKey)`, or `null` for a half-configured source — no row,
  no base URL, or no key. `null` rather than a credential with an empty key, because every consumer
  wants the same thing from that state: an unauthenticated request would be answered 401 and record
  an authentication failure against a source whose key was simply never entered. Not asking is
  cheaper and honest about what is missing.
- `SourceCredential` is **not a way to read the key**; it is the value the single reader produces.
  It is handed only to code about to issue a request at its `BaseUrl`, never logged, never returned
  to a caller, never interpolated into an error message or a probe outcome.
- The admin connectivity probe (`AdminSourceEndpoints.TestSourceAsync`) no longer reads the key. It
  takes the credential from the provider and unpacks `BaseUrl`/`ApiKey` into
  `SourceConnectivityProber.ProbeAsync`'s existing parameters, so no probe signature gains a field
  that could carry key-derived text back out. **`SourceProbeOutcome` stays a closed enum with no
  free-text field** — that is the mechanism preventing a probe failure from reporting anything
  derived from the key, and it is not widened here.

A new consumer of a source key costs a new *consumer*, never a new caller. That is the shape to
reach for when the direct-indexer search path arrives.

### Also recorded: why the release lookup stays stateful

Prowlarr's `DownloadMappingService` takes the opposite approach to Arbitarr's `IReleaseLookup`
(ADR 0015): `ConvertToProxyLink` **encrypts the upstream URL into the link the client holds** and
`ConvertToNormalLink` decrypts it on the way back, so the mapping is stateless — the link *is* the
state. This was considered as a way to avoid storing per-release rows at all, and rejected.

The encrypted-link form puts the upstream URL — *including the indexer API key* — into a value the
client holds, protected only by a symmetric key. That trades a stored row for a cryptographic
dependency on the request path, and the dependency has failed in the field: Prowlarr's issue tracker
carries reports of `Specified key is not a valid size for this algorithm`, which breaks **every**
search rather than degrading one. Arbitarr's opaque GUID handle carries no upstream material at all,
so a leaked link discloses nothing and revoking one is a row delete rather than a key rotation that
invalidates every outstanding link at once.

This is a genuine alternative with a real advantage (no storage, no expiry bookkeeping), not a
restatement of ADR 0015 — it is recorded here because it is the one design that would have removed
the per-source key from the process entirely, and the reason it loses is a secrets reason.

## Alternatives rejected

- **Generalise `SourceRepository` and `ArrInstanceRepository` into one repository type parameterised
  over a credential family.** Rejected for the same reason arb-6l9b.1's brief rejected it for
  Radarr/Sonarr: each secret family carries family-specific reasoning — `source:{id}:api_key`'s
  colon-namespaced write-only row and its per-id lifetime, versus the Sonarr instance's single
  address-and-key pair — that a generic type would either have to lose or carry conditionally. A
  conditional inside the one type that reads secrets is strictly worse to audit than two types that
  each read one thing.

- **Let the search path call `ReadApiKeyForUpstreamRequestAsync` directly, alongside the probe.**
  Rejected because that is precisely the shape `SecretReaderSingleCallerTests` exists to catch. It
  is also exactly what happened in arb-u1c on the Sonarr side, and the cost was not hypothetical:
  the comments asserting "the one place" remained in the file and read as true, so the reviews that
  should have caught the second caller read the comment rather than counting the call sites.

- **Keep resolving keys at startup and require a restart after adding an indexer.** Rejected: it
  preserves the single-caller count by making the product worse. An operator adding an indexer and
  seeing no results until a restart has no way to tell that from a misconfigured key.

- **Return an empty-keyed `SourceCredential` for a half-configured source instead of `null`.**
  Rejected: it pushes the "is this usable" decision to every consumer and invites each to answer it
  differently, and the one thing they must agree on is not sending an unauthenticated request that
  records a false authentication failure.

## Consequences

- `SecretReaderSingleCallerTests.AllowedCallSites` names
  `src/Arbitarr.Data/Sources/SourceCredentialProvider.cs` as `SourceRepository`'s sole caller. The
  entry **moved** rather than gaining a sibling; a second row for the same reader would defeat the
  rule the table encodes. The test's positive-control loop asserts each allowed site is actually
  found, so a scan that stopped matching fails loudly instead of passing vacuously.
- CLAUDE.md §1's single-caller bullet is generalised to state the rule per credential-provider type
  and to point here. `docs/standards/architecture.md`'s secrets-mechanisms list already stated the
  rule in the general form ("Every `ReadApiKeyForUpstreamRequestAsync` has exactly one caller") and
  now names the provider for the source key.
- `SourceRepository.ReadApiKeyForUpstreamRequestAsync`'s "THE RULE THIS DOES NOT RELAX" comment is
  updated to name `SourceCredentialProvider` as the caller. It is updated, not deleted: the comment
  is what tells the next reader why the indirection exists, and deleting it is how the indirection
  gets "tidied" back into a direct call.
- What must stay true: `SourceProbeOutcome` remains a closed enum with no free-text field, and the
  provider remains the only place that turns a stored key into a value another type can hold.
