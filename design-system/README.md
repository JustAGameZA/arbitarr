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

**A hidden scrollbar needs a replacement overflow affordance.** `scrollbar-width: none` removes the
only persistent cue that a container scrolls; pair it with one — SectionNav's edge fade is the
precedent — or do not hide the bar. Decided twice: SectionNav.module.css hides the bar; PageToolbar
rejects overflow-x and wraps instead.

**An observer-derived active state needs a non-observer path for the states the observer cannot
reach.** An `IntersectionObserver` reports only what geometry produces, so any state the layout
never produces is a state the highlight can never enter — and the entry stays wrong indefinitely
rather than briefly. Pair the observer with the authoritative signal for those states. One rule,
with two instances of it in the shell: SectionNav treats a click as authoritative, because a
section below the last scrollable position is never reported as intersecting at all; SidebarNav
derives its active entry from the route rather than from what is on screen.

**Where a stylesheet decides which layout is in force, declare it in a custom property rather than
re-deriving it from measurements.** Geometry cannot reliably tell one layout from another, and two
traps make position-based tests wrong in opposite directions: `position: sticky` offsets resolve
against the scrollport's *padding* box while `getBoundingClientRect` returns its *border* box, so a
stuck element is never level with the root rect's top; and a content-sized sticky element taller
than its scrollport stops sticking altogether, scrolling away with the content and taking on
exactly the geometry a stuck one would have. SectionNav is the precedent — `--section-nav-layout`
is declared in the same rule as the sticky behaviour it describes, so the claim and the layout can
only change together, and the script reads the claim instead of inferring it from a rect.

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
the alpha costs contrast. At 0.12 the muted, ok, warn *and danger* text colours all clear WCAG AA
(4.5:1) composited over `--bg-panel`; at 0.16 muted and ok fall under it. AC-CHROME-5 measures
this, so raising the alpha fails CI rather than quietly degrading legibility.

`--danger` (`#e48481`) was lightened off the original bootstrap-lineage red (`#d9534f`, 3.68:1 on
`--bg-panel` unfilled) to clear the same 4.5:1 floor as the others — see `arb-4uk`. It is no
longer an exception.

---

## Page toolbar

The control strip for a surface: filters, view options, page-level actions.

**One toolbar per surface, directly under `PageHeader`, as its sibling.** Not inside PageHeader's
`actions` slot — that slot renders inline with the `<h1>`, and these controls belong on their own
row beneath the title, the way the *arr shell arranges them.

**A tab panel may carry its own toolbar, as the panel's first child.** `Tabs` partitions one surface
into sub-surfaces, each governing its own data set, and a tab panel has no `PageHeader` of its own —
the route's single `<h1>` belongs to the page around the tablist — so "directly under `PageHeader`,
as its sibling" is unsatisfiable inside one. The rule above is not relaxed by this: the `<h1>` per
route, the no-heading rule below, and at-most-one-toolbar-per-panel all still hold, and each toolbar
on such a route needs an `aria-label` naming its own data set (`Log filters`, not `System`) so a
screen-reader user is not given two indistinguishable toolbars. System's Logs tab is the first
caller. The alternative — leaving the Logs filter as a bordered `.panel` while every other filter
surface moved to a toolbar — was rejected because it makes the same control look like two different
kinds of thing depending on which tab it is on.

**A toolbar never contains a heading**, at any level. `PageHeader` owns the single `<h1>` per route
(AC2b); a caption here is the most convenient way to break that rule, so `PageToolbar.test.tsx`
asserts the component contributes none.

**Left section is context and actions, right section is menus** (`PageToolbarSection align="end"`).

**Menus are disclosure buttons, not native `<select>`s.** `PageToolbarMenu` opens a `role="menu"`
panel; items are `PageToolbarMenuItem` with `role="menuitemradio"` for a single-select group and
`role="menuitemcheckbox"` for an independent toggle, `aria-checked` tracking the active one.

**A free-text filter is `PageToolbarInput`, not a menu.** A menu enumerates known options; a search
box does not have any — Logs' message search and Suppressions' query key are open-ended strings. It
renders a real `<label>` beside the field rather than relying on a placeholder, because a placeholder
disappears exactly when the operator starts typing, leaving the only description of what the box
filters on gone while it is filtering. That label is also its accessible name, so surfaces query it
by `getByRole('textbox', { name })`.

**The input owns no behaviour.** It holds no timer and no submit semantics: the caller decides what a
keystroke costs. Logs debounces its value and resets to page 1; Suppressions applies on an explicit
`PageToolbarButton`, and passes `onSubmit` so Enter still applies the filter the way the `<form>` it
replaced did for free. Folding either choice into the component would impose one surface's
interaction model on the other, and the difference is visible to the operator.

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

