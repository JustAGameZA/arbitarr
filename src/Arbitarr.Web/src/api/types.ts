/**
 * TypeScript mirrors of the server's response records.
 *
 * These were written from the C# records on `origin/master`, NOT from the legacy
 * wwwroot JS, which is stale against them in at least three places (see
 * surfaces/Dashboard/queries.ts and surfaces/Search/Search.tsx for the specific
 * fields). The wire shape itself was settled empirically rather than assumed:
 * Program.cs registers no AddJsonOptions and no JsonStringEnumConverter, so
 * Minimal API responses serialize with JsonSerializerDefaults.Web, which means
 *
 *   - property names are camelCase;
 *   - a plain enum is a NUMBER, not its name  (hence CacheBand below);
 *   - a TimeSpan is the string "00:01:30.5000000", not a count of seconds;
 *   - a DateTimeOffset is ISO-8601 with an offset.
 *
 * The two enum-shaped fields differ on purpose and the difference is the
 * server's, not a mistake here: `cacheBand` is a real C# enum and arrives as a
 * number, while `aiVerdict` is projected with `verdict.ToString()` in
 * AdHocSearchEndpoint.cs and therefore arrives as a NAME. Unifying them in the
 * client would misread one of the two.
 */

/** Error bodies from the admin endpoints are uniformly `{ error: string }`. */
export interface ApiErrorBody {
  error: string;
}

/**
 * Narrows an unknown ApiError body to the server's reason string.
 *
 * AC9/AC10 require the SERVER's rejection to be displayed verbatim, so this is
 * deliberately the only path by which a failure message reaches a surface: when
 * the server supplied a reason we show exactly that, and we fall back to the
 * generic message only when it did not.
 */
export function serverReason(body: unknown, fallback: string): string {
  if (typeof body === 'object' && body !== null && 'error' in body) {
    const { error } = body as { error: unknown };
    if (typeof error === 'string' && error !== '') {
      return error;
    }
  }
  return fallback;
}

// --- Dashboard (public, no admin key) -------------------------------------

/** StatusEndpoint.cs — SourceStatus. */
export interface SourceStatus {
  sourceName: string;
  state: string;
  consecutiveFailures: number;
  lastError: string | null;
}

/** StatusEndpoint.cs — WorkerHealthResponse. */
export interface WorkerHealth {
  enabled: boolean;
  lastCycleStartedUtc: string | null;
  lastCycleCompletedUtc: string | null;
  lastCycleCandidates: number;
  lastCycleRefreshed: number;
  lastCycleFailed: number;
  lastError: string | null;
  consecutiveFailedCycles: number;
}

/** StatusEndpoint.cs — StatusResponse. */
export interface StatusResponse {
  status: string;
  sources: SourceStatus[];
  worker: WorkerHealth;
}

/** RecentSearchLog.cs — RecentSearchEntry. */
export interface RecentSearchEntry {
  receivedAt: string;
  query: string;
  resolvedIdentity: string | null;
  resultCount: number;
  elapsedMilliseconds: number;
  band: string | null;
}

/** ConfigProjection.cs — EffectiveConfigResponse. */
export interface EffectiveConfigResponse {
  nzbHydraConfigured: boolean;
  freshUntilSeconds: number;
  serveUntilSeconds: number;
  activeWindowSeconds: number;
  refreshLeadSeconds: number;
  workerCycleIntervalSeconds: number;
  workerEnabled: boolean;
  querySnapshotTtlSeconds: number;
  shadowMode: boolean | null;
}

// --- Ad-hoc search --------------------------------------------------------

/**
 * Core.Caching.CacheBand, serialized as its NUMERIC value.
 *
 * Declared as a const object rather than a TS `enum` so the numbers are visible
 * at the point they are named; a bare number in the UI would be unreadable and a
 * string comparison would never match.
 */
export const CACHE_BAND_LABELS: Record<number, string> = {
  0: 'Fresh',
  1: 'Stale',
  2: 'Expired',
};

/** AdHocSearchEndpoint.cs — AdHocReleaseResponse. */
export interface AdHocRelease {
  title: string;
  guid: string;
  size: number;
  category: number[];
  sourceName: string;
  pubDate: string;
  /** 'Unknown' | 'Accept' | 'Reject', or null when the AI opt-in was not requested. */
  aiVerdict: string | null;
}

/** AdHocSearchEndpoint.cs — AdHocSearchProvenanceResponse. */
export interface AdHocSearchProvenance {
  /** A TimeSpan string such as "00:01:30.5000000", or null when nothing was cached. */
  cacheAge: string | null;
  /** Numeric — see CACHE_BAND_LABELS. */
  cacheBand: number;
  rateLimitedSources: string[];
}

