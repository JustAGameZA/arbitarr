import { describe, expect, it } from 'vitest';

import { formatBytes, formatDurationSeconds } from './format';

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
    expect(formatBytes(null)).toBe('—');
  });

  it('does not depend on locale-sensitive number formatting', () => {
    // toFixed is locale-independent (always uses '.'), unlike toLocaleString.
    expect(formatBytes(1536)).not.toContain(',');
  });
});