**View-menu display preferences are shell-wide and surfaced once, not duplicated per toolbar.**
`Compact rows` lives in Activity's View menu (#200), but the effect it toggles applies to all ten
table surfaces — a second surface's toolbar must not grow its own copy of the same toggle.

---

## Tabs

`src/Arbitarr.Web/src/components/Tabs/Tabs.tsx` — a WAI-ARIA tablist primitive, extracted
(arb-9igu) from System.tsx's inline implementation (#65) with no behaviour change at that
extraction site.

**Three exports, not one component.** `useTabs<TabId>(initialId)` owns selection state and
namespaces the tab/panel id pair (via `useId`), `<Tabs>` renders the tablist, and
`<TabPanel>` renders the associated `role="tabpanel"`. They are separate because *which
panel content renders* is the caller's decision, not the primitive's — System unmounts the
inactive tab's panel entirely (its three panels are independent queries; leaving them
mounted behind a hidden tab would keep them refetching behind a table nobody is reading),
and a future caller with cheaper panels is free to keep all of them mounted and merely
hide the inactive ones. `Tabs` never sees panel content at all.

**Roving tabindex, not `tabIndex=0` on every tab.** Only the active tab is a tab stop;
inactive tabs are not tab stops — that is the point: a tablist that let every tab consume
a stop would make it slower to Tab past on a page with several tabs, which is not what a
sighted user's mental model of "tabs" promises a keyboard user.

**Generalise the tab id type, don't reach for `string`.** Each caller passes its own union
(`'status' | 'logs' | 'backup'`), so `items` and `tabs` stay in sync at the type level — a
typo'd id fails to compile rather than silently rendering an empty panel.

**Promote a tablist here only when it has a real second caller**, the same rule
`SectionNav` documents for staying local until it does. This one already has one: the
Library surface (arb-6l9b.6) was about to grow an independent second implementation of
System's tablist before this primitive existed to reuse instead. PRD recommendation 6's
"keep tabs under `surfaces/Library/`" is superseded by this shared primitive.

**Accessibility contract.** The WAI-ARIA tabs pattern, in full, because a future edit to
any one piece breaks the others silently:

- **`label` is required**, not optional, and renders as the tablist's `aria-label`. A
  tablist with no accessible name reads to a screen reader as an unlabelled group of
  buttons — there is no visible `<label>` element a tabbed UI would otherwise supply one
  from, so the prop has no fallback.
- **`aria-controls` on the tab and `aria-labelledby` on the panel round-trip to the same
  id pair**, both derived from `useTabs`'s per-instance `useId` base so two tablists on
  one page never collide. `aria-controls` names which panel a tab owns; `aria-labelledby`
  is the reverse link a screen reader uses to announce the panel by its tab's label.
- **`aria-selected`** marks exactly one tab `true` at a time; it is the semantic state a
  screen reader announces, independent of visual styling.
- **Roving tabindex**: only the active tab has `tabIndex={0}`; every inactive tab has
  `tabIndex={-1}` and so is not a tab stop — `Tab` alone moves past the whole tablist in
  one stop, the arrow keys move *within* it.
- **`tabIndex={-1}` on the panel too**, so the panel itself (not merely some focusable
  descendant) is reachable in one step after selecting a tab, without becoming an extra
  `Tab` stop of its own.
- **Keyboard**: `ArrowRight`/`ArrowLeft` move focus and selection one tab at a time,
  wrapping at both ends (past the last tab returns to the first, and vice versa);
  `Home`/`End` jump directly to the first/last tab; `Enter`/`Space` activate the focused
  tab natively, because it is a real `<button>` — `Tabs` does not intercept or
  `preventDefault` those keys, or any key besides the four it handles.

---

## Disclosure

`src/Arbitarr.Web/src/components/Disclosure/Disclosure.tsx` — a collapsible region, wrapping a
native `<details>`/`<summary>` pair (arb-h9gd). First caller: the Dashboard's *Effective
configuration*.

**This primitive is for demoting a panel, not a licence to replace every bare `<details>`.** A
native `<details>` used in-row for content that is not a panel — LogsTab's exception rows, for
example — stays fine on its own; it is not a second, competing disclosure implementation, just a
plain use of the element this component also wraps.

**Native, not hand-rolled, and that is the whole component.** `<details>` supplies the disclosure
contract correctly and for free: open/closed state, the expanded state exposed to assistive
technology, the summary-to-region association, `Enter`/`Space` activation, and removal of collapsed
content from both the layout and the accessibility tree. A `useState` + `hidden` expander has to
re-derive every one of those by hand — which is the retrofit #466 (arb-sggv) is doing for the
expanders that predate this component. Do not reimplement it, and do not add
`aria-expanded`/`aria-controls` beside the native semantics: a second, hand-maintained source of
truth drifts from the first the moment someone changes one without the other.

