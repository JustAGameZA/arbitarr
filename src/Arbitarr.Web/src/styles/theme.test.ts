import { describe, expect, it } from 'vitest';

/**
 * AC-CHROME — the assertions that make "*arr chrome" un-losable.
 *
 * This requirement went missing once between planning sessions, and the gates
 * that existed then could not detect the loss: the colour grep never scanned
 * theme.css, so `--bg-app: #ffffff` — a pure-white theme — passed everything.
 * These tests close that hole by reading the palette back RESOLVED from the
 * document rather than grepping its source.
 *
 * Precondition, and why it is stated here: this file only works because
 * vite.config.ts sets `css: true` AND vitest.setup.ts imports theme.css. Vitest
 * defaults to css: false, under which the import is an empty stub, no
 * stylesheet reaches the document, and every read below returns ''. The
 * throw-on-empty in readToken() exists so that failure mode announces itself as
 * a broken harness instead of masquerading as a wrong colour.
 */

/** Reads a custom property off :root, refusing to silently accept an empty result. */
function readToken(name: string): string {
  const raw = getComputedStyle(document.documentElement).getPropertyValue(name);
  const value = raw.trim();
  if (value === '') {
    throw new Error(
      `Token ${name} resolved to an empty string. The stylesheet is not attached to the ` +
        'document — check that vite.config.ts sets `test.css: true` and that vitest.setup.ts ' +
        'imports ./src/styles/theme.css. Do NOT weaken this assertion to make it pass.',
    );
  }
  return value;
}

