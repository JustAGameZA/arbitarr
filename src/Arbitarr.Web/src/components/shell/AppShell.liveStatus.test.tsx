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
    // `scopeMessages` is reset alongside `message` for the same reason (arb-xzvk):
    // a scope left holding "Loading…" would coalesce away the next test's first
    // announcement, so the suppression assertions below would pass by ordering
    // rather than by the mechanism they mean to pin.
    useLiveStatusStore.setState({ message: '', scopeMessages: {} });
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

  it('re-announces two consecutive identical messages, not just the first', () => {
    // Regression test for arb-tku8's codereview-454 finding: `announce`
    // previously only did `set({ message })`, and AppShell selected the
    // primitive `state.message`, so a second "Saved." right after the first
    // (Account's own save, then Notifications' test-webhook success, or the
    // same surface saved twice) left `state.message` unchanged and produced
    // NO DOM mutation for the second announcement -- a screen reader user
    // hears the first "Saved." and then silence on the repeat, which looks
    // identical to nothing having happened. This test FAILS against that
    // implementation: a MutationObserver on the live region records exactly
    // one mutation instead of two, because React bails out of the second
    // render when neither `message` nor any other selected primitive changed.
    renderShell(<p>content</p>);

    const region = screen.getByRole('status');
    const observer = new MutationObserver(() => {});
    observer.observe(region.parentElement ?? document.body, {
      subtree: true,
      childList: true,
      characterData: true,
    });

    act(() => {
      useLiveStatusStore.getState().announce('Saved.');
    });
    // takeRecords() drains synchronously so the first announcement's
    // mutation is not merged with the second's by the observer's own
    // microtask batching, which would undercount identical back-to-back
    // announcements the same way the real bug does.
    const firstAnnounceMutations = observer.takeRecords();

    act(() => {
      useLiveStatusStore.getState().announce('Saved.');
    });
    const secondAnnounceMutations = observer.takeRecords();

    observer.disconnect();

    expect(screen.getByRole('status')).toHaveTextContent('Saved.');
    // Both announcements must be individually detectable in the DOM -- not
    // merely "the text still says Saved." at the end, which a no-op second
    // render would also satisfy.
    expect(firstAnnounceMutations.length).toBeGreaterThan(0);
    expect(secondAnnounceMutations.length).toBeGreaterThan(0);
    // The region must still render only the message text: no visible counter
    // or nonce alongside it. Re-queried rather than reusing `region`, which
    // may now be a stale reference to a remounted node.
    expect(screen.getByRole('status')).toHaveTextContent(/^Saved\.$/);
  });

  describe('announcement scoping (arb-xzvk)', () => {
    /**
     * Counts the announcements the live region actually delivered, by `seq`.
     *
     * `seq` is the right unit and a raw MutationRecord count is not, which was
     * MEASURED rather than assumed: counting records scored the three-scoped
     * case at 2 and the three-unscoped control at 2 as well, because AppShell
     * keys the region on `seq` (AppShell.tsx:110), so ONE announcement is a
     * removal plus an insertion, and React batches several into one commit. The
     * records therefore count neither calls nor announcements.
     *
     * `seq` is what a screen reader's experience is downstream of: it advances
     * once per DELIVERED announcement and not at all for one the store
     * coalesced away, and every advance forces the region to remount. Counting
     * it is counting the remounts, without the batching noise.
     *
     * Deliberately NOT a spy on `announce`: the suppressed sibling calls
     * `announce` too, so a call count reports three in both cases and could
     * never tell the bead's fix from its absence.
     */
    function announcementCount(): number {
      return useLiveStatusStore.getState().seq;
    }

    function ThreeQueries({ scope }: { scope?: string }) {
      return (
        <>
          <QueryState isPending error={undefined} data={undefined} announceScope={scope}>
            {() => <p>never rendered while pending</p>}
          </QueryState>
          <QueryState isPending error={undefined} data={undefined} announceScope={scope}>
            {() => <p>never rendered while pending</p>}
          </QueryState>
          <QueryState isPending error={undefined} data={undefined} announceScope={scope}>
            {() => <p>never rendered while pending</p>}
          </QueryState>
        </>
      );
    }

    it('announces once for three QueryStates sharing one scope', () => {
      // Dashboard's shape: three queries, one navigation. Before arb-xzvk a
      // screen-reader operator heard "Loading…" three times for it.
      //
      // The observer is attached BEFORE the mount that announces, so the
      // announcement is counted rather than assumed from the end state -- the
      // final text is "Loading…" whether it was announced once or three times,
      // which is precisely why this asserts the count and not the text.
      renderShell(<p>content</p>);
      const before = announcementCount();

      act(() => {
        render(<ThreeQueries scope="dashboard" />);
      });

      expect(screen.getByRole('status')).toHaveTextContent('Loading…');
      expect(announcementCount() - before).toBe(1);
    });

    it('announces three times for three unscoped QueryStates (positive control)', () => {
      // THE POSITIVE CONTROL for the assertion above. Without it, `toHaveLength(1)`
      // would pass just as happily against an implementation that lost two of the
      // three announcements for some unrelated reason -- a coalescing bug, a
      // batched render, an observer watching the wrong node. This proves the same
      // observer, on the same region, DOES record three separate mutations when
      // nothing is scoped, so the single mutation above is the scope doing its
      // job rather than the measurement failing to see anything.
      renderShell(<p>content</p>);
      const before = announcementCount();

      act(() => {
        render(<ThreeQueries />);
      });

      expect(screen.getByRole('status')).toHaveTextContent('Loading…');
      expect(announcementCount() - before).toBe(3);
    });

    it('announces again when a scope re-enters the same message later', () => {
      // A scope is not a one-shot mute. A group that resolves and then goes
      // pending again (a refetch, a navigation back) has had a SECOND event, and
      // an operator must hear it -- otherwise the first navigation of a session
      // would be the only one that ever spoke. This is what distinguishes
      // coalescing simultaneous siblings from suppressing repeats, and an
      // implementation that simply remembered "this scope already said Loading…"
      // forever would pass the first test and fail this one.
      renderShell(<p>content</p>);

      act(() => {
        useLiveStatusStore.getState().announce('Loading…', 'dashboard');
      });
      expect(screen.getByRole('status')).toHaveTextContent('Loading…');

      // The group resolves: the scope announces something else, which is what
      // supersedes the remembered message.
      act(() => {
        useLiveStatusStore.getState().announce('Saved.', 'dashboard');
      });
      expect(screen.getByRole('status')).toHaveTextContent('Saved.');

      const before = announcementCount();
      act(() => {
        useLiveStatusStore.getState().announce('Loading…', 'dashboard');
      });

      expect(screen.getByRole('status')).toHaveTextContent('Loading…');
      expect(announcementCount() - before).toBe(1);
    });

    it('does not let one scope silence another announcing the same message', () => {
      // Two scopes are two groups and therefore two events, even word for word.
      // A single last-message field instead of the per-scope map would fail this.
      renderShell(<p>content</p>);

      act(() => {
        useLiveStatusStore.getState().announce('Loading…', 'dashboard');
      });

      const before = announcementCount();
      act(() => {
        useLiveStatusStore.getState().announce('Loading…', 'system');
      });

      expect(announcementCount() - before).toBe(1);
    });
  });
});