**A disclosure carries no heading, at any level.** `PageHeader` owns the single `<h1>` per route and
`.panelHeading` owns the `<h2>` per panel. Promoting the summary to a heading is the obvious way to
make a collapsed region "look like the panel it replaced", and it reinstates exactly the DOM weight
that collapsing it removed — `Disclosure.test.tsx` asserts the component contributes none, the same
guard `PageToolbar` carries for the same reason.

**Collapsed by default.** `defaultOpen` exists, but the only reason to reach for this primitive is
that the content is secondary to what surrounds it, and a disclosure that starts open demotes
nothing.

**Use it to demote, not to relocate.** When a panel's content is reference material rather than
signal, collapsing it in place is preferred to moving it to another surface: the query feeding it
usually cannot move with it. The Dashboard is the worked example — `SourcesTable`'s empty state
reads `nzbHydraConfigured` from the effective-config query, so relocating that panel to System would
have moved the rendering and left the fetch behind.

**Keep the caller's `QueryState` inside the disclosure**, not above it. Hoisting it puts the pending
and error branches on the always-visible summary row, which re-promotes the thing being demoted: a
failing fetch would announce itself from the landing page about rows nobody asked to see.

**The summary keeps its native marker.** `display: block` or `list-style: none` on a `<summary>`
removes the browser's disclosure triangle, which here is the only cue that the row expands — the
same rule the hidden-scrollbar note above states: remove an affordance only if you supply a
replacement.

**Testing it: do not assert visibility.** jsdom implements none of `<details>`'s hiding — no UA rule
for the closed state, so collapsed content computes `display: block`, `toBeVisible()` answers true
for it, and `getByRole` reaches controls inside a closed `<details>`. A "the content is hidden"
assertion passes identically whether or not anything collapses, so it records a guarantee nothing is
checking. Assert the `open` attribute and its toggling, which jsdom does model, and scope the
content query to the `<details>` element so an empty disclosure beside an untouched panel cannot
satisfy it.

---

## State and storage

**The admin key lives in a session-only Zustand store.** Never `localStorage`, never
`sessionStorage`, never a query string. CI rejects references to browser storage in the web
project.

**Admin access is gated by path prefix** (`/api/admin/`), never by HTTP verb — `apiFetch` attaches
the key on that basis. Verb-based gating silently breaks the GET-only Search and Suppressions
surfaces.

**Shared live status region.** One `role="status"` `aria-live="polite"` region lives in
`AppShell`, always mounted, the same reasoning `state/tableDensityStore.ts` gives for living
shell-wide rather than per-surface: a per-page region is what this replaces, and a component that
unmounts on navigation would drop whatever announcement was in flight when a route change lands.
Announcements come from two sources only — `QueryState`'s pending branch (the `false`→`true` edge
only, not every re-render while already pending) and a surface's routed `.success` message; errors
are excluded because they already announce via `role="alert"` (25+ existing call sites), and an
assertive alert queued behind a polite announcement would only delay it. A surface announces
through `state/liveStatusStore.ts`, never by adding a second live region of its own. Identical
consecutive messages (two "Saved." in a row, from the same surface saved twice or two different
surfaces) are still both announced: the store keeps a `seq` nonce alongside `message`, so a repeat
of the same text is guaranteed to force a DOM mutation rather than silently no-op on the second
one.

**`QueryState` owns the pending/error/loaded triad; a surface owns only what it loads.** Every
surface that renders one query goes through it rather than hand-rolling the three branches, so the
503 affordance and the polite pending announcement are proven once instead of once per copy. Two
consequences worth stating, because both have been got wrong:

- **Error is checked before pending**, not after. A surface that checks pending first — as
  `isPending || data === undefined` — never reaches its own error branch at all, because `data` is
  `undefined` on an error too, and every failure renders as a spinner that never resolves. That was
  a real bug on the Suppressions surface until arb-z505.
- **An empty result is a *loaded* state**, so its sentence belongs inside `children(data)`, not
  alongside the pending and error branches.

**Surface-specific error text goes through `renderError`, never through a private error branch.**
`QueryState` takes an optional `renderError?: (error: unknown) => ReactNode`, defaulted to
`errorMessage`, so omitting it is the existing behaviour for every call site. Use it where a
surface can explain a particular status better than the generic message can — Search and
Suppressions each answer a 404 with a sentence naming their own cause, and those two sentences are
not interchangeable. The override replaces the error *text* only: the `role="alert"` that makes an
error interrupt on its own stays `QueryState`'s and is not the caller's to forget.

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
