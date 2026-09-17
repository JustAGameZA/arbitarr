import { describe, expect, it } from 'vitest';

import { formatBytes, formatDurationSeconds, formatTimestamp, formatTimestampTitle } from './format';

describe('formatDurationSeconds', () => {
  it('formats whole seconds as hh:mm:ss, matching .NET TimeSpan.ToString()', () => {
    expect(formatDurationSeconds(0)).toBe('00:00:00');
    expect(formatDurationSeconds(60)).toBe('00:01:00');
    expect(formatDurationSeconds(3600)).toBe('01:00:00');
    expect(formatDurationSeconds(300)).toBe('00:05:00');
  });

  it('prefixes a day count once the value reaches 24 hours, matching the "d." form', () => {
    // 86400s = exactly one day = TimeSpan.ToString()'s "1.00:00:00".
    expect(formatDurationSeconds(86400)).toBe('1.00:00:00');
    // 604800s = 7 days = the bead's own worked example, "7.00:00:00".
    expect(formatDurationSeconds(604800)).toBe('7.00:00:00');
  });

  it('floors fractional seconds and never renders a negative duration', () => {
    expect(formatDurationSeconds(59.9)).toBe('00:00:59');
    expect(formatDurationSeconds(-5)).toBe('00:00:00');
  });

  // arb-fao: before the guard, Math.max(0, Math.floor(NaN)) was NaN and every
  // segment padded to the string "NaN", so a field the server omitted rendered
  // "NaN:NaN:NaN" on the Dashboard.
  //
  // The undefined case is the one that actually happened: the signature says
  // `number`, so only a value crossing the untyped fetch boundary can be absent,
  // and the cast reproduces that rather than pretending a caller would pass it.
  //
  // Asserted against '00:00:00' explicitly, not just "not NaN": the em-dash is a
  // deliberate choice over a zero clock, because 00:00:00 is already the right
  // answer for a real zero (pinned above) and reusing it would erase the
  // difference between an absent value and a configured one.
  it('renders the em-dash rather than a zero clock when the value is not finite', () => {
    expect(formatDurationSeconds(Number.NaN)).toBe('—');
    expect(formatDurationSeconds(undefined as unknown as number)).toBe('—');
    expect(formatDurationSeconds(Number.POSITIVE_INFINITY)).toBe('—');
    expect(formatDurationSeconds(Number.NEGATIVE_INFINITY)).toBe('—');

    expect(formatDurationSeconds(Number.NaN)).not.toBe('00:00:00');
    // The character is U+2014 (em-dash), matching formatRate's convention -- a
    // hyphen or en-dash would read identically in review and fail here.
    expect(formatDurationSeconds(Number.NaN)).toBe('—');
  });
});

describe('formatBytes', () => {
  it('renders whole bytes below the KiB boundary', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(1023)).toBe('1023 B');
  });

  it('renders one decimal place at each binary unit boundary', () => {
    expect(formatBytes(1024)).toBe('1.0 KiB');
    expect(formatBytes(1536)).toBe('1.5 KiB');
    expect(formatBytes(1024 ** 2)).toBe('1.0 MiB');
    expect(formatBytes(1024 ** 3)).toBe('1.0 GiB');
    expect(formatBytes(1024 ** 4)).toBe('1.0 TiB');
  });

  it('stays in TiB beyond the largest unit rather than inventing a bigger one', () => {
    expect(formatBytes(1024 ** 4 * 5)).toBe('5.0 TiB');
    // One unit short of the next multiple of the top unit: still rounds up
    // within TiB rather than carrying to a unit that does not exist.
    expect(formatBytes(1024 ** 5 - 1)).toBe('1024.0 TiB');
  });

  it('carries into the next unit when rounding reaches 1024', () => {
    expect(formatBytes(1024 ** 2 - 1)).toBe('1.0 MiB');
    expect(formatBytes(1024 ** 3 - 1)).toBe('1.0 GiB');
    expect(formatBytes(1024 ** 4 - 1)).toBe('1.0 TiB');
    expect(formatBytes(1023.6)).toBe('1.0 KiB');
  });

  // Same sentinel as formatRate and formatDurationSeconds above: a plausible
  // "0 B" is worse than admitting the byte count is not actually known.
  it('renders the em-dash for every absence case rather than a fabricated size', () => {
    expect(formatBytes(null)).toBe('—');
    expect(formatBytes(undefined)).toBe('—');
    expect(formatBytes(Number.NaN)).toBe('—');
    expect(formatBytes(-1)).toBe('—');

    // The character is U+2014 (em-dash), matching the file's other absence
    // sentinels -- a hyphen or en-dash would read identically in review.
    // Checked by code point, not string equality, so a hyphen or en-dash
    // that happened to satisfy toBe above would still fail here.
    expect(formatBytes(null).codePointAt(0)).toBe(0x2014);
  });

  it('does not depend on locale-sensitive number formatting', () => {
    // toFixed is locale-independent (always uses '.'), unlike toLocaleString.
    expect(formatBytes(1536)).not.toContain(',');
  });
});

