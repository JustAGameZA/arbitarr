/**
 * Parses the machine-readable detail string a SearchServed event carries (arb-2b6).
 *
 * The producer is `SearchQueryDescriptor.DescribeDetail` (src/Arbitarr.Api/Search/), which joins
 * `key=value` pairs with `;` in ONE fixed order: type, cats, tvdbid, tmdbid, season, episode, abs,
 * then `q` LAST. That order is the contract this file reads against, and `q` being last is what
 * makes the format parseable at all: the query text is the only free-text field, so a term
 * containing `;` or `=` can only be read correctly by taking the whole remainder after `q=` rather
 * than splitting it. Splitting the entire string on `;` — the obvious implementation — silently
 * truncates any search for "a;b" and loses the rest.
 *
 * THIS PARSER NEVER THROWS AND NEVER DROPS. `detail` is declared free-form per kind
 * (`IEventSink`: "Free-form kind-specific detail"); only SearchServed uses this shape today, and
 * another kind may put arbitrary text or null there. So anything that does not start with `type=`
 * returns null rather than a half-populated object, and keys this file does not know are kept in
 * `rest` instead of discarded — a backend that adds a key should show up in the UI as an unstyled
 * extra, not as silence.
 *
 * Nothing here is a security boundary, but it is worth knowing why: the strings are built from the
 * parsed `SearchQuery` record, never the raw request, so no credential can reach them — which is
 * what lets `/api/activity` be un-gated (CLAUDE.md §1). This parser must not become a reason to
 * start echoing raw request text into the feed.
 */
export interface SearchDetail {
  /** The declared `t=` wire mode: "tvsearch", "movie" or "search". */
  type: string;
  /** Torznab category ids, in the order the backend listed them. Empty when absent. */
  cats: string[];
  tvdbId: string | null;
  tmdbId: string | null;
  season: string | null;
  episode: string | null;
  /**
   * The absolute episode number, as a STRING and null when absent.
   *
   * `abs=0` is a real value distinct from no absolute at all — SearchCacheKeyBuilder gives each its
   * own cache row (arb-u1c), and the backend renders the zero deliberately for that reason. Keeping
   * it a string keeps `""`/`0` from collapsing into each other the way a number plus a truthiness
   * check would, which is the exact bug the backend test AbsoluteZeroIsRenderedBecauseZeroIsARealValue
   * pins on the other side of the wire.
   */
  abs: string | null;
  /** The query text, verbatim, including any `;` or `=` it contains. Null when the search had none. */
  q: string | null;
  /** Keys this parser does not know, kept rather than dropped so a backend addition stays visible. */
  rest: Record<string, string>;
}

/** The marker the format is identified by: DescribeDetail always emits `type=` first. */
const TYPE_PREFIX = 'type=';

/** Everything after `q=` is the query text, so `q` is never split. */
const Q_PREFIX = 'q=';

export function parseSearchDetail(detail: string | null): SearchDetail | null {
  // Not a SearchServed detail (or not one this version produces): say so, rather than returning a
  // shape whose every field happens to be null and which a caller cannot tell apart from a search
  // that genuinely carried nothing.
  if (detail === null || !detail.startsWith(TYPE_PREFIX)) {
    return null;
  }

  // Split off `q` FIRST, at its first occurrence only, so the remainder stays verbatim. The search
  // for `;q=` (rather than a bare `q=`) is what stops a query text like "q=3" — or a future key
  // ending in "q" — from being mistaken for the start of the field.
  let head = detail;
  let q: string | null = null;
  const qAt = detail.indexOf(`;${Q_PREFIX}`);
  if (qAt !== -1) {
    head = detail.slice(0, qAt);
    q = detail.slice(qAt + 1 + Q_PREFIX.length);
  }

  const known: Record<string, string> = {};
  const rest: Record<string, string> = {};
  for (const pair of head.split(';')) {
    if (pair === '') {
      continue;
    }
    const eq = pair.indexOf('=');
    // A token with no `=` is malformed. Skipping it is deliberate: this parser reports what it
    // could read rather than failing the whole row, because the row's summary and timestamp are
    // still worth showing when one field is garbled.
    if (eq <= 0) {
      continue;
    }
    const key = pair.slice(0, eq);
    const value = pair.slice(eq + 1);
    if (KNOWN_KEYS.has(key)) {
      known[key] = value;
    } else {
      rest[key] = value;
    }
  }

  return {
    type: known['type'] ?? '',
    cats: known['cats'] === undefined || known['cats'] === '' ? [] : known['cats'].split(','),
    tvdbId: known['tvdbid'] ?? null,
    tmdbId: known['tmdbid'] ?? null,
    season: known['season'] ?? null,
    episode: known['episode'] ?? null,
    abs: known['abs'] ?? null,
    q,
    rest,
  };
}

const KNOWN_KEYS = new Set(['type', 'cats', 'tvdbid', 'tmdbid', 'season', 'episode', 'abs']);

/**
 * `S02E05` when both parts are present, otherwise the half that is — `S02` for a whole-season
 * request, `E05` alone if an episode ever arrives without one. Null when neither is set, so the
 * caller renders nothing rather than an empty marker.
 */
export function formatSeasonEpisode(detail: SearchDetail): string | null {
  const season = detail.season === null ? '' : `S${detail.season.padStart(2, '0')}`;
  const episode = detail.episode === null ? '' : `E${detail.episode.padStart(2, '0')}`;
  const combined = `${season}${episode}`;
  return combined === '' ? null : combined;
}
