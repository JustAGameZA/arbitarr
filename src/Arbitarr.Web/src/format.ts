/**
 * Formats a ratio as a percentage, or a dash when there is nothing to divide.
 *
 * `rate`/`hitRate` are null until the counter has seen traffic, and a fresh
 * process legitimately has none. Rendering "0%" there would assert a measured
 * zero hit rate, which is a different and wronger claim than "no data yet".
 *
 * MOVED HERE FROM System.tsx (#54 step 6) so there is exactly ONE em-dash
 * convention rather than a second copy that can drift. It now has two callers
 * that reach the null case for the same reason from different directions:
 *
 *   - System's observability counters, which are null until traffic arrives.
 *   - #54's agreement rate, which is null until somebody reviews a decision.
 *     That call site divides Agreed by Reviewed itself, because the endpoint
 *     deliberately returns COUNTS AND NEVER A RATE: with zero reviews there is
 *     no rate, since 0/0 is not 0%, and the ratio is therefore formed at the
 *     point of display. AC4 is precisely that this renders the dash and not
 *     "0%" — "the pipeline has been right 0% of the time" is an accusation the
 *     data does not support when nobody has judged it yet.
 *
 * The dash is an EM-dash (U+2014), matching what the System surface has always
 * rendered; the tests assert on that exact character, so a hyphen would pass
 * review and fail the suite.
 */
export function formatRate(rate: number | null): string {
  return rate === null ? '—' : `${(rate * 100).toFixed(1)}%`;
}

/**
 * The agreement rate as a ratio, or null when nothing has been reviewed.
 *
 * Split out from its rendering so the "no data yet" decision is made ONCE, in
 * one testable place, rather than re-derived by every caller that wants to show
 * the figure. Guarding on `reviewed <= 0` rather than `=== 0` because a
 * negative count could only be a server bug, and dividing by it would print a
 * confident nonsense percentage instead of admitting there is nothing to state.
 */
export function agreementRate(agreed: number, reviewed: number): number | null {
  return reviewed <= 0 ? null : agreed / reviewed;
}

/**
 * Formats a whole-seconds duration to match .NET's `TimeSpan.ToString()` default
 * ("c") format: `hh:mm:ss`, with a `d.` day prefix once the value reaches 24
 * hours (`7.00:00:00`).
 *
 * This exists because Dashboard's `EffectiveConfigResponse` carries its durations
 * as raw `*Seconds: number` fields and had grown its own bare `${value}s` printer
 * (arb-rzx / audit F-018), while System's staleness envelope and Settings' catalog
 * values are both `TimeSpan`-typed server-side and already arrive pre-formatted
 * this way (`SettingsValidator.cs`'s bound strings, `StalenessEnvelopeResponse`).
 * Those two surfaces render their strings VERBATIM on purpose — see
 * `System.tsx`'s `StalenessTable` comment — so this formatter has exactly one
 * caller: Dashboard's own numeric fields, reproducing the same convention on the
 * client since the server never sent it as a string in the first place.
 */
export function formatDurationSeconds(totalSeconds: number): string {
  const whole = Math.max(0, Math.floor(totalSeconds));
  const days = Math.floor(whole / 86400);
  const hours = Math.floor((whole % 86400) / 3600);
  const minutes = Math.floor((whole % 3600) / 60);
  const seconds = whole % 60;
  const pad = (value: number) => value.toString().padStart(2, '0');
  const clock = `${pad(hours)}:${pad(minutes)}:${pad(seconds)}`;
  return days > 0 ? `${days}.${clock}` : clock;
}