/** AdHocSearchEndpoint.cs — AdHocSearchResponse. */
export interface AdHocSearchResponse {
  releases: AdHocRelease[];
  provenance: AdHocSearchProvenance;
}

/** MatchExplanationEndpoint.cs — MatchExplanationResponse. */
export interface MatchExplanation {
  title: string;
  originalTitle: string;
}

// --- Rules ----------------------------------------------------------------

/** AdminRuleEndpoints.cs — FilterRuleResponse. */
export interface FilterRule {
  id: number;
  name: string;
  isAllow: boolean;
  pattern: string;
  precedence: number;
  enabled: boolean;
}

/** AdminRuleEndpoints.cs — UpsertFilterRuleRequest. */
export type UpsertFilterRuleRequest = Omit<FilterRule, 'id'>;

/** AdminRuleEndpoints.cs — TestFilterRuleRequest (no `enabled`; it carries a `title`). */
export interface TestFilterRuleRequest {
  name: string;
  isAllow: boolean;
  pattern: string;
  precedence: number;
  title: string;
}

/** AdminRuleEndpoints.cs — TestFilterRuleResponse. */
export interface TestFilterRuleResponse {
  verdict: string;
}

// --- Settings -------------------------------------------------------------

/**
 * AdminSettingsEndpoints.cs — SettingCatalogEntryResponse.
 *
 * All thirteen fields, including the four the legacy admin-settings.js never
 * rendered (noMaximumReason, restartReason, governedTable, governedTableRows).
 * AC10 requires the rationale for each bound and a stated reason where a value
 * has no maximum, which is exactly what those fields carry.
 */
export interface SettingCatalogEntry {
  key: string;
  group: string;
  displayName: string;
  rationale: string;
  requiresRestart: boolean;
  isBoolean: boolean;
  /** Always a string on the wire, in the same form the PUT accepts back. */
  value: string;
  min: string | null;
  max: string | null;
  noMaximumReason: string | null;
  restartReason: string | null;
  governedTable: string | null;
  governedTableRows: number | null;
}

// --- Suppressions ---------------------------------------------------------

/** SuppressionViewEndpoint.cs — SuppressionViewEntryResponse. Served as a bare array. */
export interface SuppressionViewEntry {
  occurredAt: string;
  releaseIdentifier: string;
  queryKey: string;
  /** The layer that acted: a rule name, or a label such as "ai"/"pass". */
  layer: string;
  reason: string;
  shadowMode: boolean;
}

// --- System ---------------------------------------------------------------

/** ObservabilityCounters.cs — HitRate. `rate` is a computed C# property; null until there is traffic. */
export interface HitRate {
  hits: number;
  misses: number;
  rate: number | null;
}

/** ObservabilityCounters.cs — SearchCacheStats: cache reads split by band. */
export interface SearchCacheStats {
  freshHits: number;
  staleButValidHits: number;
  fetchedMisses: number;
  degradedMisses: number;
  hitRate: number | null;
}

/**
 * ObservabilityCounters.cs — ObservabilitySnapshot. Process-lifetime counters,
 * reset on restart.
 *
 * The two dictionaries are open-ended maps whose KEYS are server-authored and
 * are NOT camel-cased: `suppressedBySourceAndReason` is keyed like
 * "DenyRule:no-cam" and `servedAgeDistribution` like "1m-5m". Only the C#
 * property names go through the camelCase policy; dictionary keys are data.
 */
export interface ObservabilitySnapshot {
  resultsIn: number;
  suppressedTotal: number;
  suppressedBySourceAndReason: Record<string, number>;
  llmCalls: number;
  llmFailures: number;
  verdictCache: HitRate;
  searchCache: SearchCacheStats;
  servedAgeDistribution: Record<string, number>;
}

/** ObservabilityEndpoint.cs — MetadataCacheCoverage. */
export interface MetadataCacheCoverage {
  entries: number;
  negativeEntries: number;
  distinctSeries: number;
}

/** ObservabilityEndpoint.cs — response body of GET /api/admin/observability. */
export interface ObservabilityResponse {
  counters: ObservabilitySnapshot;
  metadataCache: MetadataCacheCoverage;
}

/**
 * BuildInfoEndpoint.cs — response body of GET /api/system/build.
 *
 * CommitSha, ImageTag and BuildTimestampUtc are build-time: they cannot change during the
 * process lifetime and only move on a redeploy. UptimeSeconds is the one runtime field and
 * resets on every restart, independent of whether the image itself changed. Every build-time
 * field renders the literal string "unknown (local build)" rather than being empty when the
 * server was built with no build args (see BuildInfo.UnknownLocalBuild on the server).
 */
export interface BuildInfoResponse {
  commitSha: string;
  imageTag: string;
  buildTimestampUtc: string;
  informationalVersion: string;
  uptimeSeconds: number;
}

