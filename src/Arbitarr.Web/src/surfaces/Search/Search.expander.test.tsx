import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SearchPage from './Search';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';

const response = {
  releases: [
    {
      title: 'Some.Series.S01E02.1080p',
      guid: 'upstream-guid-1',
      size: 1610612736,
      category: [5000, 5040],
      sourceName: 'nzbhydra',
      pubDate: '2026-09-05T22:10:00+00:00',
      aiVerdict: null,
    },
  ],
  provenance: {
    cacheAge: '00:01:30.5000000',
    cacheBand: 0,
    rateLimitedSources: [],
  },
};

async function runSearch(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText('Query'), 'some series');
  await user.click(screen.getByRole('button', { name: 'Search' }));
}

/**
 * The "Explain" toggle exposes its disclosure state and target to assistive
 * tech (arb-sggv), mirroring the Suppressions row expander and the in-repo
 * System tablist / PageToolbarMenu trigger pattern.
 */
describe('Search row-detail expander ARIA (arb-sggv)', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('exposes aria-expanded=false by default, then aria-expanded=true with a resolvable aria-controls once opened', async () => {
    const user = userEvent.setup();
    mockApi({
      '/api/admin/search': { body: response },
      '/api/admin/search/upstream-guid-1/explanation': {
        body: { title: 'Some Series S01E02 1080p', originalTitle: 'Some.Series.S01E02.1080p.WEB' },
      },
    });
    renderSurface(<SearchPage />);

    await runSearch(user);
    const toggle = await screen.findByRole('button', { name: 'Explain' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(toggle).not.toHaveAttribute('aria-controls');

    await user.click(toggle);

    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const controlsId = toggle.getAttribute('aria-controls');
    expect(controlsId).not.toBeNull();
    expect(document.getElementById(controlsId as string)).not.toBeNull();
  });
});
