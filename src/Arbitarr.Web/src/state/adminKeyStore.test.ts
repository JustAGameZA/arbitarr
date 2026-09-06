import { beforeEach, describe, expect, it } from 'vitest';

import { useAdminKeyStore } from './adminKeyStore';

const reset = () => useAdminKeyStore.setState({ key: null, serverKeyUnset: false });

describe('adminKeyStore', () => {
  beforeEach(reset);

  it('starts with no key and no server-unset signal', () => {
    expect(useAdminKeyStore.getState().key).toBeNull();
    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(false);
  });

  it('stores a key and clears it again', () => {
    useAdminKeyStore.getState().setKey('k-1');
    expect(useAdminKeyStore.getState().key).toBe('k-1');

    useAdminKeyStore.getState().clearKey();
    expect(useAdminKeyStore.getState().key).toBeNull();
  });

  it('toggles serverKeyUnset independently of key', () => {
    // The two states describe different failures -- "you have no key" versus
    // "the server has none" -- and the top bar shows opposite affordances for
    // them. If either write bled into the other, a 503 on a fresh install would
    // silently discard a perfectly good key, or clearing a rejected key would
    // claim the server is unconfigured.
    useAdminKeyStore.getState().setKey('k-1');
    useAdminKeyStore.getState().setServerKeyUnset(true);

    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(true);
    expect(useAdminKeyStore.getState().key).toBe('k-1');

    useAdminKeyStore.getState().clearKey();
    expect(useAdminKeyStore.getState().key).toBeNull();
    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(true);

    useAdminKeyStore.getState().setServerKeyUnset(false);
    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(false);
  });

  it('does not persist the key to web storage', () => {
    // Belt and braces alongside the CI grep for localStorage|sessionStorage:
    // the grep catches the source text, this catches a `persist` middleware
    // pulled in transitively that writes under a key the grep never sees.
    useAdminKeyStore.getState().setKey('super-secret-key');

    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });
});
