import { vi } from 'vitest';

import { ADMIN_KEY_HEADER } from '../api/client';

/**
 * A fetch double that answers by path, and records what it was asked.
 *
 * The surfaces' tests assert on the request as much as on the render -- whether
 * the admin key header was attached (AC7), and what query string a control
 * produced (AC8) -- so the double has to keep the calls, not just the replies.
 *
 * Routes are matched by prefix on the pathname, longest first, so a handler for
 * '/api/admin/rules' also answers '/api/admin/rules/7' unless a more specific
 * entry is registered.
 */

export interface CapturedCall {
  url: URL;
  path: string;
  method: string;
  headers: Record<string, string>;
  body: string | undefined;
}

export interface MockRoute {
  status?: number;
  body?: unknown;
}

export type MockRoutes = Record<string, MockRoute | undefined>;

export interface MockApi {
  calls: CapturedCall[];
  /** Calls whose pathname starts with `prefix`, in order. */
  callsTo(prefix: string): CapturedCall[];
  /** The admin key header on the first call to `prefix`, or undefined. */
  adminKeyOn(prefix: string): string | undefined;
  /** Replaces the reply for one route mid-test (e.g. after a mutation). */
  set(path: string, route: MockRoute): void;
}

/**
 * jsdom has no base URL for a bare path, so requests are resolved against this
 * placeholder purely to parse them. It is never contacted; nothing is served.
 */
const TEST_ORIGIN = 'http://localhost';

function headersOf(init: RequestInit | undefined): Record<string, string> {
  const result: Record<string, string> = {};
  const raw = init?.headers;
  if (raw === undefined) {
    return result;
  }
  if (raw instanceof Headers) {
    raw.forEach((value, name) => {
      result[name] = value;
    });
    return result;
  }
  if (Array.isArray(raw)) {
    for (const [name, value] of raw) {
      result[name] = value;
    }
    return result;
  }
  return { ...(raw as Record<string, string>) };
}

export function mockApi(routes: MockRoutes): MockApi {
  const table = new Map<string, MockRoute>(Object.entries(routes) as [string, MockRoute][]);
  const calls: CapturedCall[] = [];

  const resolve = (path: string): MockRoute | undefined => {
    let best: MockRoute | undefined;
    let bestLength = -1;
    for (const [prefix, route] of table) {
      if (path.startsWith(prefix) && prefix.length > bestLength) {
        best = route;
        bestLength = prefix.length;
      }
    }
    return best;
  };

  vi.stubGlobal(
    'fetch',
    vi.fn((input: string, init?: RequestInit) => {
      const url = new URL(input, TEST_ORIGIN);
      calls.push({
        url,
        path: url.pathname,
        method: init?.method ?? 'GET',
        headers: headersOf(init),
        body: typeof init?.body === 'string' ? init.body : undefined,
      });

      const route = resolve(url.pathname);
      if (route === undefined) {
        // Unrouted rather than silently empty: a surface that grows a request
        // nobody mocked should fail loudly instead of rendering a blank panel.
        return Promise.resolve(
          new Response(JSON.stringify({ error: `No mock route for ${url.pathname}` }), {
            status: 501,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }

      const status = route.status ?? 200;
      const hasBody = route.body !== undefined;
      return Promise.resolve(
        new Response(status === 204 || !hasBody ? null : JSON.stringify(route.body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );

  return {
    calls,
    callsTo: (prefix) => calls.filter((call) => call.path.startsWith(prefix)),
    adminKeyOn: (prefix) =>
      calls.find((call) => call.path.startsWith(prefix))?.headers[ADMIN_KEY_HEADER],
    set: (path, route) => {
      table.set(path, route);
    },
  };
}
