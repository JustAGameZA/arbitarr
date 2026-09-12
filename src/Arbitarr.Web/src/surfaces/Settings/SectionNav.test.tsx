import { act, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { SectionNav, slugifyGroup } from './SectionNav';

const entries = [
  { id: 'account', label: 'Account' },
  { id: 'sources', label: 'Sources' },
  { id: 'caching', label: 'Caching' },
];

/**
 * Renders the nav with matching target elements in the document, so the
 * observer has something real to observe.
 */
function renderNav() {
  return render(
    <>
      <SectionNav entries={entries} />
      {entries.map((entry) => (
        <div key={entry.id} id={entry.id}>
          {entry.label} content
        </div>
      ))}
    </>,
  );
}

/**
 * Installs a stub IntersectionObserver and returns a trigger that fires the
 * callback with the given ids reported as intersecting.
 *
 * jsdom implements no IntersectionObserver at all, so the real highlight path
 * can only run against a stub; the media queries that drive the sticky layout
 * are likewise never evaluated here. Both are browser-only concerns and are
 * checked manually.
 */
function stubObserver() {
  const observed: Element[] = [];
  let callback: IntersectionObserverCallback | undefined;
  const disconnect = vi.fn();

  class StubObserver {
    constructor(cb: IntersectionObserverCallback) {
      callback = cb;
    }
    observe(target: Element) {
      observed.push(target);
    }
    disconnect = disconnect;
    unobserve = vi.fn();
    takeRecords = vi.fn(() => []);
    root = null;
    rootMargin = '';
    thresholds = [];
  }

  vi.stubGlobal('IntersectionObserver', StubObserver);

  return {
    observed,
    disconnect,
    /**
     * Fires the observer callback. `tops` gives each intersecting id's
     * `boundingClientRect.top` (defaulting to 0, i.e. exactly at a
     * zero-positioned band -- callers that care about the topmost comparison
     * pass real values); entries not present in `tops` but named in `ids`
     * fall back to the default so existing single-id calls are unaffected.
     */
    intersect(ids: string[], tops: Record<string, number> = {}) {
      const records = observed.map((target) => ({
        target,
        isIntersecting: ids.includes(target.id),
        boundingClientRect: { top: tops[target.id] ?? 0 } as DOMRectReadOnly,
      })) as unknown as IntersectionObserverEntry[];
      // act() is required, not decorative: the callback drives a setState from
      // outside React's event system, so without it the re-render has not been
      // flushed when the next assertion reads the DOM. A test that only ever
      // awaited findBy* would paper over this by retrying; a synchronous second
      // assertion in the same test reads the stale markup and fails.
      act(() => {
        callback?.(records, {} as IntersectionObserver);
      });
    },
  };
}

describe('SectionNav', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders one link per entry, each pointing at its own anchor', () => {
    renderNav();

    const links = screen.getAllByRole('link');
    expect(links.map((link) => link.getAttribute('href'))).toEqual([
      '#account',
      '#sources',
      '#caching',
    ]);
  });

  it('renders without throwing when the environment has no IntersectionObserver', () => {
    // The jsdom path. Without the typeof guard in SectionNav the constructor
    // throws here and takes the whole Settings surface down with it, so this
    // failing would mean every Settings test fails for an unrelated reason.
    vi.stubGlobal('IntersectionObserver', undefined);

    expect(() => renderNav()).not.toThrow();
    // And with no observer there is simply no active entry, rather than a
    // wrongly-highlighted first one.
    expect(screen.queryByRole('link', { current: true })).toBeNull();
  });

  it('marks the observed section current, and only that one', () => {
    const observer = stubObserver();
    renderNav();

    observer.intersect(['sources']);

    const current = screen.getByRole('link', { current: true });
    expect(current).toHaveTextContent('Sources');
    // Exactly one: aria-current left on a previous entry would give a screen
    // reader two "current" sections at once.
    expect(screen.getAllByRole('link', { current: true })).toHaveLength(1);
  });

  it('follows the observed section as it changes', () => {
    const observer = stubObserver();
    renderNav();

    observer.intersect(['sources']);
    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Sources');

    observer.intersect(['caching']);
    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Caching');
  });

  it('highlights the section whose top has reached the band, not whichever is first in array order', () => {
    const observer = stubObserver();
    renderNav();

    // Reported out of document order on purpose: the callback's array order is
    // not the page's. Sources' top (40) has reached the band (bandTop 0);
    // Caching's top (500) has not, so on document order alone -- with no
    // geometry -- a naive "first in array" pick could still land on either.
    // The real signal is geometry: Sources is inside the band, Caching is not.
    observer.intersect(['caching', 'sources'], { caching: 500, sources: 40 });

    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Sources');
  });

  it('prefers the section whose start is inside the band over a taller one merely bleeding into it', () => {
    const observer = stubObserver();
    renderNav();

    // Account is a tall preceding section still overlapping the band from
    // above (its top is well above bandTop, at -400); Sources' own top has
    // scrolled to just inside the band (10, >= bandTop 0). The array-order
    // `find` this replaces would have picked Account here (it comes first in
    // `entries`) purely because of that ordering, with identical geometry to
    // the reverse-order case in the previous test.
    observer.intersect(['account', 'sources'], { account: -400, sources: 10 });

    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Sources');
  });

  it('falls back to the entry nearest the band from above when none has reached it yet', () => {
    const observer = stubObserver();
    renderNav();

    // Both intersecting sections' tops are still above the band (bandTop 0):
    // Account is far above (-800), Sources is closer (-50). Neither "has
    // reached" the band, so the nearest-from-above fallback applies, and
    // Sources -- the larger (less negative) top -- wins.
    observer.intersect(['account', 'sources'], { account: -800, sources: -50 });

    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Sources');
  });

  it('keeps the last active entry when nothing is intersecting', () => {
    const observer = stubObserver();
    renderNav();

    observer.intersect(['sources']);
    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Sources');

    // Scrolling through a gap between sections should not blank the highlight;
    // the operator is still nearest the section they just left.
    observer.intersect([]);

    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Sources');
  });

  it('highlights the clicked entry immediately, before the observer reports anything', () => {
    renderNav();

    act(() => {
      screen.getByRole('link', { name: 'Caching' }).click();
    });

    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Caching');
  });

  it('highlights a clicked entry that can never intersect the band (observer reports nothing for it)', () => {
    const observer = stubObserver();
    renderNav();

    act(() => {
      screen.getByRole('link', { name: 'Caching' }).click();
    });

    // The section is short and sits below the last reachable scroll position
    // (arb-81x8 bug 1): the observer never reports it as intersecting at all,
    // so the click must be what holds the highlight, not the observer.
    observer.intersect(['account']);

    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Caching');
  });

  it('does not let a stale observer callback for a different entry steal the highlight right after a click', () => {
    const observer = stubObserver();
    renderNav();

    act(() => {
      screen.getByRole('link', { name: 'Caching' }).click();
    });

    // A callback naming a DIFFERENT entry, fired while the page is still
    // mid-scroll toward the clicked target -- exactly the burst a smooth
    // scroll produces for whatever the viewport passes on the way.
    observer.intersect(['account'], { account: 20 });
    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Caching');

    // Once the clicked target itself is reported, the observer resumes.
    observer.intersect(['caching'], { caching: 20 });
    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Caching');
  });

  it('disconnects the observer on unmount', () => {
    const observer = stubObserver();
    const { unmount } = renderNav();

    unmount();

    // A leaked observer keeps the detached section nodes alive and fires into
    // a setState on an unmounted component.
    expect(observer.disconnect).toHaveBeenCalled();
  });
});

