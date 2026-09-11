import { useEffect, useState } from 'react';

import styles from './SectionNav.module.css';

/**
 * One entry in the section nav. `id` is the anchor target and must match the
 * `id` on the corresponding <section> exactly -- Settings.test.tsx asserts that
 * correspondence per link via document.getElementById, so a drifting pair fails
 * rather than silently producing a dead anchor.
 */
export interface SectionNavEntry {
  id: string;
  label: string;
}

/**
 * Slug for a server-owned catalog group name ("Caching" -> "caching").
 *
 * The group names are the server's, not ours (arb-5oe forbids renaming them),
 * so this derives an anchor id from whatever arrives rather than mapping known
 * names to hand-written ids -- a map would silently omit any group added to the
 * catalog later, which is exactly the automatic property this nav is for.
 *
 * Non-alphanumerics collapse to a single hyphen and the ends are trimmed, so
 * "Search & indexing" and "Search  indexing" both give "search-indexing" rather
 * than an id with a leading, trailing or doubled hyphen.
 */
export function slugifyGroup(group: string): string {
  return group
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

interface SectionNavProps {
  entries: readonly SectionNavEntry[];
}

/**
 * Sticky in-page section nav for the single-page Settings (arb-5oe, epic
 * decision 3: ONE page with an anchor list, deliberately not tabs and not
 * sub-routes -- Sonarr v4's own Settings is a single list page).
 *
 * Settings-specific until a second surface needs it. It lives under
 * surfaces/Settings/ rather than components/shell/ for that reason; promote it
 * only when there is a real second caller, not in anticipation of one.
 *
 * It renders NO heading at any level. PageHeader owns the single <h1> per route
 * (AC2b, asserted by Settings.test.tsx), and a caption here is the easiest way
 * to break that rule. The <nav>'s aria-label is what names it instead.
 */
export function SectionNav({ entries }: SectionNavProps) {
  const [activeId, setActiveId] = useState<string | null>(null);

  useEffect(() => {
    // jsdom implements neither IntersectionObserver nor media queries, so this
    // whole highlight path is inert under test and the nav simply renders with
    // no active entry. Guarded rather than assumed: without the typeof check
    // the constructor call throws in jsdom and takes the entire surface down
    // with it, so every Settings test would fail on an unrelated feature.
    if (typeof IntersectionObserver === 'undefined') {
      return undefined;
    }

    const targets = entries
      .map((entry) => document.getElementById(entry.id))
      .filter((element): element is HTMLElement => element !== null);

    if (targets.length === 0) {
      return undefined;
    }

    const observer = new IntersectionObserver(
      (observed) => {
        // The topmost intersecting section wins. Several are on screen at once
        // on a tall viewport, and taking the last callback entry instead would
        // make the highlight jump to whichever crossed the threshold most
        // recently -- which, when scrolling up, is the one furthest DOWN the
        // page. Compared by document order via the entries array, not by
        // boundingClientRect, so the result does not depend on scroll direction.
        const visible = observed
          .filter((observation) => observation.isIntersecting)
          .map((observation) => observation.target.id);

        if (visible.length === 0) {
          return;
        }

        const topmost = entries.find((entry) => visible.includes(entry.id));
        if (topmost !== undefined) {
          setActiveId(topmost.id);
        }
      },
      {
        // The scrolling element is the content pane (<main> carries
        // `overflow-y: auto`), not the document, so it is the root. With the
        // default null root the observer measures against the viewport, and
        // because the pane is itself fully inside the viewport every section
        // would count as intersecting at once -- the highlight would then
        // never move off the first entry.
        root: document.querySelector('main'),
        // Biased to the top: the band sits just below the pane's top edge, so
        // the highlighted entry is the section whose heading the operator is
        // actually looking at rather than whichever occupies the most pixels.
        rootMargin: '0px 0px -70% 0px',
      },
    );

    for (const target of targets) {
      observer.observe(target);
    }

    return () => observer.disconnect();
  }, [entries]);

  return (
    <nav aria-label="Settings sections" className={styles.nav}>
      <ul className={styles.list}>
        {entries.map((entry) => (
          <li key={entry.id}>
            <a
              href={`#${entry.id}`}
              className={entry.id === activeId ? `${styles.link} ${styles.linkActive}` : styles.link}
              // Only on the active entry: aria-current="false" is a valid but
              // meaningless value that screen readers still expose, so the
              // attribute is absent rather than false on the others.
              aria-current={entry.id === activeId ? 'true' : undefined}
            >
              {entry.label}
            </a>
          </li>
        ))}
      </ul>
    </nav>
  );
}
