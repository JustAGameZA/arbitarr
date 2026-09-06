import { describe, expect, it } from 'vitest';

import { buildActivityQuery } from './queries';

/**
 * The filter-to-URL mapping, tested directly.
 *
 * Activity.test.tsx proves the surface sends *a* request with the right
 * parameters; this pins the mapping itself, including the cases the rendered
 * surface cannot easily reach — an unfiltered default, and every window bound
 * computed against a fixed clock rather than whatever Date.now() happens to be.
 */
describe('buildActivityQuery', () => {
  // A fixed instant so the window bounds are exact rather than approximate.
  const now = Date.parse('2026-09-07T12:00:00Z');

  it('asks for everything when nothing is filtered', () => {
    expect(buildActivityQuery({ kind: 'all', window: 'all' }, null, now)).toBe('/api/activity');
  });

  it('sends the kind only when one is chosen', () => {
    expect(buildActivityQuery({ kind: 'decision', window: 'all' }, null, now)).toBe(
      '/api/activity?kind=decision',
    );
  });

  it.each([
    ['hour', '2026-09-07T11:00:00.000Z'],
    ['day', '2026-09-06T12:00:00.000Z'],
    ['week', '2026-08-31T12:00:00.000Z'],
  ] as const)('computes the %s window as an absolute UTC instant', (window, expected) => {
    const url = new URL(buildActivityQuery({ kind: 'all', window }, null, now), 'http://localhost');

    expect(url.searchParams.get('since')).toBe(expected);
  });

  it('omits the time bound entirely for the all-time window', () => {
    const url = new URL(
      buildActivityQuery({ kind: 'all', window: 'all' }, null, now),
      'http://localhost',
    );

    // Not "since=null" or an epoch-zero bound: absent means unbounded.
    expect(url.searchParams.has('since')).toBe(false);
  });

  it('echoes the cursor verbatim and never emits an offset', () => {
    const url = new URL(
      buildActivityQuery({ kind: 'all', window: 'all' }, 90210, now),
      'http://localhost',
    );

    expect(url.searchParams.get('cursor')).toBe('90210');
    expect(url.searchParams.has('offset')).toBe(false);
  });

  it('composes the kind, window and cursor together', () => {
    const url = new URL(
      buildActivityQuery({ kind: 'sourceFailed', window: 'day' }, 17, now),
      'http://localhost',
    );

    // All three survive: a builder that overwrote rather than accumulated would
    // drop one and quietly widen or misplace the query.
    expect(url.searchParams.get('kind')).toBe('sourceFailed');
    expect(url.searchParams.get('since')).toBe('2026-09-06T12:00:00.000Z');
    expect(url.searchParams.get('cursor')).toBe('17');
  });
});
