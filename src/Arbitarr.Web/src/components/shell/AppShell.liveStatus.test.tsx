import { act, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it } from 'vitest';

import { AppShell } from './AppShell';
import { QueryState } from '../../surfaces/QueryState';
import { useLiveStatusStore } from '../../state/liveStatusStore';

/**
 * arb-tku8: one polite aria-live status region in AppShell, that QueryState's
 * pending state and a surface's success message both announce through.
 *
 * Renders AppShell directly via its `children` test seam (AppShell.tsx's own
 * doc comment names this as the seam for exactly this: content without a
 * router outlet), so the assertions exercise the real, always-mounted live
 * region rather than a stand-in for it.
 *
 * This test FAILS against the markup before arb-tku8: there was no
 * `role="status"`/`aria-live` element in AppShell at all, so
 * `getByRole('status')` below would throw before either assertion ran.
 */
describe('AppShell live status region (arb-tku8)', () => {
  beforeEach(() => {
    // Reset explicitly rather than relying on module state: the store is a
    // singleton for the whole test file, so a test left announcing "Loading…"
    // would otherwise decide the outcome of the next one by ordering alone --
    // the same reason tableDensityStore.test.ts resets `density`.
    useLiveStatusStore.setState({ message: '' });
  });

  function renderShell(children: React.ReactNode) {
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });

    return render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <AppShell>{children}</AppShell>
        </MemoryRouter>
      </QueryClientProvider>,
    );
  }

  it('exposes exactly one polite live region, present before anything is announced', () => {
    renderShell(<p>content</p>);

    const region = screen.getByRole('status');
    expect(region).toHaveAttribute('aria-live', 'polite');
    expect(region).toBeEmptyDOMElement();
  });

  it("announces QueryState's pending branch through the shared region", () => {
    renderShell(
      <QueryState isPending error={undefined} data={undefined}>
        {() => <p>never rendered while pending</p>}
      </QueryState>,
    );

    // The visible "Loading…" paragraph QueryState renders is a SEPARATE element
    // from the live region -- this asserts the shared status element specifically
    // picked up the announcement, not merely that the word appears somewhere.
    expect(screen.getByRole('status')).toHaveTextContent('Loading…');
  });

  it('does not announce a query that resolves straight to success', () => {
    renderShell(
      <QueryState isPending={false} error={undefined} data="ready">
        {(data) => <p>{data}</p>}
      </QueryState>,
    );

    expect(screen.getByText('ready')).toBeInTheDocument();
    expect(screen.getByRole('status')).toBeEmptyDOMElement();
  });

  it("carries a surface's success message through the same region a save announces", () => {
    renderShell(<p>content</p>);

    expect(screen.getByRole('status')).toBeEmptyDOMElement();

    // The seven `.success` "Saved." call sites all funnel through this same
    // store (surfaces/QueryState.tsx's useAnnounceOnChange) rather than each
    // growing its own region -- asserted here directly against the store, the
    // shared mechanism every one of those call sites uses.
    act(() => {
      useLiveStatusStore.getState().announce('Saved.');
    });

    expect(screen.getByRole('status')).toHaveTextContent('Saved.');
  });
});
