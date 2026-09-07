# Design system

UI patterns and component contracts for the Arbitarr admin UI.

## The palette is not here

**`src/Arbitarr.Web/src/styles/theme.css` is normative and is the single source of truth for every
colour and layout constant.** This directory deliberately does not restate any token value.

*Why:* the "*arr chrome" requirement was lost once already, because a gate existed that did not
scan the file holding the palette — `--bg-app: #ffffff` passed everything. A second copy of the
values here would recreate exactly that failure: two places to change, one of them unchecked, and
no way to tell which is current. Read the tokens from `theme.css`; it documents each one and why it
holds that value.

To change a token, move the value in `theme.css` **and** its assertion in
`src/Arbitarr.Web/src/styles/theme.test.ts` **in one commit**. That coupling is the whole
mechanism.

---

## Design intent

The UI matches the *arr stack shell (Sonarr / Radarr / Prowlarr / Lidarr), which are visually the
same family — matching one matches all. The visual reference is Sonarr v4's Series → Library index
at desktop width.

**Dark theme only.** There is no toggle, no `prefers-color-scheme: light` block, and no
`[data-theme]` switch. A CI guard rejects all three, because any of them would let the shell render
light without another gate noticing.

The accent is a generic desaturated blue — deliberately *not* Sonarr's brand blue and not Radarr's
yellow — per the neutral-accent constraint. Arbitarr sits alongside those tools; it does not
impersonate one.

---

## Rules for component styles

**Colour literals are forbidden outside `theme.css`.** CI greps `*.module.css` for hex, `rgb()`,
and `hsl()` literals. Reference `var(--token)`.

This includes values that "need" alpha: `--overlay-backdrop` is a pinned token for exactly that
reason. Needing transparency is not a reason to inline a literal.

**Layout constants are tokens too.** `--sidebar-width`, `--topbar-height`, `--content-max-width`
are pinned so the chrome assertions can test a number rather than a feeling. A hard-coded length in
the shell stylesheet is a CI failure.

**Bare element selectors appear only in `theme.css`'s reset.** Component styles are scoped through
CSS modules.

---

## State and storage

**The admin key lives in a session-only Zustand store.** Never `localStorage`, never
`sessionStorage`, never a query string. CI rejects references to browser storage in the web
project.

**Admin access is gated by path prefix** (`/api/admin/`), never by HTTP verb — `apiFetch` attaches
the key on that basis. Verb-based gating silently breaks the GET-only Search and Suppressions
surfaces.

---

## Testing UI appearance

`src/Arbitarr.Web/src/styles/theme.test.ts` (AC-CHROME) reads the tokens back **resolved** out of
jsdom and asserts both the exact literals and the WCAG contrast ratios, including an explicit "the
app ground is dark" assertion that a white theme cannot satisfy.

**`test.css: true` in `vite.config.ts` and the `theme.css` import in `vitest.setup.ts` are both
load-bearing.** Vitest defaults to `css: false`, under which the import resolves to an empty stub,
no stylesheet attaches to the jsdom document, and every read returns `''`. `readToken()` throws a
named error in that case, so a broken harness cannot be mistaken for a wrong colour — and so nobody
is tempted to "fix" it by weakening the assertion.

---

## Adding to this directory

This surface holds **patterns and contracts** — how a table behaves, what a status pill means, how
an empty state reads — as they are settled. It does not hold token values, and it does not restate
what `src/Arbitarr.Web/README.md` already covers about building and testing the frontend.

A pattern is worth writing down here once it has been decided twice.
