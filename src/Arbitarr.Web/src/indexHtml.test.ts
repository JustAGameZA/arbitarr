import { describe, expect, it } from 'vitest';

import html from '../index.html?raw';

// index.html is never loaded through JSDOM in this suite (Vite serves it, not
// React), so this test imports the raw source instead. It exists because a
// missing <link rel="icon"> causes the browser to probe /favicon.ico, which
// the SPA host does not serve -- the only console error the app produces
// (issue #47).
describe('index.html', () => {
  it('declares a favicon so the browser does not probe /favicon.ico', () => {
    expect(html).toMatch(/<link\s+rel="icon"[^>]*href="\/favicon\.svg"/);
  });

  it('declares exactly one icon link', () => {
    const matches = html.match(/<link\s+rel="icon"/g) ?? [];
    expect(matches).toHaveLength(1);
  });
});
