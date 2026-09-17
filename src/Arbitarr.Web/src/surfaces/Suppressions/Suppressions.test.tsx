import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SuppressionsPage from './Suppressions';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { SERVER_KEY_UNSET_MESSAGE } from '../QueryState';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';

const entries = [
  {
    occurredAt: '2026-09-01T12:30:00+00:00',
    releaseIdentifier: 'upstream-guid-1',
    queryKey: 'tvdbid=1234&season=2',
    layer: 'block-cam',
    reason: 'Pattern CAM|TS matched the release title.',
    shadowMode: false,
  },
  {
    occurredAt: '2026-09-01T12:29:00+00:00',
    releaseIdentifier: 'upstream-guid-2',
    queryKey: 'tvdbid=1234&season=2',
    layer: 'ai',
    reason: 'Verdict Reject: title does not match the requested season.',
    shadowMode: true,
  },
];

/**
 * A separate fixture (not `entries`) for the identifier-column tests below, so
 * adding a long identifier does not perturb the badge-text assertions the
 * other describe block already makes against the shared two-row `entries`.
 */
const entriesWithLongIdentifier = [
  ...entries,
  {
    occurredAt: '2026-09-01T12:28:00+00:00',
    // Longer than what fits in the column, so it is the positive control for
    // "the full value is still reachable" -- a test that only ever renders
    // short identifiers cannot fail if truncation swallowed the rest.
    releaseIdentifier: 'upstream-guid-with-a-very-long-value-that-does-not-fit-in-the-column-3',
    queryKey: 'tvdbid=1234&season=2',
    layer: 'pass',
    reason: 'No layer acted; the release was left untouched.',
    shadowMode: false,
  },
];

/**
 * The decisions panel (#54) shares this surface, so every test here answers its
 * GET too. Left EMPTY on purpose: these tests are about the audit-log table, and
 * a populated decisions table would put a second "Shadow only" badge on the page
 * and make their getByText assertions ambiguous. The decisions panel's own
 * behaviour is covered in DecisionReview.test.tsx.
 */
const EMPTY_DECISIONS = { '/api/decisions': { body: { decisions: [], nextCursor: null } } };