/** Relative luminance per WCAG 2.x, from a #rrggbb string. */
function relativeLuminance(hex: string): number {
  const match = /^#([0-9a-f]{6})$/i.exec(hex);
  if (!match) {
    throw new Error(`Expected a #rrggbb colour, got: ${hex}`);
  }
  const int = Number.parseInt(match[1], 16);
  const channels = [(int >> 16) & 0xff, (int >> 8) & 0xff, int & 0xff].map((raw) => {
    const c = raw / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

/** WCAG contrast ratio between two #rrggbb colours. */
function contrastRatio(a: string, b: string): number {
  const la = relativeLuminance(a);
  const lb = relativeLuminance(b);
  const [lighter, darker] = la > lb ? [la, lb] : [lb, la];
  return (lighter + 0.05) / (darker + 0.05);
}

describe('AC-CHROME-1: the pinned palette resolves to exactly the *arr values', () => {
  // Changing any value here is allowed, but it must move theme.css and this
  // table in ONE commit. That coupling is the whole mechanism.
  const PINNED: ReadonlyArray<readonly [string, string]> = [
    ['--bg-app', '#1a1d21'],
    ['--bg-sidebar', '#20232a'],
    ['--bg-panel', '#252930'],
    ['--bg-panel-alt', '#2c313a'],
    ['--border', '#3a4048'],
    ['--fg-primary', '#e8eaed'],
    ['--fg-muted', '#9aa0a8'],
    ['--accent', '#5a9fd4'],
    ['--accent-hover', '#74b3e0'],
    ['--ok', '#5cb85c'],
    ['--warn', '#e0a030'],
    ['--danger', '#d9534f'],
  ];

  it.each(PINNED)('%s is %s', (token, expected) => {
    expect(readToken(token)).toBe(expected);
  });
});

describe('AC-CHROME-2: the theme is dark, not merely "themed"', () => {
  it('the app ground is genuinely dark, so a white theme cannot pass', () => {
    // This is the specific assertion whose absence let #ffffff through before.
    expect(relativeLuminance(readToken('--bg-app'))).toBeLessThan(0.05);
  });

  it('grounds step monotonically lighter from app to sidebar to panel to panel-alt', () => {
    const ladder = ['--bg-app', '--bg-sidebar', '--bg-panel', '--bg-panel-alt'].map((token) =>
      relativeLuminance(readToken(token)),
    );
    for (let i = 1; i < ladder.length; i += 1) {
      expect(ladder[i]).toBeGreaterThan(ladder[i - 1]);
    }
  });
});

describe('AC-CHROME-3: text on the app ground stays legible', () => {
  it('primary text clears WCAG AAA body text (7:1)', () => {
    expect(contrastRatio(readToken('--fg-primary'), readToken('--bg-app'))).toBeGreaterThan(7);
  });

  it('muted text clears WCAG AA body text (4.5:1)', () => {
    expect(contrastRatio(readToken('--fg-muted'), readToken('--bg-app'))).toBeGreaterThan(4.5);
  });
});

/** Parses `rgba(r, g, b, a)` into its four components, refusing anything else. */
function parseRgba(value: string): { r: number; g: number; b: number; a: number } {
  const match = /^rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([\d.]+)\s*\)$/.exec(value);
  if (!match) {
    throw new Error(`Expected an rgba(r, g, b, a) colour, got: ${value}`);
  }
  return {
    r: Number(match[1]),
    g: Number(match[2]),
    b: Number(match[3]),
    a: Number(match[4]),
  };
}

/**
 * Composites a translucent rgba() over an opaque #rrggbb ground and returns the
 * resulting #rrggbb, so the existing contrastRatio() above can be reused rather
 * than a second contrast implementation growing beside it.
 */
function compositeOver(rgba: string, groundHex: string): string {
  const { r, g, b, a } = parseRgba(rgba);
  const match = /^#([0-9a-f]{6})$/i.exec(groundHex);
  if (!match) {
    throw new Error(`Expected a #rrggbb ground, got: ${groundHex}`);
  }
  const int = Number.parseInt(match[1], 16);
  const ground = [(int >> 16) & 0xff, (int >> 8) & 0xff, int & 0xff];
  const blended = [r, g, b].map((channel, i) =>
    Math.round(channel * a + ground[i] * (1 - a)),
  );
  return `#${blended.map((c) => c.toString(16).padStart(2, '0')).join('')}`;
}

describe('AC-CHROME-4: layout constants come from tokens', () => {
  it.each([
    ['--sidebar-width', '210px'],
    ['--topbar-height', '48px'],
    ['--content-max-width', '1440px'],
  ])('%s is %s', (token, expected) => {
    expect(readToken(token)).toBe(expected);
  });
});

describe('AC-CHROME-5: status badges are filled at low opacity and stay legible', () => {
  // Badges are filled rather than outlined (arb-0tu). The fill has to stay
  // translucent enough that the panel ground reads through, and the semantic
  // text colour has to keep clearing WCAG AA once composited over that fill.
  const FILLS: ReadonlyArray<readonly [string, string]> = [
    ['--badge-fill', '--fg-muted'],
    ['--ok-fill', '--ok'],
    ['--warn-fill', '--warn'],
    ['--danger-fill', '--danger'],
  ];

  it.each(FILLS)('%s is a translucent fill below 0.25 alpha', (fill) => {
    expect(parseRgba(readToken(fill)).a).toBeLessThan(0.25);
  });

  // Everything except danger clears the AA body-text floor composited over the
  // panel. A fill LIGHTENS a dark ground, so these ratios fall as alpha rises:
  // at alpha 0.16 muted and ok drop to 4.20 and 4.44. theme.css's 0.12 is what
  // keeps them here, which is why this assertion is what pins that value.
  it.each(FILLS.filter(([fill]) => fill !== '--danger-fill'))(
    'text on %s clears WCAG AA body text (4.5:1)',
    (fill, text) => {
      const filled = compositeOver(readToken(fill), readToken('--bg-panel'));
      expect(contrastRatio(readToken(text), filled)).toBeGreaterThan(4.5);
    },
  );

  // --danger is the documented exception, held to 3:1 — the WCAG AA floor for
  // large/bold text (badges are 11px at weight 600). This is NOT a threshold
  // chosen to make a failing test pass: --danger (#d9534f) is already 3.68:1
  // on --bg-panel with NO fill at all, so it misses AA body text on master
  // today, before this change existed, and no fill alpha can lift it over 4.5
  // because a fill only ever reduces contrast here. Lightening --danger is
  // tracked as arb-4uk, which must move the token and its AC-CHROME-1 literal
  // in one commit; when that lands, fold this case back into the 4.5
  // assertion above and delete this block.
  it('text on --danger-fill clears the large/bold-text floor (3:1) — see arb-4uk', () => {
    const filled = compositeOver(readToken('--danger-fill'), readToken('--bg-panel'));
    expect(contrastRatio(readToken('--danger'), filled)).toBeGreaterThan(3);
  });
});