describe('formatTimestamp', () => {
  /**
   * arb-p94u review fixup: a `/[A-Za-z]/` "has letters" shape check is vacuous
   * under an en-US-ish runner, because a BARE `toLocaleString()` there already
   * renders "AM"/"PM" -- letters with no zone information at all. The check
   * only bit on this machine's en-ZA 24-hour locale, which has no AM/PM. So
   * this asserts the EXACT string `formatTimestamp` must produce
   * (`toLocaleString(undefined, { timeZoneName: 'short' })`), and then proves
   * that string is not what the OLD bare rendering would have produced --
   * the positive control that makes the assertion non-tautological, run
   * against whatever locale/timezone the CURRENT runner actually has, not a
   * hard-coded one.
   */
  it('renders a valid ISO instant the way toLocaleString with timeZoneName renders it', () => {
    const iso = '2026-09-07T12:30:00+00:00';
    const parsed = new Date(iso);
    const expected = parsed.toLocaleString(undefined, { timeZoneName: 'short' });
    const bare = parsed.toLocaleString(undefined);

    // Positive control: if the zone-bearing and bare renderings were identical
    // on this runner, the exact-match assertion below could pass against the
    // old, ambiguous rendering too, and would prove nothing.
    expect(expected).not.toBe(bare);

    expect(formatTimestamp(iso)).toBe(expected);
  });

  it('renders the em-dash for an absent value, matching the file\'s other sentinels', () => {
    expect(formatTimestamp(null)).toBe('—');
    expect(formatTimestamp(undefined)).toBe('—');
    expect(formatTimestamp('')).toBe('—');
  });

  // arb-p94u: an invalid date string must not render "Invalid Date" -- that
  // asserts nothing about what the server actually sent -- nor collapse to the
  // em-dash, which would make a server-side format change indistinguishable
  // from a genuinely missing timestamp. Showing the raw value is what
  // Activity's own Timestamp already did before this helper existed.
  it('renders an unparseable value verbatim rather than "Invalid Date" or the em-dash', () => {
    expect(formatTimestamp('not-a-date')).toBe('not-a-date');
    expect(formatTimestamp('not-a-date')).not.toBe('Invalid Date');
    expect(formatTimestamp('not-a-date')).not.toBe('—');
  });
});

describe('formatTimestampTitle', () => {
  it('carries the raw ISO instant unchanged, for comparison against a log line', () => {
    expect(formatTimestampTitle('2026-09-07T12:30:00+00:00')).toBe('2026-09-07T12:30:00+00:00');
  });

  it('falls back to the empty string for an absent value, so the attribute is omitted', () => {
    expect(formatTimestampTitle(null)).toBe('');
    expect(formatTimestampTitle(undefined)).toBe('');
  });
});