/**
 * HealthStalenessEndpoint.cs — response body of GET /api/health/staleness.
 *
 * The field names are snake_case ON PURPOSE and must stay verbatim. AC25 quotes
 * them directly as the contract operators and monitoring tooling read, so the
 * server declares them in snake_case rather than letting the camelCase policy
 * rewrite them. Renaming these to camelCase here would silently read undefined.
 *
 * Every value is a C# TimeSpan rendered with ToString(), e.g. "01:30:00".
 */
export interface StalenessEnvelopeResponse {
  worst_case_unjudged_age: string;
  search_result_cache_band_bound: string;
  classifier_queue_latency: string;
  fresh_until: string;
  refresh_lead_plus_worker_cycle_interval: string;
  serve_until: string;
}

/**
 * ActivityEndpoint.cs — one row of GET /api/activity (#55).
 *
 * `occurredAt` is an ISO-8601 instant WITH its offset (e.g. "2026-09-07T14:03:11+00:00"),
 * serialized from a C# DateTimeOffset. The offset is part of the contract, not incidental:
 * AC9 requires timestamps to be unambiguous about their timezone, so this string must never
 * be truncated to a bare local-looking datetime on its way to the surface.
 */
export interface ActivityEntry {
  occurredAt: string;
  kind: ActivityKind;
  summary: string;
  reason: string | null;
  sourceDisplayName: string | null;
  detail: string | null;
}

/**
 * EventKind.cs, camelCased on the wire.
 *
 * These are the five kinds the store records — deliberately not "every event", which would
 * make the surface a log file with extra steps (plan §3.1). `decision` rows are the ones
 * #54's review queue reads.
 */
export type ActivityKind =
  | 'decision'
  | 'workerCycle'
  | 'snapshotRefreshed'
  | 'searchServed'
  | 'sourceFailed';

/**
 * ActivityEndpoint.cs — response body of GET /api/activity.
 *
 * `nextCursor` is an OPAQUE row position and null on the last page. It is deliberately not a
 * page number or an offset: the store grows at the same end it is read from, so an offset would
 * skip and repeat rows as events arrive mid-page (AC8). Pass it back verbatim as `?cursor=`;
 * never compute one, and never treat it as an index.
 */
export interface ActivityPageResponse {
  events: ActivityEntry[];
  nextCursor: number | null;
}

/**
 * LogsEndpoint.cs — one row of GET /api/admin/logs (#65).
 *
 * The shape is Sonarr's `LogResource` (plan §1), which is why `exception` and
 * `exceptionType` are two fields rather than one: the type name is what an
 * operator scans a column for, while the full text is long enough that it only
 * belongs behind a disclosure.
 *
 * `time` is an ISO-8601 instant WITH its offset, serialized from a C#
 * DateTimeOffset — the same contract ActivityEntry.occurredAt carries, and for
 * the same AC9 reason: a bare local-looking datetime would be ambiguous between
 * two readers.
 *
 * `level` is a Microsoft.Extensions.Logging.LogLevel NAME ("Information",
 * "Warning"), not an ordinal, and is matched case-insensitively by the store.
 * It is typed as a bare string rather than a union because the sink writes
 * whatever LogLevel it was handed: narrowing it here would make an unexpected
 * level a type error at the one place that must still display it.
 */
export interface LogEntryResponse {
  id: number;
  time: string;
  level: string;
  logger: string;
  message: string;
  exception: string | null;
  exceptionType: string | null;
}

/**
 * LogsEndpoint.cs — response body of GET /api/admin/logs.
 *
 * OFFSET paging (`page`/`pageSize`/`total`), deliberately unlike ActivityPageResponse's
 * opaque cursor. The two stores are read differently: activity is a growing feed read
 * from its newest end, where an offset would skip and repeat rows, while the log store
 * is filtered down to a level or logger and then paged through — a case where the
 * operator wants "page 3 of 9", which a seek cursor cannot state.
 *
 * `page` and `pageSize` are what the server ACTUALLY served after clamping, not an echo
 * of what was asked; the UI renders those rather than its own request so a clamped page
 * size is visible instead of silently disagreeing with the row count.
 *
 * `loggers` is every distinct logger category in the store, sent with each page so the
 * filter can populate without a second round trip. See the endpoint's own note on why it
 * is not its own route.
 */
export interface LogsResponse {
  entries: LogEntryResponse[];
  total: number;
  page: number;
  pageSize: number;
  loggers: string[];
}

// --- Decisions (#54) ------------------------------------------------------

/** The two verdicts a decision can carry. Unreviewed is null, never a third name. */
export type ReviewVerdict = 'agree' | 'disagree';

