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

This means those three *shell* constants — the ones the chrome assertions read back — and not every
length. A component's own padding, gap and radius stay as literals in its own `*.module.css`
(`PageHeader.module.css`, `PageToolbar.module.css`), because there is no spacing scale in
`theme.css` and a token nothing asserts is a pinned constant with no mechanism behind it. Only
colour is forbidden everywhere outside `theme.css`.

**A page-local breakpoint beside the shell's is fine when the reason is stated inline.** The shell
itself reflows at one breakpoint (`AppShell.module.css`); a component is free to pick its own
different number when its own content, not the chrome, is what drives the reflow — SectionNav's
wide-layout query is the precedent, and its own stylesheet states why that number is not the
shell's.

**Bare element selectors appear only in `theme.css`'s reset.** Component styles are scoped through
CSS modules.

---

## Status badges

**Status badges are filled, not outlined.** A badge carries a low-opacity fill of its semantic
colour behind a border and text in that same colour at full strength — the neutral `.badge` uses a
muted fill, and the ok / warn / danger variants each use their own. A filled pill reads as a status
at a glance in a dense table, where an outline of the same colour does not.

The fills are tokens (`--badge-fill`, `--ok-fill`, `--warn-fill`, `--danger-fill`) for the reason
stated under *Rules for component styles*: they need alpha, and needing alpha is not a licence to
inline a literal.

**The alpha is load-bearing.** On a dark theme a translucent fill *lightens* the ground, so raising
the alpha costs contrast. At 0.12 the muted, ok and warn text colours all clear WCAG AA (4.5:1)
composited over `--bg-panel`; at 0.16 muted and ok fall under it. AC-CHROME-5 measures this, so
raising the alpha fails CI rather than quietly degrading legibility.

`--danger` is a documented exception held to 3:1, the large/bold-text floor. It is already 3.68:1
on `--bg-panel` *unfilled*, so it misses AA body text independently of any fill; lightening it is
tracked as `arb-4uk`.

---

## Page toolbar

The control strip for a surface: filters, view options, page-level actions.

**One toolbar per surface, directly under `PageHeader`, as its sibling.** Not inside PageHeader's
`actions` slot — that slot renders inline with the `<h1>`, and these controls belong on their own
row beneath the title, the way the *arr shell arranges them.

**A toolbar never contains a heading**, at any level. `PageHeader` owns the single `<h1>` per route
(AC2b); a caption here is the most convenient way to break that rule, so `PageToolbar.test.tsx`
asserts the component contributes none.

**Left section is context and actions, right section is menus** (`PageToolbarSection align="end"`).

**Menus are disclosure buttons, not native `<select>`s.** `PageToolbarMenu` opens a `role="menu"`
panel; items are `PageToolbarMenuItem` with `role="menuitemradio"` for a single-select group and
`role="menuitemcheckbox"` for an independent toggle, `aria-checked` tracking the active one.

**A radio menu's trigger label states the active selection** — `Kind: Decisions`, not `Kind`. A
`<select>` shows its current value without being opened; a button that reads only `Kind` loses that,
and the operator can no longer see what the list in front of them is filtered by. A menu of
independent checkboxes is the exception: each item carries its own `aria-checked`, so its trigger is
a plain noun (`View`) that stays stable as options are added to it.

**Do not add a control the API cannot serve.** Activity has no sort menu because the server returns
most-recent-first and exposes no sort parameter.

**Density belongs in the view menu.** It holds display preferences — how the page is drawn, never
what it queries — as `role="menuitemcheckbox"` items rather than a radio group, because each is an
independent on/off and they do not compete for one slot. `Compact rows` is the first: it drops the
vertical padding of every `.table` and changes nothing else (row height only — not font size, not
borders, not the zebra). It is applied shell-wide from ONE rule in `surface.module.css`, keyed off a
`data-density` attribute that `AppShell` writes on the content wrapper, so all ten table surfaces
follow it without a per-surface opt-in. The choice lives in an in-memory Zustand store for the
lifetime of the tab (`state/tableDensityStore.ts`) — it is a viewer preference, never a server
setting, and deliberately has no `persist` middleware, matching `adminKeyStore` and the CI guard
that greps for `localStorage`/`sessionStorage`.

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
