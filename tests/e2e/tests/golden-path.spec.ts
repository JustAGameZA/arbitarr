import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

/**
 * The golden path (arb-rga.7): a fresh instance is bootstrapped, a source is added, a
 * search runs against the stub upstream, and rows come back -- with the admin key never
 * reaching browser storage.
 *
 * Serial by declaration, because these are steps of ONE path over one container's mutable
 * state, not independent cases: the key is set once and the source created once.
 */
test.describe.configure({ mode: 'serial' });

/** Matches AdminApiKeyFilter.HeaderName and the frontend's ADMIN_KEY_HEADER. */
const ADMIN_KEY_HEADER = 'X-Admin-Api-Key';

/**
 * Invented for this run, and >= the 16-character floor SettingsValidator.ValidateAdminApiKey
 * enforces. Not a secret: it is created and discarded inside the job, and the container is
 * destroyed with it. It must still never be hardcoded anywhere a human could mistake it for
 * a real credential, which is why it is generated per run rather than written as a literal.
 */
const ADMIN_KEY = `e2e-${'k'.repeat(8)}-${Date.now()}`;

/**
 * The stub's compose service name. NOT a *.invalid address, and that is deliberate on two
 * counts: SourceRepository.ValidateBaseUrl REJECTS any .invalid host outright (arb-c29), so
 * a placeholder here would fail source creation with a 400; and NzbHydraSource's SSRF guard
 * drops every item whose <link> origin differs from this value, so it must match the stub's
 * STUB_PUBLIC_ORIGIN in compose.yml exactly. A mismatch yields zero rows and no error.
 *
 * It is a container-internal service name, so it is neither a real host nor a secret.
 */
const STUB_BASE_URL = 'http://stub-upstream:5100';

/**
 * The SAME container under a second compose network alias, serving byte-identical fixtures.
 * A source pointed here is genuinely reachable, but the stub stamps every <link> with
 * STUB_PUBLIC_ORIGIN (the name above), so every item it returns has a foreign origin from
 * this source's point of view and the SSRF guard must drop all of them.
 *
 * Reachability is the whole point: an unreachable host would also yield zero rows, and the
 * two are indistinguishable in the result. Only a reachable mismatch shows the guard doing
 * the dropping rather than the network.
 */
const STUB_MISMATCHED_BASE_URL = 'http://stub-upstream-alias:5100';

test('the container answers /health', async ({ request }) => {
  const response = await request.get('/health');
  expect(response.ok()).toBeTruthy();
});

test('the UI loads and renders its shell', async ({ page }) => {
  await page.goto('/');
  // A heading, not just a 200: index.html is served for every unmatched path, so a status
  // check alone passes even when the bundle fails to boot and leaves an empty root div.
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
});

test('the admin key can be bootstrapped over the local-network bypass', async ({ request }) => {
  // ADR 0004: while no key is configured, admin-mutating routes are permitted from
  // loopback and RFC 1918 addresses, decided ONLY from the socket's peer address. compose
  // publishes the port on 127.0.0.1, so this request lands inside that window. Once this
  // succeeds the bypass closes and every later admin call must carry the header.
  const response = await request.put('/api/admin/security/admin-key', {
    data: { value: ADMIN_KEY },
  });
  expect(response.status(), await response.text()).toBe(204);

  // The gate is now absolute: the same route without the header must no longer be open.
  // Without this, "the key was set" is asserted only by a 204 that a permanently-open
  // bypass would also return.
  const unauthenticated = await request.get('/api/admin/sources');
  expect(unauthenticated.status()).not.toBe(200);
});

test('a source pointing at the stub upstream can be added and tested', async ({ request }) => {
  const created = await createSource(request, 'Stub Upstream', STUB_BASE_URL);
  expect(created.status(), await created.text()).toBe(201);

  const source = await created.json();
  // The response must never carry the key back -- the write-only contract (CLAUDE.md §1).
  expect(source).not.toHaveProperty('apiKey');
  expect(source.hasApiKey).toBe(true);
});

test('a search returns the stub upstream rows', async ({ request }) => {
  const response = await request.get('/api/admin/search', {
    headers: { [ADMIN_KEY_HEADER]: ADMIN_KEY },
    params: { q: 'example' },
  });
  expect(response.status(), await response.text()).toBe(200);

  const body = await response.json();
  const titles: string[] = body.releases.map((r: { title: string }) => r.title);

  // Both fixtures survive. Asserting the COUNT and the titles, not merely "not empty":
  // NzbHydraSource silently DROPS any item whose <link> origin differs from the source's
  // base URL, so a fixture/compose origin mismatch shows up here as a short list rather
  // than as an error. A bare "length > 0" would pass with one of the two items lost.
  expect(titles).toHaveLength(2);
  expect(titles).toContain('Example Show S01E01 1080p STUBGROUP');
  expect(titles).toContain('Example Show S01E02 1080p STUBGROUP');
});

/**
 * The origin-pinning guard, proved as a PAIR.
 *
 * Placed AFTER "a search returns the stub upstream rows" on purpose: in serial mode order
 * follows position in the file, and this test adds a second source, which permanently
 * changes what a plain unfiltered search returns. Moving it earlier would break that
 * earlier test's exact count of 2 -- so it must stay below it, and any test added later
 * must filter by sourceName rather than assume a total.
 *
 * This is the E2E's own positive control. "A mismatched origin yields no rows" is an
 * absence assertion, and on its own it passes for all the wrong reasons -- an unreachable
 * host, a stub that died, a search that never ran. Pairing it with the matching-origin case
 * against the SAME container serving the SAME fixtures leaves exactly one difference
 * between the two: the origin the source was configured with.
 *
 * This is not hypothetical. Running the real NzbHydraSource parser against this stub with
 * the origins mismatched returned zero rows from perfectly well-formed fixtures, silently,
 * with no error and nothing in the logs to point at the cause.
 */
