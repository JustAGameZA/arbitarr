import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SystemPage from './System';
import { RESTORE_CONFIRMATION_WORD } from './queries';
import { ADMIN_KEY_HEADER } from '../../api/client';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';

/**
 * The Status tab's routes are mocked in every case even when the assertions are about the
 * Backup tab: the page mounts on Status, and an unrouted request renders a 501 error
 * where the test expects a panel. Same reasoning as Logs.test.tsx.
 */
const statusRoutes = {
  '/api/system/build': {
    body: {
      commitSha: 'a1b2c3d',
      imageTag: 'arbitarr:a1b2c3d',
      buildTimestampUtc: '2026-09-06T12:00:00Z',
      informationalVersion: '1.2.3+a1b2c3d',
      uptimeSeconds: 3725,
    },
  },
  '/api/health/staleness': {
    body: {
      worst_case_unjudged_age: '02:30:00',
      search_result_cache_band_bound: '00:15:00',
      classifier_queue_latency: '00:00:00',
      fresh_until: '00:45:00',
      refresh_lead_plus_worker_cycle_interval: '00:05:00',
      serve_until: '03:00:00',
    },
  },
  '/api/admin/observability': {
    body: {
      counters: {
        resultsIn: 1200,
        suppressedTotal: 340,
        suppressedBySourceAndReason: {},
        llmCalls: 88,
        llmFailures: 3,
        verdictCache: { hits: 60, misses: 28, rate: 0.68 },
        searchCache: {
          freshHits: 400,
          staleButValidHits: 120,
          fetchedMisses: 80,
          degradedMisses: 5,
          hitRate: 0.86,
        },
        servedAgeDistribution: {},
      },
      metadataCache: { entries: 512, negativeEntries: 64, distinctSeries: 128 },
    },
  },
};

const statusBody = {
  lastBackupAt: '2026-09-07T09:00:00+00:00',
  lastBackupAutomatic: true,
  automaticBackupsRetained: 7,
  lastBackupFailureAt: null,
  lastBackupFailureReason: null,
  lastRestoreAt: null,
  lastRestoreSucceeded: false,
  lastRestoreMessage: null,
};

const allRoutes = {
  ...statusRoutes,
  '/api/admin/backup/status': { body: statusBody },
};

async function openBackupTab(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('tab', { name: 'Backup' }));
  return screen.findByRole('button', { name: /download backup/i });
}

