import { execFile } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { join } from 'node:path';
import { promisify } from 'node:util';

import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

const execFileAsync = promisify(execFile);

/**
 * Absolute, so the restart works regardless of the runner's working directory.
 *
 * `__dirname`, not `import.meta.url`: this package is CommonJS (no "type": "module"), and
 * Playwright transpiles the spec accordingly, so `import.meta` is a syntax error at collection
 * time -- "Cannot use 'import.meta' outside a module", which reports as "No tests found"
 * rather than as a compile failure. tsc alone does not catch it.
 */
const COMPOSE_FILE = join(__dirname, '..', 'compose.yml');

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
const ADMIN_KEY = `e2e-${randomUUID()}`;

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

/**
 * Query-text PREFIX for the origin-mismatch test. Each half appends a fresh id: the two halves
 * must NOT share a query text, and an earlier revision of this file got that exactly backwards.
 *
 * PaginationSnapshotService caches a materialised result set for 300s keyed on
 * searchType/type/protocol/q/categories/ids -- with no source identity in the key
 * (ComputeSnapshotToken, PaginationSnapshotService.cs:206) -- and QuerySnapshotStore persists it
 * in the database, so it survives the container restart the repoint requires. Reusing one q
 * across the repoint therefore replays the pre-repoint snapshot: the guard never runs, the
 * upstream is never called, and the test reports the cache's two rows. That is not hypothetical
 * -- it is what CI returned (expected 0, received 2) when both halves shared a single q.
 *
 * A unique suffix per call is what forces a real materialisation each time. The prefix is kept
 * only so both calls stay greppable as this test's, and it is unique to this test so no OTHER
 * test's snapshot can be replayed into it either.
 *
 * The stub answers every search with the same fixtures regardless of q, so this changes what is
 * cached, not what comes back.
 */
const MISMATCH_QUERY_PREFIX = 'origin-guard-probe';

test('the container answers /health', async ({ request }) => {
  const response = await request.get('/health');
  expect(response.ok()).toBeTruthy();
});