/**
 * DecisionReviewEndpoints.cs — DecisionEntryResponse.
 *
 * `id` is the event row's real identity and is what the review POST takes. This is
 * why the review affordance lives on THIS type and not on SuppressionViewEntry:
 * that one is projected from the suppression audit log, which carries no id at all.
 *
 * `shadowMode` is the flag AS IT WAS WHEN THE DECISION WAS MADE, not the current
 * setting, so flipping the switch does not retroactively relabel history.
 */
export interface DecisionEntry {
  id: number;
  occurredAt: string;
  summary: string;
  reason: string | null;
  detail: string | null;
  shadowMode: boolean | null;
  /** null while nobody has reviewed this decision. */
  reviewVerdict: ReviewVerdict | null;
  reviewedAt: string | null;
  reviewNote: string | null;
}

/** One page of decisions, most recent first. `nextCursor` is opaque — echo it, never compute it. */
export interface DecisionPageResponse {
  decisions: DecisionEntry[];
  nextCursor: number | null;
}

/**
 * DecisionReviewEndpoints.cs — DecisionAgreementResponse.
 *
 * COUNTS, NEVER A RATE, AND THAT IS DELIBERATE. With zero reviews there is no rate
 * to state: 0/0 is not 0%. The ratio is formed at the point of display by
 * `agreementRate`, which answers null here so `formatRate` renders the em-dash.
 * Adding a `rate` field to this interface would re-import the bug the endpoint was
 * written to avoid.
 */
export interface DecisionAgreementResponse {
  agreed: number;
  disagreed: number;
  reviewed: number;
  windowDays: number;
}

/** The review POST's body. `verdict` is required; a review without one is not a review. */
export interface ReviewDecisionRequest {
  verdict: ReviewVerdict;
  note?: string;
}

// --- API keys (#58 backend, #82 UI) ---------------------------------------

/**
 * The two scopes a key can hold, as the wire spells them.
 *
 * These are the STRING NAMES and nothing else. AdminApiKeyEndpoints parses the
 * incoming scope by matching these two names explicitly and REJECTS the numeric
 * form, because Enum.TryParse would have let `{"scope":"1"}` mint an Admin key.
 * A widening of this type to `string`, or a UI that ever posts an index, walks
 * straight back into that hole — so the union is the client-side half of the
 * same closure.
 */
export type ApiKeyScope = 'ReadOnly' | 'Admin';

/**
 * AdminApiKeyEndpoints.cs — ApiKeyResponse.
 *
 * NOTE WHAT IS ABSENT: there is no field for the key value, and deliberately no
 * nullable one a future edit could start populating. The plaintext exists in
 * exactly one response shape (`CreatedApiKeyResponse`) and nowhere else, because
 * only the hash is stored — see that type.
 *
 * `id` is null only for the legacy row, which is synthesised at read time and
 * addresses nothing, which is why `isLegacy` gates the revoke affordance rather
 * than a null-check on the id doing it implicitly.
 */
export interface ApiKeyEntry {
  /** null for the legacy row — there is no row to address, so nothing to DELETE. */
  id: number | null;
  label: string;
  scope: ApiKeyScope;
  /** null for the legacy key: no creation was ever recorded, and inventing one would read as real. */
  createdAt: string | null;
  /** null until the key is first used. This is the field that makes revocation safe rather than a guess. */
  lastUsedAt: string | null;
  /** Non-null means revoked. The row stays in the list as a tombstone; it never disappears. */
  revokedAt: string | null;
  /**
   * True for the synthetic row standing for the pre-#58 shared key. It comes from
   * the environment/configuration, not the key table, so there is nothing for a
   * revoke button to delete — the UI renders the explanation instead of the button.
   */
  isLegacy: boolean;
}

/** The create POST's body. An absent scope means the NARROWER one, server-side. */
export interface CreateApiKeyRequest {
  label: string;
  scope: ApiKeyScope;
}

/**
 * AdminApiKeyEndpoints.cs — CreatedApiKeyResponse, THE ONLY RESPONSE IN THIS API
 * THAT EVER CARRIES A LIVE CREDENTIAL.
 *
 * `plaintextKey` is generated, hashed and returned without ever being persisted.
 * There is no route that can produce it again — not because one was omitted, but
 * because after this response no copy exists anywhere in the system. Which is why
 * the UI states that at the point of creation: no later screen could.
 *
 * It must therefore never be written to `localStorage`, `sessionStorage`, a query
 * string, or the query cache. It lives in component state for exactly as long as
 * the reveal panel is open. `ApiKeys.test.tsx` holds that assertion with a
 * positive control.
 */
export interface CreatedApiKeyResponse {
  key: ApiKeyEntry;
  plaintextKey: string;
}