describe('Suppressions', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders its title and the decisions table', async () => {
    mockApi({ ...EMPTY_DECISIONS, '/api/admin/suppressions': { body: entries } });
    renderSurface(<SuppressionsPage />);

    expect(screen.getByRole('heading', { level: 1, name: 'Suppressions' })).toBeInTheDocument();
    expect(await screen.findByText('upstream-guid-1')).toBeInTheDocument();
    expect(screen.getByText('Pattern CAM|TS matched the release title.')).toBeInTheDocument();
  });

  it('reads as a healthy quiet state, not a missing thing, when nothing has been suppressed', async () => {
    mockApi({ ...EMPTY_DECISIONS, '/api/admin/suppressions': { body: [] } });
    renderSurface(<SuppressionsPage />);

    expect(
      await screen.findByText(
        'Nothing suppressed or de-ranked yet. Entries appear here once a rule or the AI layer acts on a release.',
      ),
    ).toBeInTheDocument();
  });

  it('attributes each row to the acting layer and distinguishes shadow mode', async () => {
    mockApi({ ...EMPTY_DECISIONS, '/api/admin/suppressions': { body: entries } });
    renderSurface(<SuppressionsPage />);

    // AC11: the layer that acted -- a rule name for the rule-engine layers, a
    // stable label for the others.
    expect(await screen.findByText('block-cam')).toBeInTheDocument();
    expect(screen.getByText('ai')).toBeInTheDocument();

    // A shadow-mode row was recorded but NOT enforced, and the two must not
    // read alike: the legacy page printed "yes"/"no" under a "Shadow Mode"
    // heading, where "yes" looked like the suppression had happened.
    expect(screen.getByText('Suppressed')).toBeInTheDocument();
    expect(screen.getByText('Shadow only')).toBeInTheDocument();
  });

  /**
   * arb-p94u: this row used to render a bare `new Date(v).toLocaleString()`,
   * exactly the ambiguity Activity's AC9 forbids for the same underlying data
   * an operator correlates against logs. The `title` assertion is what pins
   * the fix; the text assertion is an EXACT match against
   * `toLocaleString(undefined, { timeZoneName: 'short' })`, with a positive
   * control proving that string differs from the bare rendering on the
   * CURRENT runner -- a `/[A-Za-z]/` "has a letter" check would be vacuous on
   * an AM/PM (en-US-ish) locale, where the bare rendering already contains
   * letters with no zone information.
   */
  it('renders the occurred timestamp with a timezone abbreviation and the raw instant in title', async () => {
    mockApi({ ...EMPTY_DECISIONS, '/api/admin/suppressions': { body: entries } });
    renderSurface(<SuppressionsPage />);

    const row = (await screen.findByText('block-cam')).closest('tr');
    expect(row).not.toBeNull();
    const [timeCell] = within(row as HTMLElement).getAllByRole('cell');

    const iso = '2026-09-01T12:30:00+00:00';
    const parsed = new Date(iso);
    const expected = parsed.toLocaleString(undefined, { timeZoneName: 'short' });
    // Positive control: proves the exact-match assertion below could not have
    // passed against the old bare rendering on this runner.
    expect(expected).not.toBe(parsed.toLocaleString(undefined));

    expect(timeCell).toHaveAttribute('title', iso);
    expect(timeCell.textContent).toBe(expected);
  });

  it('heads the identifier column for what it holds, not for a release title', async () => {
    mockApi({ ...EMPTY_DECISIONS, '/api/admin/suppressions': { body: entries } });
    renderSurface(<SuppressionsPage />);
    await screen.findByText('upstream-guid-1');

    expect(
      screen.getByRole('columnheader', { name: 'Upstream identifier' }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: 'Release' })).not.toBeInTheDocument();
  });

  it('keeps the full identifier reachable via its title attribute even when it is long', async () => {
    mockApi({ ...EMPTY_DECISIONS, '/api/admin/suppressions': { body: entriesWithLongIdentifier } });
    renderSurface(<SuppressionsPage />);

    const longIdentifier = 'upstream-guid-with-a-very-long-value-that-does-not-fit-in-the-column-3';
    // Positive control: this fixture value is longer than the column visually
    // shows, so this assertion only passes if the full string is genuinely on
    // the element (in the title attribute and as its selectable text), not
    // merely present somewhere in the fixture data.
    const cell = await screen.findByTitle(longIdentifier);
    expect(cell).toHaveTextContent(longIdentifier);
  });

  it('offers no "Show titles" control on any row, because none can resolve', async () => {
    mockApi({ ...EMPTY_DECISIONS, '/api/admin/suppressions': { body: entriesWithLongIdentifier } });
    renderSurface(<SuppressionsPage />);
    await screen.findByText('upstream-guid-1');

    // Asserted per row, across all three fixture rows: "some row lacks it"
    // would also pass an implementation that dropped the control from only
    // one row.
    const rows = screen.getAllByRole('row').slice(1); // drop the header row
    expect(rows).toHaveLength(3);
    for (const row of rows) {
      expect(within(row).queryByRole('button', { name: /show titles/i })).not.toBeInTheDocument();
      expect(within(row).queryByRole('button', { name: /hide titles/i })).not.toBeInTheDocument();
    }
  });

  it('never requests the explanation endpoint for this surface', async () => {
    const api = mockApi({ ...EMPTY_DECISIONS, '/api/admin/suppressions': { body: entries } });
    renderSurface(<SuppressionsPage />);
    // Positive control: the list request itself IS recorded by the same spy,
    // so an empty explanation call count here reflects a control that was
    // never rendered, not a spy that records nothing.
    await screen.findByText('upstream-guid-1');
    expect(api.callsTo('/api/admin/suppressions').length).toBeGreaterThan(0);

    expect(api.calls.some((call) => call.path.includes('/explanation'))).toBe(false);
  });

  it('filters by query key server-side and attaches the admin key to its GET', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({ ...EMPTY_DECISIONS, '/api/admin/suppressions': { body: entries } });
    renderSurface(<SuppressionsPage />);
    await screen.findByText('upstream-guid-1');

    // The unfiltered load carries no queryKey at all, rather than an empty one.
    expect(api.calls[0].url.searchParams.has('queryKey')).toBe(false);

    await user.type(screen.getByLabelText('Query key'), 'tvdbid=1234&season=2');
    await user.click(screen.getByRole('button', { name: 'Apply' }));

    // The filter is a server-side WHERE on QueryKey, not a client-side
    // narrowing of a full fetch, so it must reach the wire.
    const filtered = api.calls[api.calls.length - 1];
    expect(filtered.url.searchParams.get('queryKey')).toBe('tvdbid=1234&season=2');

    // Admin-gated, and its only verb is GET -- gating by verb rather than by
    // the /api/admin/ path prefix would leave this surface unauthenticated.
    expect(api.adminKeyOn('/api/admin/suppressions')).toBe('operator-key');
  });

  it('keeps the affordance and the stored key on a 503 fresh install', async () => {
    useAdminKeyStore.getState().setKey('operator-key');
    mockApi({
      ...EMPTY_DECISIONS,
      '/api/admin/suppressions': { status: 503, body: { error: 'admin key not configured' } },
    });
    renderSurface(<SuppressionsPage />);

    expect(await screen.findByText(SERVER_KEY_UNSET_MESSAGE)).toBeInTheDocument();
    // AC6-503: the filter still works and no key prompt appears -- the
    // operator's key is not what is wrong.
    expect(screen.getByRole('button', { name: 'Apply' })).toBeInTheDocument();
    expect(useAdminKeyStore.getState().key).toBe('operator-key');
    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(true);
    expect(screen.queryByLabelText(/admin api key/i)).toBeNull();
  });
});