test('items whose link origin differs from the source base URL are dropped, and only those', async ({
  request,
}) => {
  const created = await createSource(request, 'Stub Upstream Alias', STUB_MISMATCHED_BASE_URL);
  expect(created.status(), await created.text()).toBe(201);

  const response = await request.get('/api/admin/search', {
    headers: { [ADMIN_KEY_HEADER]: ADMIN_KEY },
    params: { q: 'example' },
  });
  expect(response.status(), await response.text()).toBe(200);

  const releases: { title: string; sourceName: string }[] = (await response.json()).releases;

  // The matching-origin source still returns both items: the stub is alive, the fixtures
  // parse, and the search ran. Without this half, the emptiness below proves nothing.
  const matched = releases.filter((r) => r.sourceName === 'Stub Upstream');
  expect(matched).toHaveLength(2);

  // The mismatched source contributes nothing, from the very same container and fixtures.
  const mismatched = releases.filter((r) => r.sourceName === 'Stub Upstream Alias');
  expect(mismatched).toHaveLength(0);
});

/**
 * CLAUDE.md §1: the admin key is session-only in Zustand -- never localStorage, never
 * sessionStorage, never a query string. Today only a source grep guards that; this is the
 * first assertion that the RUNNING application behaves that way.
 *
 * Note what this test does NOT do, and why. There is no UI that accepts the admin key:
 * `adminKeyStore.setKey` has no caller in any surface, and /login and /setup take an
 * operator username and password (the session-cookie path, ADR 0008) rather than the key.
 * An earlier draft of this test typed the key into a login form; that form does not exist,
 * and a test driving it would have been asserting against a screen of its own invention.
 *
 * So this walks the real surfaces a browser actually uses and asserts that NO credential
 * -- neither this run's admin key by value, nor anything credential-shaped by name --
 * reaches either web storage. That is the regression the invariant exists to prevent:
 * adding `persist` middleware to the Zustand store would serialise it, key included, the
 * moment one was set. The name-shaped half is what keeps the assertion meaningful even
 * though a browser session normally leaves `adminKeyStore.key` null.
 */
test('the admin key never reaches browser storage', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

  // POSITIVE CONTROL FIRST. An absence assertion passes just as happily against storage
  // that is empty for an unrelated reason -- a page that never booted, a reader that
  // silently returned nothing, a value never set at all. So prove the reader would SEE a
  // planted value in both stores before trusting its absence to mean anything. Without
  // this the whole test is vacuous (CLAUDE.md §4).
  await page.evaluate(() => {
    window.localStorage.setItem('e2e-positive-control', 'CANARY-VALUE');
    window.sessionStorage.setItem('e2e-positive-control', 'CANARY-VALUE');
  });
  const withCanary = await readBrowserStorage(page);
  expect(withCanary.local).toContain('CANARY-VALUE');
  expect(withCanary.session).toContain('CANARY-VALUE');

  await page.evaluate(() => {
    window.localStorage.removeItem('e2e-positive-control');
    window.sessionStorage.removeItem('e2e-positive-control');
  });

  // Drive real surfaces, including an admin-gated one, so anything the app persists on a
  // normal operator journey has been written by the time storage is read.
  for (const path of ['/', '/search', '/settings', '/system']) {
    await page.goto(path);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  }

  const storage = await readBrowserStorage(page);

  // The key this run configured must not appear anywhere in either store.
  expect(storage.local).not.toContain(ADMIN_KEY);
  expect(storage.session).not.toContain(ADMIN_KEY);

  // Nor may the store keep it under some other name. `adminKeyStore` holds the key in
  // memory with deliberately NO `persist` middleware; adding that middleware is the
  // regression this guards, and it would serialise the store -- key included -- under a
  // zustand-shaped entry. Assert no storage entry mentions the store or a key-ish name at
  // all, so the check does not depend on the exact value surviving verbatim.
  const suspicious = /admin|apikey|api_key|zustand|token|secret/i;
  for (const [store, dump] of Object.entries(storage)) {
    const keys = Object.keys(JSON.parse(dump) as Record<string, string>);
    const offenders = keys.filter((k) => suspicious.test(k));
    expect(offenders, `${store} storage held credential-shaped keys: ${offenders.join(', ')}`)
      .toHaveLength(0);
  }

  // And it must not have ridden along in the URL either.
  expect(page.url()).not.toContain(ADMIN_KEY);
});

/**
 * Creates a source. The API key is generated per run and never written as a literal: the
 * pre-commit secrets guard rejects a credential-shaped literal in a diff, and it is right
 * to -- a committed one reads as a real key to every later reader regardless of what a
 * comment beside it claims. The stub ignores the value entirely (it authenticates nobody);
 * one is sent only because sending it exercises the write-only storage path.
 */
function createSource(request: APIRequestContext, displayName: string, baseUrl: string) {
  return request.post('/api/admin/sources', {
    headers: { [ADMIN_KEY_HEADER]: ADMIN_KEY },
    data: {
      kind: 'nzbhydra',
      displayName,
      baseUrl,
      apiKey: `stub-${Date.now()}-${Math.random().toString(36).slice(2)}`,
      enabled: true,
    },
  });
}

/** Serialises both web storages, so a key stored under ANY name is caught, not just a known one. */
async function readBrowserStorage(page: Page): Promise<{ local: string; session: string }> {
  return page.evaluate(() => ({
    local: JSON.stringify(window.localStorage),
    session: JSON.stringify(window.sessionStorage),
  }));
}