describe('SectionNav edge fade', () => {
  /**
   * Stubs the nav element's scroll geometry, since jsdom performs no layout
   * and scrollWidth/clientWidth/scrollLeft are always 0 there. `scrollLeft`
   * is stubbed as a getter/setter pair, not a plain value, so a test can
   * change it after render the same way a real scroll event would and have
   * the component's own scroll listener see the new value.
   */
  function stubScrollGeometry(nav: HTMLElement, { scrollWidth, clientWidth, scrollLeft = 0 }: {
    scrollWidth: number;
    clientWidth: number;
    scrollLeft?: number;
  }) {
    let currentScrollLeft = scrollLeft;
    Object.defineProperty(nav, 'scrollWidth', { configurable: true, value: scrollWidth });
    Object.defineProperty(nav, 'clientWidth', { configurable: true, value: clientWidth });
    Object.defineProperty(nav, 'scrollLeft', {
      configurable: true,
      get: () => currentScrollLeft,
      set: (value: number) => {
        currentScrollLeft = value;
      },
    });
  }

  function fireScroll(nav: HTMLElement, scrollLeft: number) {
    act(() => {
      nav.scrollLeft = scrollLeft;
      nav.dispatchEvent(new Event('scroll'));
    });
  }

  it('shows no fade when the content fits (scrollWidth === clientWidth)', () => {
    renderNav();
    const nav = screen.getByRole('navigation');
    act(() => {
      stubScrollGeometry(nav, { scrollWidth: 300, clientWidth: 300 });
      nav.dispatchEvent(new Event('scroll'));
    });

    expect(nav).not.toHaveAttribute('data-fade-left');
    expect(nav).not.toHaveAttribute('data-fade-right');
  });

  it('shows only the right fade when overflowing at scrollLeft 0', () => {
    renderNav();
    const nav = screen.getByRole('navigation');
    act(() => {
      stubScrollGeometry(nav, { scrollWidth: 600, clientWidth: 300, scrollLeft: 0 });
      nav.dispatchEvent(new Event('scroll'));
    });

    expect(nav).not.toHaveAttribute('data-fade-left');
    expect(nav).toHaveAttribute('data-fade-right', 'true');
  });

  it('shows only the left fade once scrolled to the end', () => {
    renderNav();
    const nav = screen.getByRole('navigation');
    stubScrollGeometry(nav, { scrollWidth: 600, clientWidth: 300, scrollLeft: 0 });

    fireScroll(nav, 300);

    expect(nav).toHaveAttribute('data-fade-left', 'true');
    expect(nav).not.toHaveAttribute('data-fade-right');
  });

  it('shows both fades when scrolled to the middle of overflowing content', () => {
    renderNav();
    const nav = screen.getByRole('navigation');
    stubScrollGeometry(nav, { scrollWidth: 600, clientWidth: 300, scrollLeft: 0 });

    fireScroll(nav, 150);

    expect(nav).toHaveAttribute('data-fade-left', 'true');
    expect(nav).toHaveAttribute('data-fade-right', 'true');
  });

  it('absorbs a sub-pixel short end state within the 1px slack (no right fade)', () => {
    renderNav();
    const nav = screen.getByRole('navigation');
    stubScrollGeometry(nav, { scrollWidth: 600.5, clientWidth: 300, scrollLeft: 0 });

    fireScroll(nav, 300);

    expect(nav).toHaveAttribute('data-fade-left', 'true');
    expect(nav).not.toHaveAttribute('data-fade-right');
  });
});

describe('slugifyGroup', () => {
  it('lowercases and hyphenates a server-owned group name', () => {
    expect(slugifyGroup('Caching')).toBe('caching');
    expect(slugifyGroup('Ingest pipeline')).toBe('ingest-pipeline');
  });

  it('collapses runs of non-alphanumerics and trims the ends', () => {
    // A naive replace leaves "search--indexing" and "-search-" here, both of
    // which still work as ids but read as mangled in a URL bar or bookmark.
    expect(slugifyGroup('Search  &  indexing')).toBe('search-indexing');
    expect(slugifyGroup('  Observability  ')).toBe('observability');
    expect(slugifyGroup('API / keys')).toBe('api-keys');
  });
});
