import { useEffect, useRef, useState } from 'react';

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
  const navRef = useRef<HTMLElement | null>(null);
  const [fadeLeft, setFadeLeft] = useState(false);
  const [fadeRight, setFadeRight] = useState(false);

  // Set by the click handler and read by the observer effect below. A ref, not
  // state: writing it must not trigger a re-render (that would fire the
  // observer effect's cleanup/setup for a change that is not about `entries`),
  // and the observer callback below needs to see the latest value on its next
  // invocation, not the value captured when its closure was created.
  const suppressUntilRef = useRef<{ id: string; until: number } | null>(null);

  useEffect(() => {
    const nav = navRef.current;
    if (nav === null) {
      return undefined;
    }

    // The strip's own scrollbar is hidden (scrollbar-width: none in
    // SectionNav.module.css), so this is the only signal left that it
    // scrolls. Re-measured on both scroll and resize: a window resize can
    // cross the 1100px breakpoint and turn the horizontal strip into the
    // vertical rail (overflow-x: visible there), or change how many entries
    // fit, either of which changes scrollWidth without the container ever
    // firing a scroll event on its own.
    const update = () => {
      const { scrollLeft, scrollWidth, clientWidth } = nav;
      setFadeLeft(scrollLeft > 0);
      // A 1px slack rather than an exact equality: sub-pixel layout can leave
      // scrollLeft + clientWidth a fraction short of scrollWidth even when
      // the strip is scrolled all the way to its end, which would otherwise
      // keep the right fade lit past the last entry.
      setFadeRight(scrollLeft + clientWidth < scrollWidth - 1);
    };

    update();

    nav.addEventListener('scroll', update, { passive: true });

    // ResizeObserver over a window resize listener: the vertical rail at
    // >=1100px never fires a window resize just because entries were added
    // or removed, but its own box still changes size.
    let resizeObserver: ResizeObserver | undefined;
    if (typeof ResizeObserver !== 'undefined') {
      resizeObserver = new ResizeObserver(update);
      resizeObserver.observe(nav);
    }

    return () => {
      nav.removeEventListener('scroll', update);
      resizeObserver?.disconnect();
    };
  }, [entries]);

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

    const root = document.querySelector('main');

    const observer = new IntersectionObserver(
      (observed) => {
        const intersecting = observed.filter((observation) => observation.isIntersecting);

        if (intersecting.length === 0) {
          return;
        }

        // A click just set the highlight directly; a stale callback naming a
        // different entry must not steal it back before the click's own
        // target has had a chance to be reported as intersecting (see the
        // click handler below for why "reported" and "settled" are the same
        // condition here). Once that target IS among the intersecting
        // entries, or the bounded window has elapsed, this stops applying and
        // the scroll-driven comparison below resumes as normal.
        const suppressed = suppressUntilRef.current;
        if (suppressed !== null) {
          const targetSettled = intersecting.some(
            (observation) => observation.target.id === suppressed.id,
          );
          if (targetSettled || performance.now() >= suppressed.until) {
            suppressUntilRef.current = null;
          } else {
            return;
          }
        }

        // The band's top edge in viewport coordinates. rootMargin's bottom
        // component ('-70%') shrinks the *effective* intersection rectangle by
        // that fraction of the root's height, which moves its bottom edge up
        // -- the top edge (what "the band" means here) is simply the root's
        // own top, whether root is the scrolling <main> or (its fallback) the
        // viewport.
        const bandTop = (root ?? document.documentElement).getBoundingClientRect().top;

        // Among sections whose heading has scrolled at or past the band's top
        // edge (their own top is >= bandTop, i.e. not above it), the one
        // closest to that edge -- the smallest such top -- is the section the
        // operator is actually looking at. A tall preceding section can still
        // be "intersecting" (its bottom is still inside the band) while its
        // own top sits well above bandTop; comparing tops rather than
        // document-array order is what excludes it in favour of whichever
        // section's heading has actually reached the band.
        const withinBand = intersecting.filter(
          (observation) => observation.boundingClientRect.top >= bandTop,
        );

        let winner: IntersectionObserverEntry;
        if (withinBand.length > 0) {
          winner = withinBand.reduce((closest, candidate) =>
            candidate.boundingClientRect.top < closest.boundingClientRect.top ? candidate : closest,
          );
        } else {
          // Nothing has reached the band yet (every intersecting section's
          // heading is still above it, e.g. one tall section spanning the
          // whole band): fall back to the entry nearest the band from above --
          // the largest top among those still <= bandTop.
          winner = intersecting.reduce((nearest, candidate) =>
            candidate.boundingClientRect.top > nearest.boundingClientRect.top ? candidate : nearest,
          );
        }

        setActiveId(winner.target.id);
      },
      {
        // The scrolling element is the content pane (<main> carries
        // `overflow-y: auto`), not the document, so it is the root. With the
        // default null root the observer measures against the viewport, and
        // because the pane is itself fully inside the viewport every section
        // would count as intersecting at once -- the highlight would then
        // never move off the first entry.
        root,
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
    <nav
      aria-label="Settings sections"
      className={styles.nav}
      ref={navRef}
      data-fade-left={fadeLeft ? 'true' : undefined}
      data-fade-right={fadeRight ? 'true' : undefined}
    >
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
              onClick={() => {
                // A click is authoritative (arb-81x8 bug 1): a section below
                // the last scrollable position can never intersect the band at
                // all, so the observer alone can never highlight it -- without
                // this the anchor still navigates but the previous entry stays
                // highlighted forever. The default anchor navigation is left
                // alone (no preventDefault) so `#hash` updates and the
                // scroll-into-view/keyboard behaviour from #199 are unchanged.
                setActiveId(entry.id);
                // The browser's own jump (smooth-scrolled unless the operator
                // has prefers-reduced-motion) will fire a burst of observer
                // callbacks for whatever the viewport passes on the way,
                // which would otherwise immediately overwrite the click with
                // a stale mid-scroll candidate. Suppressed until either the
                // clicked target itself is reported intersecting (the normal
                // case) or a bounded window elapses (the target-never-
                // intersects case from bug 1, where that report never comes).
                // The window is shorter under reduced motion because the jump
                // there is instant rather than animated, so there is no
                // multi-frame scroll to wait out.
                const reducedMotion =
                  typeof window.matchMedia === 'function' &&
                  window.matchMedia('(prefers-reduced-motion: reduce)').matches;
                suppressUntilRef.current = {
                  id: entry.id,
                  until: performance.now() + (reducedMotion ? 150 : 1000),
                };
              }}
            >
              {entry.label}
            </a>
          </li>
        ))}
      </ul>
    </nav>
  );
}
