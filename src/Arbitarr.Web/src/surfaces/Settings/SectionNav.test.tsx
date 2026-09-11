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
    intersect(...ids: string[]) {
      const records = observed.map((target) => ({
        target,
        isIntersecting: ids.includes(target.id),
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

    observer.intersect('sources');

    const current = screen.getByRole('link', { current: true });
    expect(current).toHaveTextContent('Sources');
    // Exactly one: aria-current left on a previous entry would give a screen
    // reader two "current" sections at once.
    expect(screen.getAllByRole('link', { current: true })).toHaveLength(1);
  });

  it('follows the observed section as it changes', () => {
    const observer = stubObserver();
    renderNav();

    observer.intersect('sources');
    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Sources');

    observer.intersect('caching');
    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Caching');
  });

  it('highlights the topmost section when several are on screen at once', () => {
    const observer = stubObserver();
    renderNav();

    // Reported out of document order on purpose: the callback's array order is
    // not the page's, so taking the last reported entry would highlight the
    // section furthest DOWN the page when scrolling up.
    observer.intersect('caching', 'sources');

    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Sources');
  });

  it('keeps the last active entry when nothing is intersecting', () => {
    const observer = stubObserver();
    renderNav();

    observer.intersect('sources');
    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Sources');

    // Scrolling through a gap between sections should not blank the highlight;
    // the operator is still nearest the section they just left.
    observer.intersect();

    expect(screen.getByRole('link', { current: true })).toHaveTextContent('Sources');
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
