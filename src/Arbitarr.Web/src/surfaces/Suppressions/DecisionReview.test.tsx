import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SuppressionsPage from './Suppressions';
import DashboardPage from '../Dashboard/Dashboard';
import { agreementRate } from '../../format';
import { buildDecisionsQuery } from './decisionQueries';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';

/** The audit-log panel shares this surface; empty so its rows never collide with these. */
const NO_SUPPRESSIONS = { '/api/admin/suppressions': { body: [] } };

const unreviewed = {
  id: 41,
  occurredAt: '2026-09-01T12:30:00+00:00',
  summary: 'Suppressed Some.Show.S02E01.CAM',
  reason: 'Pattern CAM|TS matched the release title.',
  detail: 'rule:block-cam',
  shadowMode: true,
  reviewVerdict: null,
  reviewedAt: null,
  reviewNote: null,
};

const reviewed = {
  id: 42,
  occurredAt: '2026-09-01T12:29:00+00:00',
  summary: 'Suppressed Some.Show.S02E02.WEB',
  reason: 'Verdict Reject: title does not match the requested season.',
  detail: 'ai',
  shadowMode: true,
  reviewVerdict: 'agree' as const,
  reviewedAt: '2026-09-01T13:00:00+00:00',
  reviewNote: 'Correct, wrong season.',
};

const page = (decisions: unknown[]) => ({ body: { decisions, nextCursor: null } });

/** The Dashboard's other three reads, so only the agreement figure is under test. */
const DASHBOARD_READS = {
  '/api/status': {
    body: {
      status: 'ok',
      sources: [],
      worker: {
        enabled: true,
        lastCycleStartedUtc: '2026-09-06T10:00:00+00:00',
        lastCycleCompletedUtc: '2026-09-06T10:00:12+00:00',
        lastCycleCandidates: 0,
        lastCycleRefreshed: 0,
        lastCycleFailed: 0,
        lastError: null,
        consecutiveFailedCycles: 0,
      },
      health: [],
    },
  },
  '/api/searches/recent': { body: [] },
  '/api/config/effective': {
    body: {
      nzbHydraConfigured: true,
      freshUntilSeconds: 300,
      serveUntilSeconds: 900,
      activeWindowSeconds: 3600,
      refreshLeadSeconds: 60,
      workerCycleIntervalSeconds: 120,
      workerEnabled: true,
      querySnapshotTtlSeconds: 86400,
      shadowMode: true,
    },
  },
};

describe('agreementRate', () => {
  // AC4 at the unit level: the guard that keeps 0/0 from becoming 0%.
  it('is null with nothing reviewed, so there is no rate to state', () => {
    expect(agreementRate(0, 0)).toBeNull();
  });

  it('divides agreed by reviewed once there is a sample', () => {
    expect(agreementRate(47, 52)).toBeCloseTo(47 / 52);
  });

  // A negative count could only be a server bug; printing a confident
  // percentage from it would be worse than admitting there is nothing to state.
  it('refuses to divide by a negative count', () => {
    expect(agreementRate(1, -1)).toBeNull();
  });
});

describe('buildDecisionsQuery', () => {
  // 'all' must OMIT the parameter: sending shadowMode= would filter to a value
  // the operator never chose, silently hiding half the queue.
  it('omits shadowMode entirely for the unfiltered view', () => {
    expect(buildDecisionsQuery('all', null)).toBe('/api/decisions');
  });

  it('maps the two filters onto the flag the server stores', () => {
    expect(buildDecisionsQuery('shadow', null)).toBe('/api/decisions?shadowMode=true');
    expect(buildDecisionsQuery('live', null)).toBe('/api/decisions?shadowMode=false');
  });

  it('echoes the cursor verbatim rather than computing an offset', () => {
    expect(buildDecisionsQuery('all', 900)).toBe('/api/decisions?cursor=900');
  });
});