/**
 * arb-ajrv: the filter moved out of a bordered `.panel` headed "Filter" and onto
 * the shared PageToolbar row.
 *
 * Every assertion here fails against the pre-migration markup, which is what
 * makes them evidence rather than decoration: there was no `role="toolbar"` on
 * this surface at all, and the "Filter" heading it asserts the absence of was
 * present.
 */
describe('Suppressions filter toolbar', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: 'test-key', serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('puts the query key field and Apply inside the toolbar, not beside it', async () => {
    mockApi({ ...EMPTY_DECISIONS, '/api/admin/suppressions': { body: entries } });
    renderSurface(<SuppressionsPage />);
    await screen.findByText('upstream-guid-1');

    // CONTAINMENT, not co-presence: `within(toolbar)` is the whole point. A
    // toolbar rendered empty next to an untouched `.panel` would satisfy a bare
    // getByRole('toolbar') and leave the migration undone.
    const toolbar = screen.getByRole('toolbar', { name: 'Suppression filters' });
    expect(within(toolbar).getByRole('textbox', { name: 'Query key' })).toBeInTheDocument();
    expect(within(toolbar).getByRole('button', { name: 'Apply' })).toBeInTheDocument();
  });

  it('no longer renders the bordered Filter panel the toolbar replaced', async () => {
    mockApi({ ...EMPTY_DECISIONS, '/api/admin/suppressions': { body: entries } });
    renderSurface(<SuppressionsPage />);
    await screen.findByText('upstream-guid-1');

    // Without this a migration that ADDED a toolbar and left the panel in place
    // would pass the containment test above while shipping the control twice.
    expect(screen.queryByRole('heading', { name: 'Filter' })).not.toBeInTheDocument();

    // The panel it replaced is gone, but the surface's other panels are not --
    // asserted so the check above cannot pass by the page failing to render.
    expect(
      screen.getByRole('heading', { name: 'Suppression audit log' }),
    ).toBeInTheDocument();
  });

  it('contributes no heading from the toolbar row', async () => {
    mockApi({ ...EMPTY_DECISIONS, '/api/admin/suppressions': { body: entries } });
    renderSurface(<SuppressionsPage />);
    await screen.findByText('upstream-guid-1');

    // design-system/README.md makes this a per-surface rule, not only a
    // component one: a migration is exactly when someone re-adds a caption to
    // replace the panel heading they just removed.
    const toolbar = screen.getByRole('toolbar', { name: 'Suppression filters' });
    expect(within(toolbar).queryAllByRole('heading')).toHaveLength(0);
  });

  it('still applies on Apply rather than on each keystroke', async () => {
    const user = userEvent.setup();
    const api = mockApi({
      ...EMPTY_DECISIONS,
      '/api/admin/suppressions': { body: entries },
    });
    renderSurface(<SuppressionsPage />);
    await screen.findByText('upstream-guid-1');

    const before = api.callsTo('/api/admin/suppressions').length;
    await user.type(screen.getByRole('textbox', { name: 'Query key' }), 'tvdbid=1234');

    // The interaction model is unchanged by the migration: typing alone issues
    // nothing, because the query key is a server-side WHERE. A toolbar that had
    // quietly switched to debounced live filtering would fail here.
    expect(api.callsTo('/api/admin/suppressions')).toHaveLength(before);

    await user.click(screen.getByRole('button', { name: 'Apply' }));

    const after = api.callsTo('/api/admin/suppressions');
    expect(after).toHaveLength(before + 1);
    expect(after.at(-1)?.url.searchParams.get('queryKey')).toBe('tvdbid=1234');
  });
});
