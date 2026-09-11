import { describe, expect, it } from 'vitest';

import { formatSeasonEpisode, parseSearchDetail } from './searchDetail';

/**
 * The strings in the first block are COPIED VERBATIM from the backend's own pinned expectations in
 * tests/Arbitarr.Api.Tests/SearchQueryDescriptorTests.cs, which is the producer of this format
 * (SearchQueryDescriptor.DescribeDetail). Inventing plausible-looking inputs here would test this
 * parser against a format nothing emits: the two sides would drift and both suites would stay
 * green. If a test in that file changes its expected string, the matching one here must change
 * with it — that coupling is the point.
 */
describe('parseSearchDetail against the backend’s pinned strings', () => {
  it('parses a text-only search (SearchQueryDescriptorTests: type=search;q=bleach)', () => {
    const parsed = parseSearchDetail('type=search;q=bleach');

    expect(parsed).not.toBeNull();
    expect(parsed?.type).toBe('search');
    expect(parsed?.q).toBe('bleach');
    expect(parsed?.cats).toEqual([]);
    expect(parsed?.tvdbId).toBeNull();
  });

  it('parses a category-only feed (type=tvsearch;cats=5030,5040)', () => {
    const parsed = parseSearchDetail('type=tvsearch;cats=5030,5040');

    expect(parsed?.type).toBe('tvsearch');
    expect(parsed?.cats).toEqual(['5030', '5040']);
    // The RSS-sync case: no text at all, which is what made 55 events indistinguishable (F-010).
    expect(parsed?.q).toBeNull();
  });

  it('parses ids, season and episode (type=tvsearch;cats=5030,5040;tvdbid=74796;season=2;episode=5)', () => {
    const parsed = parseSearchDetail('type=tvsearch;cats=5030,5040;tvdbid=74796;season=2;episode=5');

    expect(parsed?.tvdbId).toBe('74796');
    expect(parsed?.season).toBe('2');
    expect(parsed?.episode).toBe('5');
    expect(formatSeasonEpisode(parsed!)).toBe('S02E05');
  });

  it('parses a movie search (type=movie;tmdbid=438631;q=dune)', () => {
    const parsed = parseSearchDetail('type=movie;tmdbid=438631;q=dune');

    expect(parsed?.type).toBe('movie');
    expect(parsed?.tmdbId).toBe('438631');
    expect(parsed?.q).toBe('dune');
    expect(parsed?.tvdbId).toBeNull();
  });

  it('parses the bare feed spelling (type=search)', () => {
    const parsed = parseSearchDetail('type=search');

    expect(parsed?.type).toBe('search');
    expect(parsed?.q).toBeNull();
    expect(parsed?.cats).toEqual([]);
  });
});

describe('the query text is taken verbatim because q is last', () => {
  // The reason the backend puts q last. A parser that split the whole string on ';' would return
  // "a" here and silently lose the rest of what the user searched for.
  it('keeps a ";" inside the query text', () => {
    const parsed = parseSearchDetail('type=search;q=a;b');

    expect(parsed?.q).toBe('a;b');
  });

  it('keeps an "=" inside the query text', () => {
    const parsed = parseSearchDetail('type=search;q=x=y');

    expect(parsed?.q).toBe('x=y');
  });

  it('keeps text that itself looks like a key=value pair', () => {
    const parsed = parseSearchDetail('type=tvsearch;tvdbid=74796;q=season=2;episode=5');

    // Everything after the FIRST ";q=" is the text, so these do not become fields.
    expect(parsed?.q).toBe('season=2;episode=5');
    expect(parsed?.season).toBeNull();
    expect(parsed?.episode).toBeNull();
    expect(parsed?.tvdbId).toBe('74796');
  });

  it('keeps a query that is literally "q=..."', () => {
    const parsed = parseSearchDetail('type=search;q=q=3');

    expect(parsed?.q).toBe('q=3');
  });
});

describe('abs=0 is distinguishable from an absent absolute', () => {
  // Both halves together, because either alone proves nothing: the backend renders zero on purpose
  // (SearchCacheKeyBuilder gives abs=0 its own cache row, arb-u1c), so a parser that folded "0"
  // into null would erase exactly the search this field exists to separate.
  it('reads abs=0 as present', () => {
    const parsed = parseSearchDetail('type=tvsearch;tvdbid=74796;abs=0;q=0');

    expect(parsed?.abs).toBe('0');
  });

  it('reads an absent abs as null', () => {
    const parsed = parseSearchDetail('type=tvsearch;tvdbid=74796;q=bleach');

    expect(parsed?.abs).toBeNull();
  });
});

describe('non-search and malformed details', () => {
  it('returns null for a free-form detail from another kind', () => {
    // `detail` is free-form per kind (IEventSink), so this is a real input, not a hypothetical.
    expect(parseSearchDetail('classified 12 releases, 1 failed')).toBeNull();
  });

  it('returns null for null', () => {
    expect(parseSearchDetail(null)).toBeNull();
  });

  it('returns null for an empty string', () => {
    expect(parseSearchDetail('')).toBeNull();
  });

  it('returns null for a string that merely contains type= later on', () => {
    // Anchored at the start: otherwise a prose detail mentioning the word would be parsed as one.
    expect(parseSearchDetail('search failed for type=tvsearch')).toBeNull();
  });

  it('does not throw on malformed pairs, and reports what it could read', () => {
    const parsed = parseSearchDetail('type=tvsearch;;garbage;=novalue;tvdbid=74796');

    expect(parsed?.type).toBe('tvsearch');
    expect(parsed?.tvdbId).toBe('74796');
  });
});

describe('unknown keys are kept rather than dropped', () => {
  it('puts a key this parser does not know into rest', () => {
    // A backend that adds a field should surface as an unstyled extra, not as silence.
    const parsed = parseSearchDetail('type=search;imdbid=tt0111161;q=shawshank');

    expect(parsed?.rest).toEqual({ imdbid: 'tt0111161' });
    expect(parsed?.q).toBe('shawshank');
  });

  it('leaves rest empty when every key is known', () => {
    const parsed = parseSearchDetail('type=movie;tmdbid=438631;q=dune');

    expect(parsed?.rest).toEqual({});
  });
});

describe('formatSeasonEpisode', () => {
  it('pads both parts', () => {
    expect(formatSeasonEpisode(parseSearchDetail('type=tvsearch;season=2;episode=5')!)).toBe('S02E05');
  });

  it('renders a whole-season request with no episode', () => {
    expect(formatSeasonEpisode(parseSearchDetail('type=tvsearch;season=2')!)).toBe('S02');
  });

  it('does not pad a number that is already two digits', () => {
    expect(formatSeasonEpisode(parseSearchDetail('type=tvsearch;season=12;episode=105')!)).toBe('S12E105');
  });

  it('returns null when neither part is present', () => {
    expect(formatSeasonEpisode(parseSearchDetail('type=search;q=bleach')!)).toBeNull();
  });
});
