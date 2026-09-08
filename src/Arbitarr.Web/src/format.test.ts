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
});
