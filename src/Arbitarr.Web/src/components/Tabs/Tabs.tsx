import { useId, useState } from 'react';
import type { KeyboardEvent } from 'react';

import styles from './Tabs.module.css';

/** One tab: a stable id and its visible label. */
export interface TabItem<TabId extends string> {
  id: TabId;
  label: string;
}

interface UseTabsResult<TabId extends string> {
  /** The currently selected tab id. */
  activeId: TabId;
  /** Selects a tab directly (used by the tablist's click handler). */
  setActiveId: (id: TabId) => void;
  /** The `id` attribute for a tab's `<button role="tab">`. Pass to `getTabPanelProps` too. */
  getTabId: (id: TabId) => string;
  /** The `id` attribute for a tab's associated `role="tabpanel"`. */
  getPanelId: (id: TabId) => string;
}

/**
 * Owns tab selection state and the DOM ids that associate each tab button with
 * its panel, so a caller never hand-rolls `useId` composition itself. Extracted
 * from System.tsx (#65) where this bookkeeping lived inline; Library (arb-6l9b.6)
 * is the second caller this generalises for, so the ids are namespaced per-hook
 * rather than assuming a page has only one tablist.
 */
export function useTabs<TabId extends string>(initialId: TabId): UseTabsResult<TabId> {
  const [activeId, setActiveId] = useState<TabId>(initialId);
  const base = useId();

  return {
    activeId,
    setActiveId,
    getTabId: (id) => `${base}-tab-${id}`,
    getPanelId: (id) => `${base}-panel-${id}`,
  };
}

interface TabsProps<TabId extends string> {
  /** The tablist's accessible name (WAI-ARIA tabs pattern requires one). */
  label: string;
  /** Tabs in display and navigation order. */
  items: readonly TabItem<TabId>[];
  /** State from `useTabs`, so the tablist and its panel share one id scheme. */
  tabs: UseTabsResult<TabId>;
}

/**
 * A WAI-ARIA tablist: roving tabindex, arrow-key navigation with wraparound at
 * both ends, and `aria-controls`/`aria-selected` wiring to the panel `useTabs`
 * names. Extracted verbatim from System.tsx's inline implementation (#65) —
 * same markup, same keyboard behaviour, no behaviour change at the extraction
 * site.
 *
 * Renders ONLY the tablist. The panel (including which tab's content shows) is
 * the caller's: some pages unmount inactive panels to stop their queries
 * refetching behind a hidden tab (System's StatusTab), which only the caller
 * can decide. Pair with `TabPanel` for the matching `role="tabpanel"` wrapper.
 */
export function Tabs<TabId extends string>({ label, items, tabs }: TabsProps<TabId>) {
  const { activeId, setActiveId, getTabId } = tabs;

  /**
   * Arrow-key roving focus (WAI-ARIA tabs pattern), wrapping at both ends.
   *
   * Without this the tablist is reachable but not operable by keyboard the way
   * its appearance promises: Tab alone would step through every tab as a
   * separate stop, which is precisely what `tabIndex={-1}` on the inactive tabs
   * prevents.
   */
  const onTabKeyDown = (event: KeyboardEvent<HTMLButtonElement>) => {
    const delta = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
    if (delta === 0) {
      return;
    }

    event.preventDefault();
    const index = items.findIndex((item) => item.id === activeId);
    const next = items[(index + delta + items.length) % items.length].id;
    setActiveId(next);
    document.getElementById(getTabId(next))?.focus();
  };

  return (
    <div className={styles.tabs} role="tablist" aria-label={label}>
      {items.map(({ id, label: itemLabel }) => (
        <button
          key={id}
          type="button"
          id={getTabId(id)}
          role="tab"
          aria-selected={activeId === id}
          aria-controls={tabs.getPanelId(id)}
          // Only the active tab is a tab stop; the arrow keys move between them.
          tabIndex={activeId === id ? 0 : -1}
          className={activeId === id ? `${styles.tab} ${styles.tabActive}` : styles.tab}
          onClick={() => setActiveId(id)}
          onKeyDown={onTabKeyDown}
        >
          {itemLabel}
        </button>
      ))}
    </div>
  );
}

interface TabPanelProps<TabId extends string> {
  /** The panel's own tab id — used to derive its `id` and `aria-labelledby`. */
  id: TabId;
  tabs: UseTabsResult<TabId>;
  children: React.ReactNode;
}

/**
 * The `role="tabpanel"` wrapper for one tab's content, associated with its tab
 * via `aria-labelledby`. `tabIndex={-1}` makes the panel itself focusable
 * (without a focusable descendant) so keyboard users can move into its content
 * in one step after selecting a tab, matching System's pre-extraction markup.
 */
export function TabPanel<TabId extends string>({ id, tabs, children }: TabPanelProps<TabId>) {
  return (
    <div id={tabs.getPanelId(id)} role="tabpanel" aria-labelledby={tabs.getTabId(id)} tabIndex={-1}>
      {children}
    </div>
  );
}