describe('Backup tab', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: 'test-key', serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('states at the download point that the archive contains secrets', async () => {
    // AC3 / plan §3.3: the warning belongs beside the button, not in a doc. An operator
    // who never opens the docs must still be told what they are about to download.
    const user = userEvent.setup();
    mockApi(allRoutes);
    renderSurface(<SystemPage />);

    await openBackupTab(user);

    expect(screen.getByText(/this file is a credential/i)).toBeInTheDocument();
    expect(
      screen.getAllByText((_, element) =>
        (element?.textContent ?? '').includes('API keys of every configured source'),
      ).length,
    ).toBeGreaterThan(0);
  });

  it('says the application log database is not in the archive', async () => {
    // The log store is a SEPARATE SQLite file kept out of backups on purpose. An operator
    // who believes a backup covers their logs is wrong in a way that only surfaces after a
    // disaster, so the copy states the exclusion rather than staying silent about it.
    const user = userEvent.setup();
    mockApi(allRoutes);
    renderSurface(<SystemPage />);

    await openBackupTab(user);

    // Matched with a node-spanning matcher: the sentence is broken by a <strong>, so a
    // plain string query would find nothing even though the text is on screen.
    expect(
      screen.getAllByText((_, element) =>
        (element?.textContent ?? '').includes('not contain the application log database'),
      ).length,
    ).toBeGreaterThan(0);
  });

  it('names the release-GUID consequence before a restore is possible', async () => {
    // Issue #56: "GUID-secret replacement must be called out explicitly during restore."
    // Restoring an older key invalidates every GUID issued since, and an operator who was
    // not told cannot have consented.
    const user = userEvent.setup();
    mockApi(allRoutes);
    renderSurface(<SystemPage />);

    await openBackupTab(user);

    // Same node-spanning treatment: a <strong> splits the warning sentence.
    expect(
      screen.getAllByText((_, element) =>
        (element?.textContent ?? '').includes('invalidates every release GUID issued since'),
      ).length,
    ).toBeGreaterThan(0);
    expect(screen.getByText(/saves a backup of your current state before applying/i)).toBeInTheDocument();
  });

  it('does not warn about a failed backup when the last pass succeeded', async () => {
    // POSITIVE CONTROL for the test below. Without it, "the warning appears when a failure is
    // reported" would be satisfied just as well by a banner that is always rendered.
    const user = userEvent.setup();
    mockApi(allRoutes);
    renderSurface(<SystemPage />);

    await openBackupTab(user);

    expect(screen.queryByText(/last automatic backup failed/i)).toBeNull();
  });

  it('surfaces a failed automatic backup and says the timestamp is older than it looks', async () => {
    // Provenance on the degraded path (docs/standards/data.md). Without it a broken scheduled
    // backup shows only as a "Last backup" time that stopped moving -- which reads as a healthy
    // safety net, and is the stale-backup failure this tab exists to prevent.
    const user = userEvent.setup();
    mockApi({
      ...statusRoutes,
      '/api/admin/backup/status': {
        body: {
          ...statusBody,
          lastBackupFailureAt: '2026-09-07T10:00:00+00:00',
          lastBackupFailureReason: 'IOException: There is not enough space on the disk.',
        },
      },
    });
    renderSurface(<SystemPage />);

    await openBackupTab(user);

    expect(screen.getByText(/last automatic backup failed/i)).toBeInTheDocument();
    expect(screen.getByText(/not enough space on the disk/i)).toBeInTheDocument();
    // The whole point of the copy: the surviving timestamp is not what it appears to be.
    expect(screen.getByText(/older than it looks/i)).toBeInTheDocument();
  });

  it('shows the last backup time and the retained count', async () => {
    const user = userEvent.setup();
    mockApi(allRoutes);
    renderSurface(<SystemPage />);

    await openBackupTab(user);

    expect(screen.getByText('Keeping 7')).toBeInTheDocument();
    // Rendered as a <time> carrying the server's exact instant in `title`, so the
    // assertion does not depend on the test machine's locale or zone.
    expect(screen.getByTitle('2026-09-07T09:00:00+00:00')).toBeInTheDocument();
  });

  it('says automatic backups are off when the retained count is zero', async () => {
    // A timestamp that quietly stopped advancing is exactly the stale-safety-net failure
    // #56 exists to prevent, so the off state is named rather than left to be inferred.
    const user = userEvent.setup();
    mockApi({
      ...statusRoutes,
      '/api/admin/backup/status': { body: { ...statusBody, automaticBackupsRetained: 0 } },
    });
    renderSurface(<SystemPage />);

    await openBackupTab(user);

    expect(screen.getByText('Off')).toBeInTheDocument();
    expect(screen.getByText(/only moves when you download one/i)).toBeInTheDocument();
  });

  it('names what would fill the panel when no backup has ever been taken', async () => {
    const user = userEvent.setup();
    mockApi({
      ...statusRoutes,
      '/api/admin/backup/status': { body: { ...statusBody, lastBackupAt: null } },
    });
    renderSurface(<SystemPage />);

    await openBackupTab(user);

    expect(screen.getByText(/no backup has been taken yet/i)).toBeInTheDocument();
  });

  it('keeps restore disabled until both a file and the exact confirmation word are given', async () => {
    // The typed confirmation is the last gate in front of the most destructive action in
    // the product, so a near-miss must not arm it.
    const user = userEvent.setup();
    mockApi(allRoutes);
    renderSurface(<SystemPage />);

    await openBackupTab(user);

    const restore = screen.getByRole('button', { name: /restore and restart/i });
    expect(restore).toBeDisabled();

    const file = new File(['archive bytes'], 'backup.zip', { type: 'application/zip' });
    await user.upload(screen.getByLabelText(/backup archive/i), file);

    // A file alone is not enough.
    expect(restore).toBeDisabled();

    const confirm = screen.getByLabelText(new RegExp(`type ${RESTORE_CONFIRMATION_WORD} to confirm`, 'i'));

    // Lower case is a near-miss, not a confirmation.
    await user.type(confirm, RESTORE_CONFIRMATION_WORD.toLowerCase());
    expect(restore).toBeDisabled();

    await user.clear(confirm);
    await user.type(confirm, RESTORE_CONFIRMATION_WORD);
    expect(restore).toBeEnabled();
  });

  it('posts the archive with the admin key in a header and never in the URL', async () => {
    // AC8 / plan §3.3: no credential in a URL, on either route. The admin key must travel
    // in the header, because a URL-borne one lands in browser history and proxy logs.
    const user = userEvent.setup();
    const api = mockApi({
      ...allRoutes,
      '/api/admin/restore': {
        body: {
          succeeded: true,
          message: 'Restore applied. Arbitarr is shutting down now.',
          preRestoreBackupTaken: true,
          restarting: true,
        },
      },
    });
    renderSurface(<SystemPage />);

    await openBackupTab(user);

    const file = new File(['archive bytes'], 'backup.zip', { type: 'application/zip' });
    await user.upload(screen.getByLabelText(/backup archive/i), file);
    await user.type(
      screen.getByLabelText(new RegExp(`type ${RESTORE_CONFIRMATION_WORD} to confirm`, 'i')),
      RESTORE_CONFIRMATION_WORD,
    );
    await user.click(screen.getByRole('button', { name: /restore and restart/i }));

    await screen.findByText(/restore applied/i);

    const posted = api.callsTo('/api/admin/restore');
    expect(posted).toHaveLength(1);
    expect(posted[0].method).toBe('POST');
    expect(posted[0].headers[ADMIN_KEY_HEADER]).toBe('test-key');

    // Positive control on the URL assertion: the request really was made to this path, so
    // "no key in the query string" is a statement about a live request and not a vacuous
    // read of a request that never happened.
    expect(posted[0].path).toBe('/api/admin/restore');
    expect(posted[0].url.search).toBe('');
  });

  it('reports the outcome of a refused restore without clearing the form', async () => {
    const user = userEvent.setup();
    mockApi({
      ...allRoutes,
      '/api/admin/restore': {
        status: 400,
        body: {
          succeeded: false,
          message: "That backup was taken from a newer version of Arbitarr.",
          preRestoreBackupTaken: false,
          restarting: false,
        },
      },
    });
    renderSurface(<SystemPage />);

    await openBackupTab(user);

    const file = new File(['archive bytes'], 'backup.zip', { type: 'application/zip' });
    await user.upload(screen.getByLabelText(/backup archive/i), file);
    await user.type(
      screen.getByLabelText(new RegExp(`type ${RESTORE_CONFIRMATION_WORD} to confirm`, 'i')),
      RESTORE_CONFIRMATION_WORD,
    );
    await user.click(screen.getByRole('button', { name: /restore and restart/i }));

    // The operator is told what went wrong, not left with a silent no-op.
    await screen.findByText(/newer version of Arbitarr/i);
  });
});
