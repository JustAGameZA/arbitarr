import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { QueryState, errorMessage } from './QueryState';
import { SourcesSection } from './Settings/Sources/Sources';
import { ApiError } from '../api/client';
import { useAdminKeyStore } from '../state/adminKeyStore';
import { useLiveStatusStore } from '../state/liveStatusStore';
import { AppShell } from '../components/shell/AppShell';
import { mockApi } from '../test/mockApi';

const SOURCES = '/api/admin/sources';

/**
 * arb-z505's acceptance test: the surfaces that hand-rolled their own
 * pending/error branches now go through QueryState, and are asserted against
 * the markup and the announcement QueryState actually contracts.
 *
 * Mounts the real AppShell rather than `renderSurface`, because the pending
 * announcement's destination is the SHELL's single shared live region
 * (AppShell.tsx's `role="status"`), not anything QueryState renders itself.
 * QueryState's own pending paragraph carries NO role — a test asserting
 * `role="status"` on it would fail against the correct implementation. This
 * follows components/shell/AppShell.liveStatus.test.tsx, which is the model
 * for reaching the shared region through AppShell's `children` seam.
 *
 * Measured against the pre-migration bespoke ternary rather than assumed: of
 * the three Sources assertions below, the PENDING one failed and the other two
 * passed. That is reported rather than tidied away, because it is the honest
 * result and it says precisely what the migration buys. The bead expected the
 * old ternary to lack `role="alert"`; it did not — it already rendered
 * `role="alert"` and already called `errorMessage`, so those two arms were
 * genuinely equivalent and their assertions pin the migration against
 * regression rather than proving it was needed. What the old ternary did NOT
 * do is announce: it rendered a bare `<p>Loading…</p>` wired to nothing, so
 * the shared live region stayed empty and `getByRole('status')` held no text
 * until it timed out. A screen-reader operator got silence where every
 * QueryState surface in the app speaks.
 */
function renderInShell(children: ReactNode) {
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

describe('QueryState (arb-z505)', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    useLiveStatusStore.setState({ message: '', seq: 0 });
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  describe("Sources' migrated list state", () => {
    it('renders a failed source list as a role=alert paragraph carrying errorMessage', async () => {
      mockApi({ [SOURCES]: { status: 500, body: { error: 'Source store unavailable.' } } });
      renderInShell(<SourcesSection />);

      const alert = await screen.findByRole('alert');
      // The server's own reason verbatim, which is what errorMessage yields for
      // an ApiError with an `{ error }` body -- not a generic substitute.
      expect(alert).toHaveTextContent('Source store unavailable.');
    });

    it('announces the pending source list through the shared shell live region', async () => {
      // Never resolves: holds the query in its pending state for the life of
      // the assertion, so the announcement is observed while it is true rather
      // than raced against a resolution.
      vi.stubGlobal(
        'fetch',
        vi.fn(() => new Promise<Response>(() => {})),
      );
      renderInShell(<SourcesSection />);

      // The visible "Loading…" paragraph is a SEPARATE element from the live
      // region; this asserts the shared region specifically received it.
      await waitFor(() => {
        expect(screen.getByRole('status')).toHaveTextContent('Loading…');
      });
    });

    it('still renders the empty-list sentence, distinct from pending', async () => {
      mockApi({ [SOURCES]: { body: [] } });
      renderInShell(<SourcesSection />);

      expect(
        await screen.findByText(
          'No sources configured — add an NZBHydra2 base URL and API key below to start searching.',
        ),
      ).toBeInTheDocument();
      // An empty list is a LOADED state: conflating it with pending is exactly
      // what the four-arm ternary existed to keep apart.
      //
      // Scoped to the VISIBLE paragraph, excluding the shared live region:
      // that region legitimately still holds the "Loading…" announced while
      // the query was in flight, because an announcement is a record of an
      // event and is never retracted (the store has no clear, by design --
      // see liveStatusStore's `seq` doc). A bare `queryByText` matches it too
      // and would assert the opposite of what this test means.
      const spinner = screen.queryAllByText('Loading…').filter((node) => node.role !== 'status');
      expect(spinner).toHaveLength(0);
    });
  });

  describe('renderError', () => {
    it('defaults to errorMessage, leaving every existing call site unchanged', () => {
      renderInShell(
        <QueryState isPending={false} error={new Error('Boom.')} data={undefined}>
          {() => <p>never rendered</p>}
        </QueryState>,
      );

      expect(screen.getByRole('alert')).toHaveTextContent('Boom.');
    });

    it('lets a surface supply its own sentence, still inside role=alert', () => {
      renderInShell(
        <QueryState
          isPending={false}
          error={new ApiError(404, 'Not Found', undefined)}
          data={undefined}
          renderError={(error) =>
            error instanceof ApiError && error.status === 404 ? 'Surface-specific sentence.' : errorMessage(error)
          }
        >
          {() => <p>never rendered</p>}
        </QueryState>,
      );

      const alert = screen.getByRole('alert');
      expect(alert).toHaveTextContent('Surface-specific sentence.');
      // The override replaces the TEXT, not the treatment: the role="alert"
      // that makes an error interrupt on its own is QueryState's, not the
      // caller's to forget.
      expect(alert).toHaveAttribute('role', 'alert');
    });

    it('falls through to the default for a non-404 when the override delegates', () => {
      renderInShell(
        <QueryState
          isPending={false}
          error={new ApiError(500, 'Server Error', { error: 'Upstream refused.' })}
          data={undefined}
          renderError={(error) =>
            error instanceof ApiError && error.status === 404 ? 'Surface-specific sentence.' : errorMessage(error)
          }
        >
          {() => <p>never rendered</p>}
        </QueryState>,
      );

      expect(screen.getByRole('alert')).toHaveTextContent('Upstream refused.');
      expect(screen.queryByText('Surface-specific sentence.')).not.toBeInTheDocument();
    });

    it('does not announce an error politely, however it is rendered', () => {
      renderInShell(
        <QueryState
          isPending
          error={new ApiError(404, 'Not Found', undefined)}
          data={undefined}
          renderError={() => 'Surface-specific sentence.'}
        >
          {() => <p>never rendered</p>}
        </QueryState>,
      );

      // `isPending` is true here AND an error is present: the pending-or-
      // success-never-error routing rule (QueryState's single
      // `useAnnounceOnChange` call) must keep the polite queue silent so the
      // assertive alert is not delayed behind it.
      expect(screen.getByRole('status')).toBeEmptyDOMElement();
    });
  });
});
