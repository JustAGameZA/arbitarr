# Arbitarr.Web

The Arbitarr web UI: React 18 + Vite + TypeScript, styled to match the *arr
stack (Sonarr/Radarr/Prowlarr/Lidarr) shell.

## Commands

Run all of these from this directory.

| Command | What it does |
| --- | --- |
| `npm install` | Install dependencies. CI uses `npm ci` against the committed lockfile. |
| `npm run dev` | Vite dev server, proxying `/api`, `/health`, `/torznab`, `/newznab` and `/download` to the Host on `127.0.0.1:8080`. |
| `npm run build` | Typecheck, then build. |
| `npm test` | Run the suite once and write `vitest-report.json` (the JSON reporter CI's floor parses). |
| `npm run test:watch` | Watch mode, human-readable output. |
| `npm run lint` | ESLint with `--max-warnings=0`. |
| `npm run typecheck` | `tsc --noEmit`. |

### Local loop

`npm run build` writes into `../Arbitarr.Host/wwwroot`, so `dotnet run` on the
Host then serves the built SPA with no extra flags. This is **developer
convenience only** — see the comment in `vite.config.ts`. The shipped image uses
the Dockerfile's `--outDir /web/dist` instead, so image content never depends on
a developer's local state.

Note that this build sets `emptyOutDir: true` and will therefore delete the
legacy static files still present in `wwwroot` on `master`. Restore them with
`git checkout -- ../Arbitarr.Host/wwwroot/` until Step 5b removes them for good.

## Styling rules

`src/styles/theme.css` is **normative** and is the only file allowed to contain
colour literals. Everything else references `var(--token)`.

The palette is pinned rather than indicative: "*arr chrome" is the reason this
work exists, and it was lost once already because every gate in place at the
time was satisfiable by a UI that looked nothing like Sonarr — the colour grep
never scanned the file holding the palette, so `--bg-app: #ffffff` passed
everything.

Two mechanisms prevent a repeat:

- **`src/styles/theme.test.ts` (AC-CHROME)** reads the tokens back *resolved*
  out of jsdom and asserts both the exact literals and the WCAG contrast
  ratios, including an explicit "the app ground is dark" assertion that a white
  theme cannot satisfy.
- **CI guards** reject colour literals in `*.module.css`, any
  `prefers-color-scheme: light` block or `[data-theme]` switch, and any
  reference to `localStorage`/`sessionStorage`.

Changing a palette value is allowed, but it must move `theme.css` and the
assertion table in **one commit**. That coupling is the whole mechanism.

### Why `css: true` matters

`vite.config.ts` sets `test.css: true` and `vitest.setup.ts` imports
`theme.css`. Both halves are required. Vitest defaults to `css: false`, under
which the import resolves to an empty stub, no stylesheet ever attaches to the
jsdom document, and every AC-CHROME read returns `''`. `readToken()` throws a
named error in that case so a broken harness cannot be mistaken for a wrong
colour — and so nobody is tempted to "fix" it by weakening the assertion.

## The admin key

It lives in a session-only Zustand store. It is never written to
`localStorage`, never to `sessionStorage`, and never placed in a query string.
Admin access is gated by **path prefix** (`/api/admin/`), never by HTTP verb —
verb-based gating silently breaks the GET-only Search and Suppressions
surfaces.

## Test-count floor

`tests/frontend-test-count-floor.txt` (repo root) holds the minimum number of
tests that must pass. CI parses `numPassedTests` from `vitest-report.json` —
*passed*, not *total*, so a skipped or `todo` test cannot hold the floor up
without running. Raise the floor in the same commit that adds tests.

The new value is the number a run actually **printed** — never the old floor
plus the number of tests you wrote. Run the suite, read `numPassedTests`, write
that down. After a rebase, re-measure: different branches legitimately measure
different totals.

This is deliberately a separate file and a separate parser from the backend's
`tests/test-count-floor.txt`, which parses `executed="N"` out of `.trx`. Two
files, two parsers, so neither suite's count can mask a collapse in the other.
