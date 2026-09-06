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
