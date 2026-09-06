import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ADMIN_KEY_HEADER,
  AdminKeyNotConfiguredError,
  AdminKeyRejectedError,
  ApiError,
  apiFetch,
  needsAdminKey,
} from './client';
import { useAdminKeyStore } from '../state/adminKeyStore';

interface Captured {
  url: string;
  init: RequestInit & { headers?: Record<string, string> };
}

let captured: Captured[] = [];

function mockFetch(status: number, body: unknown = '') {
  const fetchMock = vi.fn((url: string, init: RequestInit) => {
    captured.push({ url, init: init as Captured['init'] });
    const text = typeof body === 'string' ? body : JSON.stringify(body);
    return Promise.resolve(
      new Response(status === 204 ? null : text, {
        status,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const headerOf = (index = 0) => captured[index].init.headers?.[ADMIN_KEY_HEADER];

describe('needsAdminKey', () => {
  it.each([
    ['/api/admin/suppressions', true],
    ['/api/admin/search', true],
    ['/api/admin/rules/7', true],
    ['/api/status', false],
    ['/api/dashboard/summary', false],
    // Not a prefix match: an /api/administration/ route is not admin-gated.
    ['/api/administration/thing', false],
  ])('%s -> %s', (path, expected) => {
    expect(needsAdminKey(path)).toBe(expected);
  });
});

describe('apiFetch admin key attachment', () => {
  beforeEach(() => {
    captured = [];
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('attaches the key to a GET on an admin path', async () => {
    // The verb matters: /api/admin/suppressions and /api/admin/search are both
    // MapGet. Gating on "mutating verbs only" would 401 those two surfaces in
    // production while every mocked component test kept passing.
    useAdminKeyStore.getState().setKey('k-1');
    mockFetch(200, { items: [] });

    await apiFetch('/api/admin/suppressions');

    expect(captured[0].init.method).toBeUndefined();
    expect(headerOf()).toBe('k-1');
  });

  it('attaches the key to a mutating admin request', async () => {
    useAdminKeyStore.getState().setKey('k-1');
    mockFetch(200, { ok: true });

    await apiFetch('/api/admin/rules', { method: 'POST', body: JSON.stringify({}) });

    expect(headerOf()).toBe('k-1');
    expect(captured[0].init.headers?.['Content-Type']).toBe('application/json');
  });

  it('omits the key on a non-admin path even when a key is held', async () => {
    useAdminKeyStore.getState().setKey('k-1');
    mockFetch(200, { status: 'ok' });

    await apiFetch('/api/status');

    expect(headerOf()).toBeUndefined();
  });

  it('never places the key in the URL', async () => {
    useAdminKeyStore.getState().setKey('k-1');
    mockFetch(200, { items: [] });

    await apiFetch('/api/admin/suppressions');

    expect(captured[0].url).toBe('/api/admin/suppressions');
    expect(captured[0].url).not.toContain('k-1');
  });
});

describe('apiFetch error branches', () => {
  beforeEach(() => {
    captured = [];
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('503 sets serverKeyUnset and leaves the key untouched', async () => {
    // The operator's key is not the problem on a fresh install. Clearing it
    // here would ask them to retype a value that cannot satisfy a gate with no
    // key configured on the server side.
    useAdminKeyStore.getState().setKey('k-1');
    mockFetch(503, { error: 'not configured' });

    await expect(apiFetch('/api/admin/rules')).rejects.toBeInstanceOf(AdminKeyNotConfiguredError);

    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(true);
    expect(useAdminKeyStore.getState().key).toBe('k-1');
  });

  it.each([401, 403])('%d clears the key', async (status) => {
    useAdminKeyStore.getState().setKey('k-1');
    mockFetch(status, { error: 'rejected' });

    await expect(apiFetch('/api/admin/rules')).rejects.toBeInstanceOf(AdminKeyRejectedError);

    expect(useAdminKeyStore.getState().key).toBeNull();
    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(false);
  });

  it('raises a plain ApiError for other failures without touching the store', async () => {
    useAdminKeyStore.getState().setKey('k-1');
    mockFetch(500, { error: 'boom' });

    await expect(apiFetch('/api/admin/rules')).rejects.toBeInstanceOf(ApiError);

    expect(useAdminKeyStore.getState().key).toBe('k-1');
    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(false);
  });

  it('returns undefined for a 204 rather than failing to parse an empty body', async () => {
    mockFetch(204);

    await expect(apiFetch('/api/status')).resolves.toBeUndefined();
  });
});
