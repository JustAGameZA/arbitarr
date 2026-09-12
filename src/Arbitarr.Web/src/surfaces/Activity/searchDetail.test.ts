import { describe, expect, it } from 'vitest';

import { formatSeasonEpisode, parseSearchDetail } from './searchDetail';
import fixtureCases from '../../../../../tests/fixtures/search-detail.json';

/**
 * arb-6jks: these strings are no longer copied by hand. Both this file and the backend's
 * tests/Arbitarr.Api.Tests/SearchQueryDescriptorTests.cs (DescribeDetailMatchesTheSharedFixture)
 * read the SAME file, tests/fixtures/search-detail.json — the producer/consumer parity contract.
 * If the wire format changes: edit the C# producer (SearchQueryDescriptor.DescribeDetail) first,
 * update its `Assert.Equal` pins, then update the fixture to match, and this test follows with no
 * changes of its own.
 */
interface FixtureQuery {
  type: string;
  cats: number[];
  tvdbid: number | null;
  tmdbid: number | null;
  season: number | null;
  episode: number | null;
  abs: number | null;
  /** Always the TRIMMED value — the wire format is already trimmed, so `q` is what round-trips. */
  q: string | null;
  /** Present only on the "query text needing trim" case; not used here (see the C# fixture DTO). */
  rawQ?: string;
}

interface FixtureCase {
  name: string;
  query: FixtureQuery;
  detail: string;
}

const cases = fixtureCases as FixtureCase[];

describe('parseSearchDetail against the shared fixture', () => {
  // Guard against a fixture that no suite reads (arb-6jks): an empty or missing file must fail
  // this test, not silently skip it.
  it('has at least one fixture case', () => {
    expect(cases.length).toBeGreaterThan(0);
  });

  it.each(cases)('parses "$detail" ($name) back into the source query', ({ query, detail }) => {
    const parsed = parseSearchDetail(detail);

    expect(parsed).not.toBeNull();
    expect(parsed?.type).toBe(query.type);
    expect(parsed?.cats).toEqual(query.cats.map(String));
    expect(parsed?.tvdbId).toBe(query.tvdbid === null ? null : String(query.tvdbid));
    expect(parsed?.tmdbId).toBe(query.tmdbid === null ? null : String(query.tmdbid));
    expect(parsed?.season).toBe(query.season === null ? null : String(query.season));
    expect(parsed?.episode).toBe(query.episode === null ? null : String(query.episode));
    expect(parsed?.abs).toBe(query.abs === null ? null : String(query.abs));
    // A whitespace-only q renders no "q=" key at all (bare feed spelling), so trim before comparing.
    expect(parsed?.q).toBe(query.q?.trim() || null);

    if (query.season !== null && query.episode !== null) {
      expect(formatSeasonEpisode(parsed!)).toBe(
        `S${String(query.season).padStart(2, '0')}E${String(query.episode).padStart(2, '0')}`,
      );
    }

    // Every case in the shared fixture is built entirely from keys this parser knows, so `rest`
    // must be empty for every one of them. A producer that starts emitting an unknown key on any
    // of these shapes should fail here rather than pass silently.
    expect(parsed?.rest).toEqual({});
  });

  // Positive control for the assertion above: proves it would actually catch an unknown key,
  // rather than passing vacuously because no fixture case exercises `rest`.
  it('fails the rest-is-empty assertion when a fixture-shaped detail carries an unknown key', () => {
    const parsed = parseSearchDetail('type=search;imdbid=tt0111161;q=shawshank');

    expect(parsed?.rest).not.toEqual({});
    expect(parsed?.rest).toEqual({ imdbid: 'tt0111161' });
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
