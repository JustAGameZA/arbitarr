import type { ReactElement } from 'react';
import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * Mounts one surface on its own, without the shell.
 *
 * The shell has its own tests; mounting it again around every surface would
 * make each surface test depend on the sidebar's markup, so a nav change would
 * break twenty unrelated assertions. A MemoryRouter is still needed because
 * surfaces render <Link>s.
 *
 * A fresh QueryClient per render, with retries off: a shared one leaks cached
 * responses between tests, and a retry turns a failed assertion into a timeout.
 */
export function renderSurface(element: ReactElement) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });

  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>{element}</MemoryRouter>
    </QueryClientProvider>,
  );
}
