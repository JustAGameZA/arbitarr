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
 *
 * arb-fao: a non-finite input renders the EM-dash (U+2014), the same "no value"
 * sentinel `formatRate` above uses, NOT `00:00:00`. The type says `number`, but
 * an `EffectiveConfigResponse` field the server omits arrives as `undefined`
 * through a cast at the fetch boundary, and `Math.max(0, Math.floor(NaN))` is
 * `NaN`, so the surface rendered `NaN:NaN:NaN`.
 *
 * `00:00:00` is rejected as the sentinel because it is ALREADY the correct
 * render of a real zero (the test above pins `formatDurationSeconds(0)`), so
 * using it here would make "the server sent nothing" indistinguishable from "the
 * server sent zero" — a missing refresh lead would read as a configured
 * zero-second one. That is the same error `formatRate` documents at the top of
 * this file: printing a measured-looking value asserts a fact the data does not
 * support. The dash admits the gap instead, and the file then has ONE absence
 * convention rather than two that must be remembered separately.
 */
export function formatDurationSeconds(totalSeconds: number): string {
  if (!Number.isFinite(totalSeconds)) {
    return '—';
  }

  const whole = Math.max(0, Math.floor(totalSeconds));
  const days = Math.floor(whole / 86400);
  const hours = Math.floor((whole % 86400) / 3600);
  const minutes = Math.floor((whole % 3600) / 60);
  const seconds = whole % 60;
  const pad = (value: number) => value.toString().padStart(2, '0');
  const clock = `${pad(hours)}:${pad(minutes)}:${pad(seconds)}`;
  return days > 0 ? `${days}.${clock}` : clock;
}

/**
 * Formats an ISO-8601 instant unambiguously (AC9), or the em-dash when there is
 * none.
 *
 * MOVED HERE FROM Activity.tsx's `Timestamp`/`repeatTitle` (arb-p94u), the
 * original site of this reasoning: `toLocaleString` renders in the VIEWER's
 * timezone, which is the right default -- an operator reads "did this happen
 * during the outage?" in their own clock -- but a bare local time is exactly
 * the ambiguity AC9 forbids, since the same string means different instants to
 * two readers. So the offset comes with it via `timeZoneName: 'short'`.
 *
 * `null`/`undefined`/`''` render the em-dash (U+2014), the same absence
 * convention `formatRate`/`formatDurationSeconds`/`formatBytes` use above --
 * one "no data" idiom for this file, not a second one invented per caller.
 *
 * A value that fails to parse renders VERBATIM rather than as the em-dash or
 * "Invalid Date": collapsing it to the absence sentinel would make a
 * server-side format change look like a missing timestamp, and "Invalid Date"
 * asserts nothing useful about what the server actually sent. Showing the raw
 * string is what Activity's own `Timestamp` already did for this case.
 *
 * This deliberately does NOT invent a relative-time formatter ("3 minutes
 * ago"). The project's convention for durations is verbatim
 * `TimeSpan.ToString()` (System.tsx, a load-bearing comment there), and the
 * same honesty principle applies here: state the instant, do not hide it
 * behind a rounded approximation that cannot be compared against a log line.
 */
export function formatTimestamp(value: string | null | undefined): string {
  if (value === null || value === undefined || value === '') {
    return '—';
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }

  return parsed.toLocaleString(undefined, { timeZoneName: 'short' });
}

/**
 * The `title` attribute for a `formatTimestamp`-rendered element: the raw ISO
 * instant the server sent, so a reader can compare it against a log line
 * exactly, without relying on the (already-localized) visible text.
 *
 * Split out from `formatTimestamp` rather than folded into a combined
 * "renderer" component: the five call sites this helper has (Activity,
 * Dashboard, Suppressions, Search, ApiKeys) render the value in a `<td>` or a
 * `<time>`, not a shared component shape, so a JSX-returning helper would force
 * one of them into markup it does not otherwise use. A plain string pairs with
 * either.
 *
 * Absent values fall back to the empty string rather than the em-dash used in
 * the visible text: a `title=""` attribute is simply omitted from the
 * accessibility tree, where `title="—"` would read as if the raw instant were
 * itself unknown-but-present.
 */
export function formatTimestampTitle(value: string | null | undefined): string {
  return value ?? '';
}

/**
 * Formats a byte count in binary units (B, KiB, MiB, GiB, TiB), one decimal
 * place once the value reaches KiB or above, whole bytes below that.
 *
 * Same absence convention as `formatRate` and `formatDurationSeconds` above:
 * `null`, `undefined`, `NaN` and negative values all render the em-dash
 * (U+2014) rather than a plausible-looking size. A release or file whose byte
 * count is genuinely unknown is a different fact from one that is zero bytes
 * long, and a size cannot be negative in the first place, so treating a
 * negative input as "no data" rather than clamping it to zero avoids printing
 * a confident, fabricated figure for a value the server never actually sent.
 *
 * Units are binary (1024-based, KiB/MiB/GiB/TiB) rather than decimal
 * (1000-based, KB/MB/GB/TB): filesystems and the byte counts Sonarr/Radarr
 * and NZB indexers report are all binary-multiple already, so a decimal
 * label here would be a mislabelled unit, not just a rounding difference.
 */
export function formatBytes(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value) || value < 0) {
    return '—';
  }

  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let amount = value;
  let unit = 0;
  while (amount >= 1024 && unit < units.length - 1) {
    amount /= 1024;
    unit += 1;
  }

  // Round before deciding whether the value has crossed into the next unit:
  // 1048575 (1024**2 - 1) divides down to 1023.999... KiB, which toFixed(1)
  // alone would print as "1024.0 KiB" -- a unit-sized figure in a unit it
  // never carried into. Rounding first and re-checking the threshold carries
  // it to "1.0 MiB" instead, the same way the values on either side of it do.
  let display = Number(amount.toFixed(unit === 0 ? 0 : 1));
  if (display >= 1024 && unit < units.length - 1) {
    unit += 1;
    display = Number((display / 1024).toFixed(1));
  }
  return `${display.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}
