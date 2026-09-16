import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SuppressionsPage from './Suppressions';
import { useAdminKeyStore } from '../../state/adminKeyStore';
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
];

const EMPTY_DECISIONS = { '/api/decisions': { body: { decisions: [], nextCursor: null } } };

/**
 * The "Show titles" / "Hide titles" toggle exposes its disclosure state and
 * target to assistive tech (arb-sggv). System's tablist and
 * PageToolbarMenu's trigger already do this correctly elsewhere in the app;
 * this is the same pattern applied to the row-detail expander.
 */
describe('Suppressions row-detail expander ARIA (arb-sggv)', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('exposes aria-expanded=false by default, then aria-expanded=true with a resolvable aria-controls once opened', async () => {
    const user = userEvent.setup();
    mockApi({
      ...EMPTY_DECISIONS,
      '/api/admin/suppressions': { body: entries },
      '/api/admin/search/upstream-guid-1/explanation': {
        body: { title: 'Some Show S02E01 1080p', originalTitle: 'Some.Show.S02E01.1080p.WEB' },
      },
    });
    renderSurface(<SuppressionsPage />);
    await screen.findByText('upstream-guid-1');

    const toggle = screen.getByRole('button', { name: 'Show titles' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(toggle).not.toHaveAttribute('aria-controls');

    await user.click(toggle);

    const opened = screen.getByRole('button', { name: 'Hide titles' });
    expect(opened).toHaveAttribute('aria-expanded', 'true');
    const controlsId = opened.getAttribute('aria-controls');
    expect(controlsId).not.toBeNull();
    expect(document.getElementById(controlsId as string)).not.toBeNull();
  });
});
