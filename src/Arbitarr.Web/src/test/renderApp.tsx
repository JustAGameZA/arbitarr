import { render } from '@testing-library/react';
import { BrowserRouter, MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import App from '../App';

function newQueryClient() {
  // Per render, and with retries off: a shared client would leak cached data
  // between tests, and a retry turns an assertion failure into a timeout.
  return new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
}

/**
 * Mounts the real App under a MemoryRouter so tests exercise the actual route
 * table rather than a parallel one that can drift from routes.tsx.
 */
export function renderApp(initialEntry = '/') {
  return render(
    <QueryClientProvider client={newQueryClient()}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <App />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

/**
 * Mounts the App against jsdom's own history, for the back/forward test only.
 *
 * MemoryRouter keeps its stack internally and does not listen to `popstate`, so
 * window.history.back() there moves the browser's history and leaves the router
 * where it was -- the test would assert nothing. BrowserRouter subscribes to
 * popstate, which is the behaviour actually shipped in main.tsx.
 *
 * jsdom's history is per-file and persists across tests in that file, so this
 * resets the URL before mounting.
 */
export function renderAppWithBrowserHistory(initialEntry = '/') {
  window.history.pushState({}, '', initialEntry);

  return render(
    <QueryClientProvider client={newQueryClient()}>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </QueryClientProvider>,
  );
}