describe('Decision review', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('says what would fill the queue, not just that it is empty (#52)', async () => {
    mockApi({ ...NO_SUPPRESSIONS, '/api/decisions': page([]) });
    renderSurface(<SuppressionsPage />);

    // The empty state names the action that produces rows -- running a search --
    // rather than stating the absence and leaving the operator to guess.
    const empty = await screen.findByText(/No pipeline decisions recorded yet/);
    expect(empty).toHaveTextContent('Run a search');
    expect(empty).toHaveTextContent(/agree or disagree/);
  });

  it('shows the existing verdict, since re-reviewing updates rather than appends', async () => {
    mockApi({ ...NO_SUPPRESSIONS, '/api/decisions': page([reviewed]) });
    renderSurface(<SuppressionsPage />);

    // The operator must see what they are about to change; a bare pair of
    // buttons would make re-reviewing feel like it silently did nothing.
    expect(await screen.findByText('Agreed')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Change' })).toBeInTheDocument();
  });

  it('offers a review, not a change, for an unreviewed decision', async () => {
    mockApi({ ...NO_SUPPRESSIONS, '/api/decisions': page([unreviewed]) });
    renderSurface(<SuppressionsPage />);

    expect(await screen.findByRole('button', { name: 'Review' })).toBeInTheDocument();
    expect(screen.getByText('Not reviewed')).toBeInTheDocument();
  });

  it('posts a verdict with its note and attaches the admin key', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({
      ...NO_SUPPRESSIONS,
      '/api/decisions': page([unreviewed]),
      '/api/admin/decisions/41/review': { status: 200, body: {} },
    });
    renderSurface(<SuppressionsPage />);

    await user.click(await screen.findByRole('button', { name: 'Review' }));
    await user.type(screen.getByLabelText('Review note for decision 41'), 'Right call.');
    await user.click(screen.getByRole('button', { name: 'Agree' }));

    const posted = await vi.waitFor(() => {
      const calls = api.callsTo('/api/admin/decisions/41/review');
      expect(calls).toHaveLength(1);
      return calls[0];
    });

    expect(posted.method).toBe('POST');
    expect(JSON.parse(posted.body ?? '{}')).toEqual({ verdict: 'agree', note: 'Right call.' });
    // Admin-gated by path prefix; the key rides the header, never the URL.
    expect(api.adminKeyOn('/api/admin/decisions/41/review')).toBe('operator-key');
    expect(posted.url.searchParams.has('verdict')).toBe(false);
  });

  it('omits an empty note rather than recording one the operator did not write', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({
      ...NO_SUPPRESSIONS,
      '/api/decisions': page([unreviewed]),
      '/api/admin/decisions/41/review': { status: 200, body: {} },
    });
    renderSurface(<SuppressionsPage />);

    await user.click(await screen.findByRole('button', { name: 'Review' }));
    await user.click(screen.getByRole('button', { name: 'Disagree' }));

    const posted = await vi.waitFor(() => {
      const calls = api.callsTo('/api/admin/decisions/41/review');
      expect(calls).toHaveLength(1);
      return calls[0];
    });

    // The column is nullable; '' would record an empty note as if written.
    expect(JSON.parse(posted.body ?? '{}')).toEqual({ verdict: 'disagree' });
  });

  it('updates rather than duplicates when the same decision is reviewed twice', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({
      ...NO_SUPPRESSIONS,
      '/api/decisions': page([unreviewed]),
      '/api/admin/decisions/41/review': { status: 200, body: {} },
    });
    renderSurface(<SuppressionsPage />);

    // The reply is staged BEFORE the click, because the mutation's onSuccess
    // invalidates immediately and the refetch races anything staged after it.
    api.set(
      '/api/decisions',
      page([{ ...unreviewed, reviewVerdict: 'agree', reviewedAt: '2026-09-01T13:05:00+00:00' }]),
    );
    await user.click(await screen.findByRole('button', { name: 'Review' }));
    await user.click(screen.getByRole('button', { name: 'Agree' }));
    await screen.findByText('Agreed');

    // The form collapses on success, so the refreshed badge is the confirmation
    // rather than a still-open form that looks like nothing happened.
    expect(screen.queryByLabelText('Review note for decision 41')).toBeNull();

    // Reviewing again changes the verdict on the SAME row.
    api.set(
      '/api/decisions',
      page([{ ...unreviewed, reviewVerdict: 'disagree', reviewedAt: '2026-09-01T13:06:00+00:00' }]),
    );
    await user.click(screen.getByRole('button', { name: 'Change' }));
    await user.click(screen.getByRole('button', { name: 'Disagree' }));

    expect(await screen.findByText('Disagreed')).toBeInTheDocument();
    // The decisive assertion: ONE row, carrying ONE verdict. The write is
    // idempotent per decision, so a second review must not leave the earlier
    // verdict behind beside it.
    expect(screen.queryByText('Agreed')).toBeNull();
    expect(screen.getAllByRole('row')).toHaveLength(2); // header + the one decision
    expect(api.callsTo('/api/admin/decisions/41/review')).toHaveLength(2);
  });

  it('renders the em-dash, and never 0%, with nothing reviewed (AC4)', async () => {
    mockApi({
      ...DASHBOARD_READS,
      '/api/decisions/agreement': { body: { agreed: 0, disagreed: 0, reviewed: 0, windowDays: 7 } },
    });
    renderSurface(<DashboardPage />);

    const summary = await screen.findByText(/no decisions reviewed/);
    // 0/0 is not 0%: "the pipeline has been right 0% of the time" is an
    // accusation the data does not support when nobody has judged it yet.
    expect(summary).toHaveTextContent('—');
    expect(summary).not.toHaveTextContent('0%');
    expect(summary).toHaveTextContent('last 7 days');
  });

  it('states the agreement as a count and a rate where shadow mode is decided (AC3)', async () => {
    mockApi({
      ...DASHBOARD_READS,
      '/api/decisions/agreement': { body: { agreed: 47, disagreed: 5, reviewed: 52, windowDays: 7 } },
    });
    renderSurface(<DashboardPage />);

    // Requirement 3's motivating sentence, next to the switch it informs.
    const summary = await screen.findByText(/agreed with 47 of 52/);
    expect(summary).toHaveTextContent('90.4%');
    expect(summary).toHaveTextContent('last 7 days');
  });

  it('reads without an admin key, since the decision list is a PublicRead', async () => {
    const api = mockApi({ ...NO_SUPPRESSIONS, '/api/decisions': page([unreviewed]) });
    renderSurface(<SuppressionsPage />);
    await screen.findByText('Suppressed Some.Show.S02E01.CAM');

    // /api/decisions is not under /api/admin/, so apiFetch attaches no header --
    // #59's ruling, the same treatment /api/activity gets over these very rows.
    expect(api.adminKeyOn('/api/decisions')).toBeUndefined();
  });

  it('distinguishes a shadow-only decision from an enforced one', async () => {
    mockApi({
      ...NO_SUPPRESSIONS,
      '/api/decisions': page([unreviewed, { ...reviewed, id: 43, shadowMode: false }]),
    });
    renderSurface(<SuppressionsPage />);

    // Recorded-but-not-enforced must not read like a suppression that happened.
    // Matched exactly: the summaries begin "Suppressed ...", so a substring
    // match would find those and pass without the badge existing at all.
    expect(await screen.findByText('Shadow only')).toBeInTheDocument();
    expect(screen.getByText('Suppressed', { exact: true })).toBeInTheDocument();
  });

  it('sends the shadow-mode filter to the server when narrowed', async () => {
    const user = userEvent.setup();
    const api = mockApi({ ...NO_SUPPRESSIONS, '/api/decisions': page([unreviewed]) });
    renderSurface(<SuppressionsPage />);
    await screen.findByText('Suppressed Some.Show.S02E01.CAM');

    await user.selectOptions(screen.getByLabelText('Enforcement'), 'shadow');

    const filtered = await vi.waitFor(() => {
      const calls = api.callsTo('/api/decisions');
      const last = calls[calls.length - 1];
      expect(last.url.searchParams.get('shadowMode')).toBe('true');
      return last;
    });
    expect(filtered.method).toBe('GET');
  });

  it('keeps the two stores visually distinct on the shared surface', async () => {
    mockApi({ ...NO_SUPPRESSIONS, '/api/decisions': page([unreviewed]) });
    renderSurface(<SuppressionsPage />);

    // Two panels, two names. The audit log carries no row id and so cannot be
    // reviewed; the decisions panel can. Naming both "Decisions" would read as
    // one list duplicated rather than as the two distinct stores they are.
    expect(
      await screen.findByRole('heading', { level: 2, name: 'Suppression audit log' }),
    ).toBeInTheDocument();
    const decisions = screen.getByRole('heading', { level: 2, name: 'Pipeline decisions' });
    expect(decisions).toBeInTheDocument();

    // No seventh sidebar entry was added: this is one surface, still.
    expect(screen.getByRole('heading', { level: 1, name: 'Suppressions' })).toBeInTheDocument();
  });

  it('leaves the shadow-mode row readable when the agreement read fails', async () => {
    mockApi({
      ...DASHBOARD_READS,
      '/api/decisions/agreement': { status: 500, body: { error: 'boom' } },
    });
    renderSurface(<DashboardPage />);

    // The shadow-mode state is the load-bearing fact and still renders; a
    // fabricated rate beside it would be worse than its absence.
    const shadow = await screen.findByText('Shadow mode');
    const value = shadow.nextElementSibling as HTMLElement;
    expect(within(value).queryByText(/agreement|agreed with/)).toBeNull();
    expect(value).toHaveTextContent('On');
  });
});
