import { describe, expect, it } from 'vitest';

import { formatDurationSeconds } from './format';

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