test('the UI loads and renders its shell', async ({ page }) => {
  await page.goto('/');
  // A NAMED heading, not just a 200 and not merely "some h1": index.html is served for every
  // unmatched path, so a status check alone passes even when the bundle fails to boot and
  // leaves an empty root div. On this fresh install "/" is inside <RequireSession> with no
  // operator account yet, so the guard lands on the setup screen -- naming it is what makes
  // this prove React mounted and routed, rather than that some element happened to exist.
  await expect(page.getByRole('heading', { level: 1, name: 'Set up Arbitarr' })).toBeVisible();
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

/**
 * A source added over the API is NOT in force until the process restarts, and that is by
 * design rather than a defect. `ResolvedSourceConfiguration` is a mutable-once singleton that
 * `SourceSeeder` fills exactly once at startup -- its own doc comment says "resolved once at
 * startup" -- because the `IUpstreamSource` factory is registered before `app.Build()` while
 * the database is not safely readable until after migration.
 *
 * With no source in force the adapter is still constructed, pointed at RFC 5737 TEST-NET-1
 * (http://192.0.2.1:1), so a search fails fast and degrades to an empty result set.
 *
 * That is exactly what the first CI run of this suite hit: a search that took 10.1 seconds --
 * the upstream timeout -- and returned zero rows, which is INDISTINGUISHABLE in the response
 * from the origin-pinning drop this file warns about elsewhere. Two unrelated causes, one
 * identical symptom. That is why the restart is an explicit, asserted step rather than a
 * sleep: a future zero-row failure should not send the next reader hunting the wrong one.
 *
 * The golden path therefore restarts the app between "add a source" and "search", mirroring
 * what an operator actually does. It does not weaken the product to suit the test.
 */
test('the app is restarted so the newly added source comes into force', async ({
  request,
  baseURL,
}) => {
  await restartAppContainer(baseURL!);

  // Prove the restart really happened and the source survived it, rather than trusting a
  // sleep: the source is still listed once the process is back up.
  const sources = await request.get('/api/admin/sources', {
    headers: { [ADMIN_KEY_HEADER]: ADMIN_KEY },
  });
  expect(sources.status(), await sources.text()).toBe(200);
  const names: string[] = (await sources.json()).map((s: { displayName: string }) => s.displayName);
  expect(names).toContain('Stub Upstream');
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

  // Walk only what an UNAUTHENTICATED visitor can actually render, and assert the specific
  // heading each one is expected to show.
  //
  // The earlier version walked /, /search, /settings and /system and asserted merely that
  // "some h1 is visible". Every one of those sits inside <RequireSession> in routes.tsx, so on
  // this fresh install -- no operator account yet -- the guard redirects them all to /setup.
  // The walk was therefore rendering the same setup screen four times and passing on its
  // heading, which is exactly the vacuity this file is careful about elsewhere.
  //
  // /setup is the honest surface here: it is outside the guard, it is where a fresh install
  // legitimately lands, and it is the screen that would carry a credential if any screen did.
  await page.goto('/setup');
  await expect(page.getByRole('heading', { level: 1, name: 'Set up Arbitarr' })).toBeVisible();

  // And confirm the guard's redirect is real rather than assumed, so the note above cannot
  // quietly go stale if RequireSession changes.
  await page.goto('/system');
  await expect(page.getByRole('heading', { level: 1, name: /Set up Arbitarr|Sign in to Arbitarr/ }))
    .toBeVisible();

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
 * The origin-pinning guard, proved as a PAIR against the same container and fixtures.
 *
 * Runs LAST, and must stay last: it repoints the one source in force at a mismatched origin
 * and leaves it that way. In serial mode order follows position in the file, so any test
 * added below this one would run against a deliberately broken source and fail for a reason
 * that has nothing to do with what it is testing.
 *
 * Why it edits the existing source instead of adding a second one: `SourceSeeder` takes only
 * the FIRST source by id (`OrderBy(s => s.Id).FirstOrDefaultAsync`), so exactly one source is
 * ever in force. A second source would simply never be queried, and the "mismatched source
 * returned nothing" assertion would pass without the guard having run at all -- the precise
 * shape of vacuity this test exists to avoid.
 *
 * The alias is the same container under a second network name, so it is genuinely reachable
 * and serves byte-identical fixtures; only the origin the source was configured with differs.
 * That matters, because an unreachable host also yields zero rows and the two are
 * indistinguishable in the response -- as this suite already learned the hard way: its first
 * CI run returned zero rows because no source was in force yet, which looked exactly like the
 * guard firing.
 *
 * Not hypothetical either: running the real NzbHydraSource parser against this stub with the
 * origins mismatched returned zero rows from perfectly well-formed fixtures, silently.
 */
test('items whose link origin differs from the source base URL are dropped', async ({
  request,
  baseURL,
}) => {
  const sources = await request.get('/api/admin/sources', {
    headers: { [ADMIN_KEY_HEADER]: ADMIN_KEY },
  });
  const source = (await sources.json()).find(
    (s: { displayName: string }) => s.displayName === 'Stub Upstream',
  );
  expect(source, 'the golden-path source must exist before it can be repointed').toBeTruthy();

  // POSITIVE CONTROL, under a query text no search has used before -- see
  // MISMATCH_QUERY_PREFIX for why the two halves must never share one.
  const matchedQuery = `${MISMATCH_QUERY_PREFIX}-matched-${randomUUID()}`;
  const beforeRepoint = await request.get('/api/admin/search', {
    headers: { [ADMIN_KEY_HEADER]: ADMIN_KEY },
    params: { q: matchedQuery },
  });
  expect(beforeRepoint.status(), await beforeRepoint.text()).toBe(200);
  const before: { title: string }[] = (await beforeRepoint.json()).releases;
  expect(before, 'the matching origin must return rows, or the emptiness below proves nothing')
    .toHaveLength(2);

  const repointed = await request.put(`/api/admin/sources/${source.id}`, {
    headers: { [ADMIN_KEY_HEADER]: ADMIN_KEY },
    data: {
      // Same ordinal match as createSource -- see the comment there (arb-pn5).
      kind: 'NzbHydra',
      displayName: 'Stub Upstream',
      baseUrl: STUB_MISMATCHED_BASE_URL,
      enabled: true,
    },
  });
  expect(repointed.status(), await repointed.text()).toBe(200);

  // The source configuration is resolved once per process, so the repoint only takes effect
  // after a restart -- exactly as the original add did.
  await restartAppContainer(baseURL!);

  const mismatchedQuery = `${MISMATCH_QUERY_PREFIX}-mismatched-${randomUUID()}`;
  const response = await request.get('/api/admin/search', {
    headers: { [ADMIN_KEY_HEADER]: ADMIN_KEY },
    params: { q: mismatchedQuery },
  });
  expect(response.status(), await response.text()).toBe(200);

  // Reachable, identical fixtures, foreign origin: every item is dropped by the guard. Two
  // things differ from the two-row control above -- the source's base URL, which is the
  // variable under test, and the query text, which must differ to defeat the snapshot cache
  // (see MISMATCH_QUERY_PREFIX). The stub answers every q with the same fixtures, so that
  // second difference changes what is cached, not what comes back. Same stub, same
  // container, same fixtures.
  const releases: { title: string }[] = (await response.json()).releases;
  expect(releases).toHaveLength(0);
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
      // MUST be exactly "NzbHydra" -- SourceRepository.NzbHydraKind, the value
      // SourceSeeder resolves against ORDINALLY (`Where(s => s.Kind == NzbHydraKind &&
      // s.Enabled)`). Do NOT lowercase this: since arb-pn5 (#142) the endpoint rejects any
      // other casing with a 400, so a wrong spelling now fails this suite at createSource
      // with a named error rather than silently.
      //
      // The exact spelling is still load-bearing, for a better reason than before. arb-pn5
      // closed the silent half: the endpoint used to store ANY casing and return 201 while
      // the seeder never selected the row, leaving the adapter pinned to the unconfigured
      // placeholder and every search returning zero rows with no error anywhere -- which
      // cost this suite a full CI cycle. What it did not do is make the casings
      // interchangeable: it rejects rather than normalises, deliberately (CLAUDE.md §3),
      // because only one spelling is ever resolved.
      kind: 'NzbHydra',
      displayName,
      baseUrl,
      apiKey: `stub-${randomUUID()}`,
      enabled: true,
    },
  });
}

/**
 * Restarts the app container and waits for it to serve /health again.
 *
 * Uses `docker compose restart` rather than an in-product reload because no such reload
 * exists: the source configuration is resolved once per process by design (see the restart
 * test's comment). The compose project is addressed by file, so this works regardless of the
 * runner's working directory.
 *
 * `baseUrl` is the caller's `baseURL` fixture, not a second read of ARBITARR_BASE_URL: the
 * default lives once, in playwright.config.ts, so a repointed run cannot have this helper
 * probing 127.0.0.1 while the tests around it drive somewhere else.
 *
 * The deadline is 25s, deliberately BELOW playwright.config.ts's 30s per-test `timeout`.
 * At the 60s it used to be, the test was killed by the harness first, so neither the
 * deadline nor its error message could ever be reached and a hung restart reported a bare
 * timeout instead of naming /health. Raising the test's timeout was the alternative and was
 * not taken: the restart is one step of a serial path, and a 60s budget for it would hide a
 * genuinely slow boot rather than fail on it.
 */
async function restartAppContainer(baseUrl: string): Promise<void> {
  await execFileAsync('docker', ['compose', '-f', COMPOSE_FILE, 'restart', 'arbitarr']);

  const deadline = Date.now() + 25_000;

  for (;;) {
    try {
      const response = await fetch(`${baseUrl}/health`);
      if (response.ok) {
        return;
      }
    } catch {
      // Connection refused while the process is still coming up: keep waiting rather than
      // failing, since that is the expected state for the first second or so.
    }

    if (Date.now() > deadline) {
      throw new Error('The app container did not answer /health within 25s of a restart.');
    }

    await new Promise((resolve) => setTimeout(resolve, 500));
  }
}

/** Serialises both web storages, so a key stored under ANY name is caught, not just a known one. */
async function readBrowserStorage(page: Page): Promise<{ local: string; session: string }> {
  return page.evaluate(() => ({
    local: JSON.stringify(window.localStorage),
    session: JSON.stringify(window.sessionStorage),
  }));
}
