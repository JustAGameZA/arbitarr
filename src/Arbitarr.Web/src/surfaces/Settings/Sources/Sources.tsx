import { useState } from 'react';

import { QueryState, errorMessage } from '../../QueryState';
import styles from '../../surface.module.css';
import { useSecretEvictingMutation } from '../useSecretEvictingMutation';
import local from './Sources.module.css';
import {
  useCreateSourceMutation,
  useDeleteSourceMutation,
  useSourcesQuery,
  useTestSourceMutation,
  useUpdateSourceMutation,
} from './queries';
import {
  DAY_LIMITS_UNIT,
  HOUR_LIMITS_UNIT,
  NEWZNAB_KIND,
  NZBHYDRA_KIND,
  PROXY_ACCESS_MODE,
  REDIRECT_ACCESS_MODE,
  TORZNAB_KIND,
  type CreateSourceRequest,
  type SourceSummary,
  type UpdateSourceRequest,
} from './types';
import { formatTimestamp, formatTimestampTitle } from '../../../format';

/**
 * The two NZB access modes the form offers, in the order they are shown — Proxy
 * first, because it is the default and the one that does not expose the indexer
 * key.
 *
 * SPELLED EXACTLY AS THE SERVER ACCEPTS THEM. `SourceRepository` matches this
 * value by exact ordinal name and rejects `'redirect'`, `'REDIRECT'`, `' 1 '`
 * and `'1'` alike, so a casing or whitespace "tidy-up" here turns every save
 * into a 400.
 */
const ACCESS_MODES = [PROXY_ACCESS_MODE, REDIRECT_ACCESS_MODE] as const;

/**
 * arb-x7w8.1 — the three kinds the form offers, in `SourceRepository.KnownKinds`
 * order.
 *
 * SPELLED EXACTLY AS THE SERVER ACCEPTS THEM, on the same terms as
 * `ACCESS_MODES` above: `ValidateKind` compares ordinally against this exact
 * set, so `'nzbhydra'` is a 400 rather than a tolerated variant.
 *
 * A PICKER AND NOT A TEXT BOX, and that is the point of this constant. Kind was
 * free text until arb-x7w8.16, which meant the only way to discover the three
 * accepted spellings was to submit a wrong one and read the rejection — and a
 * typo that reached the database would be listed forever while never matching
 * the ordinal comparison any resolver makes. A closed set of options cannot
 * produce a value the server will refuse, so the rejection stops being something
 * an operator has to learn their way around.
 */
const KINDS = [NZBHYDRA_KIND, NEWZNAB_KIND, TORZNAB_KIND] as const;

/**
 * The operator-facing label for each kind. Names the PROTOCOL the value selects
 * rather than restating the enum, because "Newznab" and "Torznab" differ in what
 * they return and an operator choosing between them is choosing usenet or
 * torrents, which the bare name does not say.
 */
const KIND_LABELS: Record<string, string> = {
  [NZBHYDRA_KIND]: 'NZBHydra2 — an aggregator in front of other indexers',
  [NEWZNAB_KIND]: 'Newznab — a usenet indexer, queried directly',
  [TORZNAB_KIND]: 'Torznab — a torrent indexer, queried directly',
};

/**
 * arb-x7w8.1 — the two accepted rolling windows the query and grab limits are
 * counted over, matched by the server exactly and ordinally like the kinds.
 */
const LIMITS_UNITS = [HOUR_LIMITS_UNIT, DAY_LIMITS_UNIT] as const;

const LIMITS_UNIT_LABELS: Record<string, string> = {
  [HOUR_LIMITS_UNIT]: 'per hour',
  [DAY_LIMITS_UNIT]: 'per day',
};

/**
 * The operator-facing label for each mode. Each says what ARBITARR does, not
 * what the setting is called, because "Redirect" alone does not tell an operator
 * that a credential changes hands — the warning below the control carries the
 * consequence, and this carries the behaviour.
 */
const ACCESS_MODE_LABELS: Record<string, string> = {
  [PROXY_ACCESS_MODE]: 'Proxy — Arbitarr downloads the file and serves it',
  [REDIRECT_ACCESS_MODE]: 'Redirect — send the client to the indexer',
};

/**
 * The operator-facing label for each probe outcome, keyed by the server's
 * closed `SourceProbeOutcome` enum.
 *
 * FIVE DISTINCT LABELS, NOT ONE RED "FAILED" (§3.3/AC4). The four failure modes
 * have entirely different fixes — a wrong port, an expired certificate, a stale
 * key, and a base URL pointing at a reverse proxy are not the same problem — so
 * collapsing them into a single verdict makes the button decorative. The
 * server's longer `message` is rendered underneath and says what to check next;
 * this is the short badge that makes the five scannable at a glance.
 *
 * NONE OF THIS TEXT IS DERIVED FROM THE KEY, and none of it can be: the server's
 * enum carries no string field, so there is no upstream body, exception message,
 * or submitted credential anywhere in the path that produces it. Do not add a
 * free-text field to carry a server message through — that is the leak this
 * shape prevents.
 */
const OUTCOME_LABELS: Record<string, string> = {
  Ok: 'Connected',
  Unreachable: 'Unreachable',
  TlsFailure: 'TLS failure',
  AuthenticationFailed: 'API key rejected',
  UnexpectedResponse: 'Unexpected response',
};

/**
 * Falls back to the outcome string itself rather than to a generic "failed".
 *
 * If the server ever grows a sixth outcome, an operator seeing its raw enum name
 * still learns more than one seeing "Error" — and the fallback is safe to render
 * because the outcome is a closed enum name, never free text.
 */
const outcomeLabel = (outcome: string): string => OUTCOME_LABELS[outcome] ?? outcome;

/**
 * arb-x7w8.11 — the operator-facing label for each runtime state, keyed by the
 * server's closed `SourceRuntimeState` enum.
 *
 * FOUR DISTINCT LABELS, NOT ONE "UNAVAILABLE", for exactly the reason
 * `OUTCOME_LABELS` above gives for the five probe outcomes — this is that same
 * argument applied to a different closed enum, and the server entity states it
 * directly: collapsing these "reports a broken key as a temporary pause and
 * removes the signal to go and fix it". Each wants a different response:
 *
 * - Budgeted — the allowance for this window is spent. Raise the limit, or wait.
 * - Backing off — a transient fault. IT CLEARS ITSELF and needs no action at
 *   all, which is precisely why it is not a `/api/status` health item.
 * - Permanently disabled — the key was rejected. Nothing clears this but a human
 *   replacing the credential, which is why this one IS a blocking health item.
 *
 * "Permanently disabled" is also a DIFFERENT CONCEPT from the `enabled` column
 * beside it: that is configuration an operator chose, this is a credential
 * failure they did not. They are rendered in separate cells with separate
 * wording so a surface showing both cannot conflate them.
 */
const RUNTIME_STATE_LABELS: Record<string, string> = {
  Healthy: 'Healthy',
  Budgeted: 'Budgeted',
  BackingOff: 'Backing off',
  PermanentlyDisabled: 'Permanently disabled',
};

/**
 * Falls back to the raw enum name rather than to a generic "unavailable", on the
 * same reasoning as `outcomeLabel`: a fifth server state's own name tells an
 * operator more than a collapsed verdict, and it is safe to render because it is
 * a closed enum name and never free text.
 */
const runtimeStateLabel = (state: string): string => RUNTIME_STATE_LABELS[state] ?? state;

/**
 * "N used" against a cap, or against no cap at all.
 *
 * NULL IS UNLIMITED AND IS NOT ZERO. Rendering an unconfigured limit as "3 of 0"
 * or as a percentage is the exact collapse the server column's doc warns about,
 * and it would read as a source permanently over an allowance nobody set. There
 * is deliberately no `?? 0` anywhere in this function.
 */
const formatUsage = (used: number, limit: number | null): string =>
  limit === null ? `${used} used, unlimited` : `${used} of ${limit}`;

/** The editor's own state. Every field is a string while it is being typed. */
interface SourceDraft {
  kind: string;
  displayName: string;
  baseUrl: string;
  enabled: boolean;
  /**
   * A REPLACEMENT for the stored key, never the stored key itself — there is no
   * stored value to seed this from, by design. Empty means "the operator typed
   * nothing", which the request builders translate into an ABSENT field.
   */
  apiKey: string;
  /**
   * `'Proxy'` or `'Redirect'`. Seeded from the stored value on the edit form and
   * from `PROXY_ACCESS_MODE` on the add form — never left empty, because an
   * empty string is a 400 at the server and, more importantly, because there is
   * no "unset" state an operator should be able to leave this in: the column
   * always holds one of the two.
   */
  nzbAccessMode: string;
  /** arb-x7w8.1 — appended to `baseUrl` to reach the indexer's API. */
  apiPath: string;
  /**
   * The search weight, as typed. Held as a string like every other numeric
   * field here — see `timeoutSeconds` for why — but unlike the three below it
   * has no unset state: the column is non-nullable and `0` is an ordinary
   * weight, so an empty box here simply means the operator has not typed a
   * replacement for what is stored.
   */
  priority: string;
  /**
   * The per-source timeout in seconds, as typed, or `''`.
   *
   * A STRING AND NOT A `number | null`, and this is the load-bearing half of the
   * unlimited rule. A numeric draft field has to represent an empty box as
   * something, and every available candidate lies: `0` is a real cap of zero and
   * a real rejected timeout, `NaN` cannot survive a round trip, and `null`
   * cannot be told apart from "the operator cleared it" versus "there was never
   * a value". The raw string keeps the empty box EMPTY all the way to the
   * request builders, which is where the three-way decision actually belongs
   * because only they know what was stored before.
   */
  timeoutSeconds: string;
  /**
   * The query cap as typed, or `''` for NO CAP.
   *
   * NULL IS UNLIMITED AND IS NOT ZERO, restated here because this field is where
   * the two are most easily conflated: `''` and `'0'` are different strings and
   * must stay different all the way to the wire. `Number('')` is `0`, so any
   * builder that coerces this field without first testing for the empty string
   * turns every uncapped source into one capped at nothing.
   */
  queryLimit: string;
  /** The grab cap as typed, under exactly the same rule as `queryLimit`. */
  grabLimit: string;
  /** `'Hour'` or `'Day'` — the window both caps are counted over. */
  limitsUnit: string;
}

const BLANK_DRAFT: SourceDraft = {
  kind: NZBHYDRA_KIND,
  displayName: '',
  baseUrl: '',
  enabled: true,
  apiKey: '',
  /**
   * PROXY, NOT REDIRECT, and this line is the UI half of the owner's ship-OFF
   * ruling. A new source must never expose its indexer key because the operator
   * added it and touched nothing else; opting in has to be a deliberate act.
   */
  nzbAccessMode: PROXY_ACCESS_MODE,
  /**
   * EVERY TUNING FIELD STARTS EMPTY, and none of them is seeded with the
   * server's own default value.
   *
   * Reproducing `/api`, `0` and `Day` here would make this file a second copy of
   * `Source.cs`'s initialisers that the server could change out from under — and
   * the create builder sends only what was typed, so an empty box genuinely
   * takes the entity default rather than sending a guess at it. `limitsUnit` is
   * the one exception in appearance only: the select must show a value, so it
   * shows the same `'Day'` the column defaults to, and the builder sends it for
   * the same reason `nzbAccessMode` is always sent — a field the operator can
   * see is a field the submit must mean.
   */
  apiPath: '',
  priority: '',
  timeoutSeconds: '',
  queryLimit: '',
  grabLimit: '',
  limitsUnit: DAY_LIMITS_UNIT,
};

/**
 * Seeds the edit form from a source. Note that `apiKey` starts EMPTY and not
 * from any server value: `SourceSummary` has no field that could hold one, and
 * adding a masked or placeholder value here to "make the form feel complete"
 * would mean either inventing a value or round-tripping a real one through the
 * browser. Both are the thing the write-only contract exists to prevent.
 */
/**
 * A stored nullable column as the text of its input box.
 *
 * NULL BECOMES `''` AND NEVER `'0'`. This is the read half of the unlimited
 * rule: a stored null is no cap at all, so its box is empty, and a stored `0` is
 * a real cap of zero, so its box reads `0`. `String(limit ?? '')` would be the
 * same thing written shorter, but it puts a `??` on a nullable limit — the
 * spelling `formatUsage`'s doc bans for being one careless edit away from
 * `?? 0`, which is the collapse itself. Testing for null explicitly leaves
 * nowhere for that edit to land.
 */
const numberFieldValue = (stored: number | null): string =>
  stored === null ? '' : String(stored);

const draftOf = (source: SourceSummary): SourceDraft => ({
  kind: source.kind,
  displayName: source.displayName,
  baseUrl: source.baseUrl,
  enabled: source.enabled,
  apiKey: '',
  // Unlike apiKey, this DOES seed from the server value: it is not a secret, it
  // has no write-only contract, and showing the stored mode is the whole point
  // of putting the control on the edit form.
  nzbAccessMode: source.nzbAccessMode,
  // The tuning fields all seed from the server value, like nzbAccessMode and
  // unlike apiKey: none is a secret and none has a write-only contract. The
  // three nullable ones go through `numberFieldValue`, so a stored null arrives
  // as an EMPTY box that the update builder will read back as "unlimited" —
  // never as a `0` the operator never typed.
  apiPath: source.apiPath,
  priority: String(source.priority),
  timeoutSeconds: numberFieldValue(source.timeoutSeconds),
  queryLimit: numberFieldValue(source.queryLimit),
  grabLimit: numberFieldValue(source.grabLimit),
  limitsUnit: source.limitsUnit,
});

/**
 * The create body. `apiKey` is included only when something was typed, so a
 * source added without a key is created without one rather than with an empty
 * string masquerading as a credential.
 */
function toCreateRequest(draft: SourceDraft): CreateSourceRequest {
  const request: CreateSourceRequest = {
    kind: draft.kind,
    displayName: draft.displayName,
    baseUrl: draft.baseUrl,
    enabled: draft.enabled,
    // Always sent, unlike apiKey: the form showed the operator a mode, so the
    // save must mean it rather than leaning on the server default happening to
    // agree with the control's initial value.
    nzbAccessMode: draft.nzbAccessMode,
    // Same reasoning, and the select always holds one of the two values.
    limitsUnit: draft.limitsUnit,
  };
  if (draft.apiKey !== '') {
    request.apiKey = draft.apiKey;
  }
  // arb-x7w8.1 — the typed-only fields. EACH IS ADDED ONLY WHEN NON-EMPTY, and
  // an empty one is left off the body entirely rather than sent as `0`. For
  // `apiPath` and `priority` that means the entity default applies; for the two
  // limits and the timeout it means the column's NULL applies, which is
  // UNLIMITED for the caps and "use the global default" for the timeout.
  //
  // The `!== ''` test before every Number() is the load-bearing part and not a
  // tidy-up-able guard clause: `Number('')` is `0`, so a builder that coerced
  // unconditionally would send `queryLimit: 0` for every source the operator
  // left uncapped — storing a cap of nothing on a source that was meant to have
  // no cap at all.
  if (draft.apiPath !== '') {
    request.apiPath = draft.apiPath;
  }
  if (draft.priority !== '') {
    request.priority = Number(draft.priority);
  }
  if (draft.timeoutSeconds !== '') {
    request.timeoutSeconds = Number(draft.timeoutSeconds);
  }
  if (draft.queryLimit !== '') {
    request.queryLimit = Number(draft.queryLimit);
  }
  if (draft.grabLimit !== '') {
    request.grabLimit = Number(draft.grabLimit);
  }
  return request;
}

/**
 * The edit body, and the single most dangerous function in this file.
 *
 * `kind`, `displayName` and `baseUrl` are ALWAYS sent, even unchanged: omitting
 * one makes it `string.Empty` server-side and the update is then rejected as a
 * validation failure. `apiKey` is sent ONLY when the operator typed a
 * replacement, because for that one field omission means "leave the stored key
 * alone" and sending an empty string would blank a working credential on an
 * unrelated edit. Three fields where absence is fatal, one where presence is —
 * see `UpdateSourceRequest`'s doc for why the contract is shaped that way.
 *
 * `nzbAccessMode` is a FOURTH case and is always sent. Server-side its absence
 * means "leave the stored mode alone", so omitting it would be safe in the
 * `apiKey` sense — but the form always DISPLAYS a mode, and a displayed value
 * that a save does not carry is a value that can silently diverge from what is
 * stored. For this column that divergence is the difference between the indexer
 * key staying server-side and being handed to the client.
 *
 * `apiPath`, `priority` and `limitsUnit` are a FIFTH case that behaves like
 * `nzbAccessMode`: absence would leave the stored value alone, but the form
 * displays all three, so all three are sent.
 *
 * THE THREE NULLABLE COLUMNS ARE THE SIXTH AND THE DANGEROUS ONE, and they are
 * why this function now needs `stored` as well as `draft`. For them the wire has
 * three states where the fields above have two, and the draft alone cannot tell
 * them apart — an empty box means "there was never a value" or "the operator
 * just emptied it", and those are a no-op and a destructive write respectively.
 * Comparing against what the server last said is the only thing that separates
 * them. Per column:
 *
 * - box empty, stored already null — SEND NOTHING. Neither the value nor the
 *   flag. There is nothing to change, and a clear flag here would be a write
 *   where the operator made no edit.
 * - box empty, stored had a value — send `clear*: true` and NO value. This is
 *   the only spelling of "make this unlimited again", and the reason it exists
 *   at all: `0` would store a cap of zero, which is a source that can never be
 *   searched rather than one with no cap.
 * - box has text — send the number. A typed `0` IS sent as `0`, deliberately:
 *   it is a real cap of zero and the server decides whether to accept it. This
 *   is the one place `0` is a legitimate value on these columns, and it arrives
 *   only because somebody typed it.
 *
 * The timeout follows the same three states even though its null means "fall
 * back to the global default" rather than "unlimited": the shape of the contract
 * is what is shared, not the meaning of the null.
 */
function toUpdateRequest(draft: SourceDraft, stored: SourceSummary): UpdateSourceRequest {
  const request: UpdateSourceRequest = {
    kind: draft.kind,
    displayName: draft.displayName,
    baseUrl: draft.baseUrl,
    enabled: draft.enabled,
    nzbAccessMode: draft.nzbAccessMode,
    apiPath: draft.apiPath,
    limitsUnit: draft.limitsUnit,
  };
  if (draft.apiKey !== '') {
    request.apiKey = draft.apiKey;
  }
  // Priority is sent whenever the box holds anything, and that is nearly always
  // — `draftOf` seeds it from the stored weight, so it is empty only if the
  // operator deleted the digits. That case sends nothing rather than `0`: an
  // emptied box is an unfinished edit, and `Number('')` being `0` would turn it
  // into a real write demoting the source to the lowest weight. Priority has no
  // clear flag because it has no null state to return to, so "leave it alone"
  // is the only safe reading of an empty box.
  if (draft.priority !== '') {
    request.priority = Number(draft.priority);
  }
  if (draft.timeoutSeconds !== '') {
    request.timeoutSeconds = Number(draft.timeoutSeconds);
  } else if (stored.timeoutSeconds !== null) {
    request.clearTimeoutSeconds = true;
  }
  if (draft.queryLimit !== '') {
    request.queryLimit = Number(draft.queryLimit);
  } else if (stored.queryLimit !== null) {
    request.clearQueryLimit = true;
  }
  if (draft.grabLimit !== '') {
    request.grabLimit = Number(draft.grabLimit);
  } else if (stored.grabLimit !== null) {
    request.clearGrabLimit = true;
  }
  return request;
}

function SourceForm({
  draft,
  onChange,
  onSubmit,
  onCancel,
  submitLabel,
  busy,
  idPrefix,
  hasApiKey,
}: {
  draft: SourceDraft;
  onChange: (draft: SourceDraft) => void;
  onSubmit: () => void;
  onCancel?: () => void;
  submitLabel: string;
  busy: boolean;
  /** Namespaces the field ids so the add and edit forms can coexist on the page. */
  idPrefix: string;
  /**
   * Whether a key is already stored, for the edit form's wording. `undefined` on
   * the add form, where there is nothing to replace.
   */
  hasApiKey?: boolean;
}) {
  const set = <K extends keyof SourceDraft>(name: K, value: SourceDraft[K]) =>
    onChange({ ...draft, [name]: value });

  const keyLabel =
    hasApiKey === true ? 'Replace API key' : hasApiKey === false ? 'Set API key' : 'API key';

  return (
    <form
      className={styles.form}
      onSubmit={(event) => {
        event.preventDefault();
        onSubmit();
      }}
    >
      <label className={styles.field}>
        Kind
        {/*
          arb-x7w8.16 — A PICKER, AND NO FREE-TEXT PATH SURVIVES. The server
          matches this value ordinally against exactly three spellings and
          rejects everything else, so a text box could only ever produce a
          correct value by the operator already knowing the answer. See `KINDS`.

          A stored value outside the three is shown AS-IS via the extra option
          below rather than silently rewritten to the first of them. Such a row
          should not exist — every write path validates — but if one does, an
          operator who opens the form must see what is actually stored, and a
          select that quietly re-selected `NzbHydra` would make an unrelated
          save rewrite a column nobody looked at.
        */}
        <select
          className={styles.input}
          aria-label={`${idPrefix} kind`}
          value={draft.kind}
          onChange={(event) => set('kind', event.target.value)}
        >
          {KINDS.map((kind) => (
            <option key={kind} value={kind}>
              {KIND_LABELS[kind] ?? kind}
            </option>
          ))}
          {!KINDS.includes(draft.kind as (typeof KINDS)[number]) && (
            <option value={draft.kind}>{draft.kind}</option>
          )}
        </select>
      </label>
      <label className={styles.field}>
        Display name
        <input
          className={styles.input}
          aria-label={`${idPrefix} display name`}
          value={draft.displayName}
          onChange={(event) => set('displayName', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        Base URL
        <input
          className={styles.input}
          aria-label={`${idPrefix} base URL`}
          value={draft.baseUrl}
          onChange={(event) => set('baseUrl', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        API path
        {/*
          Appended to the base URL to reach the indexer's API. Empty on the add
          form means the server's own default, which is why there is no `/api`
          placeholder value seeded into the draft: showing the default as if the
          operator had typed it would send it, and this file would then be a
          copy of the entity initialiser. The server rejects an empty stored
          value and one carrying a query string; both rejections render below.
        */}
        <input
          className={styles.input}
          aria-label={`${idPrefix} API path`}
          placeholder="/api"
          value={draft.apiPath}
          onChange={(event) => set('apiPath', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        {keyLabel}
        <input
          // Password type so the value the operator types is not shoulder-read
          // and is not offered to a browser's plain-text autofill history. The
          // value lives in component state for the life of the form and is sent
          // to the server; it is never written to localStorage, sessionStorage,
          // a query string, or the query cache.
          type="password"
          className={styles.input}
          autoComplete="new-password"
          aria-label={`${idPrefix} API key`}
          value={draft.apiKey}
          onChange={(event) => set('apiKey', event.target.value)}
        />
      </label>
      {/*
        arb-x7w8.14 — THE EXPOSURE WARNING, AND IT IS A CO-REQUIREMENT OF THE
        SERVER ACCEPTING 'Redirect' AT ALL. `SourceRepository` refused this value
        outright until this control existed, because the owner's ruling is that
        the mode ships OFF and per-indexer opt-in AND that the operator is told
        what it costs. SettingsCatalog's discipline applied to a source column:
        an operator is never shown a bare, unexplained value.

        THE WARNING IS ASSOCIATED WITH THE CONTROL VIA aria-describedby, not
        merely placed next to it. A warning a screen reader never reaches while
        the select has focus is decoration; this one is announced with the
        control, which is what makes the opt-in informed for every operator
        rather than only the sighted ones.

        RENDERED ONLY WHEN Redirect IS SELECTED. A permanent warning beside a
        setting that is safe by default trains an operator to ignore it, and it
        would then be ignored at the one moment it matters.
      */}
      <label className={styles.field}>
        NZB access mode
        <select
          className={styles.input}
          aria-label={`${idPrefix} nzb access mode`}
          aria-describedby={
            draft.nzbAccessMode === REDIRECT_ACCESS_MODE ? `${idPrefix}-access-mode-warning` : undefined
          }
          value={draft.nzbAccessMode}
          onChange={(event) => set('nzbAccessMode', event.target.value)}
        >
          {ACCESS_MODES.map((mode) => (
            <option key={mode} value={mode}>
              {ACCESS_MODE_LABELS[mode] ?? mode}
            </option>
          ))}
        </select>
      </label>
      {draft.nzbAccessMode === REDIRECT_ACCESS_MODE && (
        <p id={`${idPrefix}-access-mode-warning`} className={local.accessModeWarning} role="note">
          This indexer&rsquo;s API key will be visible to Sonarr, Radarr and anything else that can
          read the download response: Arbitarr answers with a redirect to the indexer&rsquo;s own
          URL, and that URL contains the key. Redirect mode saves Arbitarr the download bandwidth.
          Choose Proxy to keep the key on the server.
        </p>
      )}
      <label className={styles.field}>
        Priority
        {/*
          The search weight. No `min`, because the column is a plain signed int
          and a negative weight is a legitimate way to push a source last -- an
          attribute narrowing it here would be a client-side rule the server
          does not have, and this form deliberately carries none of those.
        */}
        <input
          type="number"
          className={styles.input}
          aria-label={`${idPrefix} priority`}
          value={draft.priority}
          onChange={(event) => set('priority', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        Timeout (seconds)
        {/*
          THE ONLY CLIENT-SIDE VALIDATION IN THIS FORM IS THIS PAIR OF NATIVE
          ATTRIBUTES, and they mirror `SourceRepository.MinTimeoutSeconds` and
          `MaxTimeoutSeconds` rather than inventing a bound. The server REJECTS
          anything outside 1..30 and never clamps (AC24), and these attributes
          MIRROR that rule rather than being a second one: an out-of-range value
          is refused by the browser's own constraint validation before submit,
          so the operator is told at the control instead of after a round trip.

          THEY ARE THE ONLY CLIENT-SIDE VALIDATION HERE, and nothing above them
          re-checks. Every other rejection -- an empty API path, a query string
          in it, a bad base URL, a duplicate name -- reaches the wire and comes
          back as a 400 whose own message renders in the banner above. Adding a
          validation layer for those would replace the server's exact words with
          ours and put a second copy of each rule in the tree, one that drifts
          the moment the server's changes.

          Empty means fall back to the global default. That is a real stored
          state, not an absence, which is why clearing this box sends
          `clearTimeoutSeconds` rather than a value -- see `toUpdateRequest`.
        */}
        <input
          type="number"
          min={1}
          max={30}
          className={styles.input}
          aria-label={`${idPrefix} timeout seconds`}
          placeholder="Global default"
          value={draft.timeoutSeconds}
          onChange={(event) => set('timeoutSeconds', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        Query limit
        {/*
          EMPTY IS UNLIMITED AND IS NOT ZERO, stated in the placeholder because
          the distinction is invisible in an empty box and an operator typing 0
          to mean "no limit" would be configuring the opposite: a cap of nothing,
          on a source that can then never be searched. `min={0}` and not `min={1}`
          -- a cap of zero is a value the server accepts, and the form does not
          get to decide it is a mistake.
        */}
        <input
          type="number"
          min={0}
          className={styles.input}
          aria-label={`${idPrefix} query limit`}
          placeholder="Unlimited"
          value={draft.queryLimit}
          onChange={(event) => set('queryLimit', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        Grab limit
        {/* Same unlimited-is-empty rule as the query limit above. */}
        <input
          type="number"
          min={0}
          className={styles.input}
          aria-label={`${idPrefix} grab limit`}
          placeholder="Unlimited"
          value={draft.grabLimit}
          onChange={(event) => set('grabLimit', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        Limits window
        {/*
          The rolling window BOTH caps are counted over -- one setting for the
          two, matching the single `LimitsUnit` column, rather than a unit beside
          each box implying they can differ. Spelled exactly as the server
          accepts, like the kinds and the access modes.
        */}
        <select
          className={styles.input}
          aria-label={`${idPrefix} limits window`}
          value={draft.limitsUnit}
          onChange={(event) => set('limitsUnit', event.target.value)}
        >
          {LIMITS_UNITS.map((unit) => (
            <option key={unit} value={unit}>
              {LIMITS_UNIT_LABELS[unit] ?? unit}
            </option>
          ))}
        </select>
      </label>
      <label className={styles.checkboxField}>
        <input
          type="checkbox"
          aria-label={`${idPrefix} enabled`}
          checked={draft.enabled}
          onChange={(event) => set('enabled', event.target.checked)}
        />
        Enabled
      </label>
      <button type="submit" className={styles.button} disabled={busy}>
        {submitLabel}
      </button>
      {onCancel !== undefined && (
        <button type="button" className={styles.buttonSecondary} onClick={onCancel}>
          Cancel
        </button>
      )}
    </form>
  );
}

/**
 * #53 stage 53d — the Sources section, mounted into the Settings surface.
 *
 * <h3>Why this is a Settings SECTION and not a seventh nav surface</h3>
 * A sidebar entry was considered and rejected. `SidebarNav.tsx` carries an AC5
 * comment stating the surface count is seven and accounting for why; adding
 * Sources would make it eight and would put configuration in two places, since
 * everything else an operator configures already lives behind Settings. Sources
 * are configuration in exactly the sense the Settings catalog below them is, so
 * they belong on the same page. THE NAV COUNT IS THEREFORE UNCHANGED AT SEVEN,
 * and `SidebarNav.tsx`'s comment and `SidebarNav.test.tsx`'s exact-count
 * assertion are both deliberately untouched by this change.
 *
 * <h3>Scope: which upstream addresses are editable here</h3>
 * The owner's instruction (2026-09-07) is that every configurable upstream
 * address is settable from the settings pages, with a test button. Enumerated
 * from `Program.cs`'s `GetSection` calls at the time of writing:
 *
 * - `Arbitarr:Sources:NzbHydra` — a source. COVERED here: it is seeded as a
 *   `Source` row, and every row on this list is editable with a test button.
 * - `Arbitarr:Ai:Ollama` — an address, but NOT a source. DELIBERATELY NOT HERE.
 *   It is an AI backend rather than an indexer: it is not searched, it does not
 *   appear in the sources health table, it carries no Torznab/Newznab API key,
 *   and the connectivity probe on this page speaks the source API, so a shared
 *   test button would report `UnexpectedResponse` against a perfectly healthy
 *   Ollama. Putting it in the sources list would make "source" mean two
 *   different things in the same table. It has its own section with its own
 *   probe -- `Settings/Ai/Ai.tsx` (#89), which speaks Ollama's `/api/tags` and
 *   reports its own four outcomes; it is not silently skipped.
 * - `Arbitarr:Ai` and `Arbitarr:ClientApiKeys` — no upstream address between
 *   them (model/feature settings and locally-minted credentials respectively),
 *   so nothing to surface.
 */
export function SourcesSection() {
  const sources = useSourcesQuery();
  const create = useCreateSourceMutation();
  const update = useUpdateSourceMutation();
  const remove = useDeleteSourceMutation();
  const test = useTestSourceMutation();

  const [newDraft, setNewDraft] = useState<SourceDraft>(BLANK_DRAFT);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [editDraft, setEditDraft] = useState<SourceDraft>(BLANK_DRAFT);
  const [confirmingId, setConfirmingId] = useState<number | null>(null);
  const [testedId, setTestedId] = useState<number | null>(null);
  /**
   * The last write rejection, held HERE rather than read off the mutation.
   *
   * The mutations reset() on settle so no apiKey-bearing `variables` linger in
   * the MutationCache (see queries.ts). Holding the message here rather than
   * reading `create.error` / `update.error` is what makes that reset free of
   * consequence: reset() clears mutation `error`, so a banner sourced from it
   * would blank at the same instant the credential does. Component state keeps
   * the server's exact words on screen while the cached key still goes away
   * immediately.
   */
  const [writeError, setWriteError] = useState<string | null>(null);

  const editing = sources.data?.find((source) => source.id === editingId);
  const tested = sources.data?.find((source) => source.id === testedId);

  const startEditing = (source: SourceSummary) => {
    setEditingId(source.id);
    setEditDraft(draftOf(source));
  };

  const { settle } = useSecretEvictingMutation();

  /**
   * Settle handler shared by every write that can carry an apiKey.
   *
   * Delegates the capture-then-evict half to `useSecretEvictingMutation`
   * (arb-689) — see that module for why eviction must happen from here, a
   * per-call site, and never from a hook-level callback in `queries.ts`. This
   * function's own job is the part that hook cannot know: dropping the typed
   * key from BOTH forms on settle. The rest of a rejected draft is
   * deliberately kept so the operator can correct it and resubmit, but the
   * secret is not part of what needs correcting — they can retype it — and
   * holding it in component state (and therefore in the rendered input)
   * after the request has settled keeps a copy alive for no benefit.
   * Clearing it here is what makes the DOM sweep in the leak test true rather
   * than merely close.
   */
  const settleWrite = (mutation: { reset: () => void }, error: unknown) => {
    // `settle` hands back the RAW error — `writeError` here is a rendered
    // string, unlike ApiKeys.tsx's `createFailure`, which stays `unknown` and
    // is formatted at render time instead. Format it here, at the one place
    // this file turns an error into displayed text.
    settle(mutation, error, (captured) =>
      setWriteError(captured === null ? null : errorMessage(captured)),
    );
    setNewDraft((draft) => (draft.apiKey === '' ? draft : { ...draft, apiKey: '' }));
    setEditDraft((draft) => (draft.apiKey === '' ? draft : { ...draft, apiKey: '' }));
  };

  /**
   * Enable/disable from the row, without opening the editor. It still sends the
   * full body — `kind`, `displayName` and `baseUrl` included — because omitting
   * them is a validation failure, not a no-op. `apiKey` stays absent, which is
   * what keeps a toggle from wiping the stored credential.
   *
   * IT DELIBERATELY DOES NOT GO THROUGH `toUpdateRequest`, and every arb-x7w8.1
   * tuning field is absent here on purpose. This path shows the operator no form
   * and therefore no tuning value, so the argument that makes those fields
   * always-sent on the edit form — a displayed value the save must mean — does
   * not apply. Absence leaves each stored value alone, and no clear flag is sent
   * for the nullable three, so flipping Enabled cannot disturb a limit.
   */
  const toggleEnabled = (source: SourceSummary) =>
    update.mutate(
      {
        id: source.id,
        source: {
          kind: source.kind,
          displayName: source.displayName,
          baseUrl: source.baseUrl,
          enabled: !source.enabled,
        },
      },
      { onSettled: (_data, error) => settleWrite(update, error) },
    );

  return (
    <section className={styles.panel}>
      <div className={local.headingRow}>
        <h2 className={styles.panelHeading}>Sources</h2>
        {/* Same convention as the Maintenance interval setting's badge
            (Settings.tsx's SettingRow, entry.requiresRestart): sources are
            edited here but only take effect on the next restart, so the
            badge matches the copy directly below it rather than introducing
            a second wording for the same fact. A sibling of the <h2>, not a
            child -- nesting it inside would fold "Restart required" into the
            heading's accessible name alongside "Sources". */}
        <span className={`${styles.badge} ${styles.badgeWarn}`}>Restart required</span>
      </div>
      <div className={styles.panelBody}>
        <p className={styles.muted}>
          Indexer sources Arbitarr searches. These are stored in the database and take effect on
          the next restart; environment variables are read only to seed this list on a first run.
        </p>

        {/* arb-x7w8.11. The "Restart required" badge on the heading describes the
            CONFIGURATION columns, and a reader who carried that reading across
            would take Status, Queries and Grabs for three more values awaiting a restart —
            they are the opposite, changing continuously while the process runs.
            Saying so once here is cheaper than a second badge per row, and it
            also states what clears the one state nothing clears by itself. */}
        <p className={styles.muted}>
          Status, Queries and Grabs are live and refresh when this page is loaded, unlike the settings
          above them. A source that is backing off recovers on its own; one that is permanently
          disabled had its API key rejected and stays disabled until a corrected key succeeds.
        </p>

        {writeError !== null && (
          <p className={`${styles.error} ${local.writeError}`} role="alert">
            {writeError}
          </p>
        )}

        {/* The list's pending/error/503 treatment is QueryState's, not this
            file's (arb-z505): the four-arm ternary this replaces was a fourth
            private copy of it, and a copy is a place the shared treatment can
            be improved everywhere except here. The empty-list arm is NOT part
            of that triad — an empty list is a loaded state, so it stays a
            surface concern inside `children(data)`. */}
        <QueryState isPending={sources.isPending} error={sources.error} data={sources.data}>
          {(loaded) =>
            loaded.length === 0 ? (
              <p className={styles.empty}>
                No sources configured — add an NZBHydra2 base URL and API key below to start
                searching.
              </p>
            ) : (
              <div className={styles.tableScroll}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th>Kind</th>
                      <th>Name</th>
                      <th>Base URL</th>
                      <th>Enabled</th>
                      {/* arb-x7w8.11. Status is a SEPARATE column from Enabled rather
                          than merged into it, because the two answer different
                          questions: Enabled is configuration the operator chose,
                          Status is what the source is actually doing. An operator
                          seeing "Disabled" and "Permanently disabled" in one cell
                          could not tell which they had caused. Two new columns
                          rather than an expander: every value here is a short
                          badge or a count, so a row that has to be opened to read
                          one word costs more than the width it saves. */}
                      <th>Status</th>
                      <th>Queries</th>
                      <th>Grabs</th>
                      <th>API key</th>
                      <th>Created</th>
                      <th>Updated</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {loaded.map((source) => (
                      <tr key={source.id}>
                        <td>{source.kind}</td>
                        <td>{source.displayName}</td>
                        <td>
                          <code className={local.url}>{source.baseUrl}</code>
                        </td>
                        <td>
                          <span className={`${styles.badge} ${source.enabled ? styles.badgeOk : ''}`}>
                            {source.enabled ? 'Enabled' : 'Disabled'}
                          </span>
                        </td>
                        <td>
                          {/* arb-x7w8.11 — the derived runtime state, per row.
                              Healthy is the only affirmative badge; the three that
                              mean "not being searched right now" share the warning
                              treatment because the LABEL is what distinguishes them
                              and a third and fourth hue would compete with it.

                              This is LIVE state inside a panel captioned "Restart
                              required", which is why it carries its own caption
                              below the table saying so: the badge above that one
                              describes pending CONFIGURATION, and a reader who
                              carried that reading down here would take a backoff
                              for another value awaiting a restart. */}
                          <span
                            className={`${styles.badge} ${
                              source.runtimeState === 'Healthy' ? styles.badgeOk : styles.badgeWarn
                            }`}
                          >
                            {runtimeStateLabel(source.runtimeState)}
                          </span>
                          {/* Shown only while a hold-off is genuinely in force —
                              keyed off runtimeState and NEVER off `disabledUntil`
                              being non-null, which stays populated after the
                              hold-off has elapsed and would otherwise render every
                              recovered source as still waiting. */}
                          {source.runtimeState === 'BackingOff' && source.disabledUntil !== null && (
                            <span className={local.stateDetail}>
                              until {new Date(source.disabledUntil).toLocaleTimeString()} (level{' '}
                              {source.disabledLevel})
                            </span>
                          )}
                          {source.lastOutcome !== null && (
                            <span className={local.stateDetail}>Last: {source.lastOutcome}</span>
                          )}
                        </td>
                        {/* Usage against the cap. `formatUsage` holds the
                            null-is-unlimited rule; do not inline a `?? 0` here. */}
                        <td className={styles.muted}>
                          {formatUsage(source.queriesUsed, source.queryLimit)}
                        </td>
                        {/* Grabs alongside queries because EITHER allowance being
                            spent renders the single Budgeted badge above (see
                            SourceRuntimeStateReader.Derive). Without this column
                            that badge is unattributable: an operator seeing
                            Budgeted while queries read "3 of 50" has nothing on
                            the row explaining it. Same `formatUsage`, same
                            null-is-unlimited rule, still no `?? 0`. */}
                        <td className={styles.muted}>
                          {formatUsage(source.grabsUsed, source.grabLimit)}
                        </td>
                        <td>
                          {/* The entire read surface for the secret: a boolean the
                              server derived. There is no value here to reveal,
                              mask, or copy — SourceResponse has no field that could
                              carry one. */}
                          <span
                            className={`${styles.badge} ${source.hasApiKey ? styles.badgeOk : styles.badgeWarn}`}
                          >
                            {source.hasApiKey ? 'Configured' : 'Not configured'}
                          </span>
                        </td>
                        <td className={styles.muted} title={formatTimestampTitle(source.createdAt)}>
                          {formatTimestamp(source.createdAt)}
                        </td>
                        <td className={styles.muted} title={formatTimestampTitle(source.updatedAt)}>
                          {formatTimestamp(source.updatedAt)}
                        </td>
                        <td className={local.rowActions}>
                          <button
                            type="button"
                            className={styles.buttonSecondary}
                            onClick={() => {
                              setTestedId(source.id);
                              test.mutate(source.id);
                            }}
                            disabled={test.isPending}
                          >
                            Test
                          </button>
                          <button
                            type="button"
                            className={styles.buttonSecondary}
                            onClick={() => toggleEnabled(source)}
                            disabled={update.isPending}
                          >
                            {source.enabled ? 'Disable' : 'Enable'}
                          </button>
                          <button
                            type="button"
                            className={styles.buttonSecondary}
                            onClick={() => startEditing(source)}
                          >
                            Edit
                          </button>
                          {confirmingId === source.id ? (
                            <>
                              <button
                                type="button"
                                className={styles.buttonDanger}
                                onClick={() => {
                                  setConfirmingId(null);
                                  // remove carries only an id, never a key, so it
                                  // needs no gcTime/reset treatment — but its
                                  // rejection still has to reach the same banner.
                                  remove.mutate(source.id, {
                                    onSettled: (_data, error) =>
                                      setWriteError(
                                        error === null || error === undefined
                                          ? null
                                          : errorMessage(error),
                                      ),
                                  });
                                }}
                                disabled={remove.isPending}
                              >
                                Confirm remove
                              </button>
                              <button
                                type="button"
                                className={styles.buttonSecondary}
                                onClick={() => setConfirmingId(null)}
                              >
                                Keep
                              </button>
                            </>
                          ) : (
                            <button
                              type="button"
                              className={styles.buttonDanger}
                              onClick={() => setConfirmingId(source.id)}
                            >
                              Remove
                            </button>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )
          }
        </QueryState>

        {/* Removing a source deletes its stored key with it, and the key cannot
            be read back out to restore it — so the destructive action is
            two-step rather than a single click next to Edit. */}
        {confirmingId !== null && (
          <p className={local.confirm} role="alert">
            Removing this source also deletes its stored API key. The key cannot be recovered and
            will have to be entered again.
          </p>
        )}

        {/* One result region for the whole table, naming the source it belongs
            to. With several sources a bare verdict is ambiguous — the operator
            cannot tell which row it answered — and the probe is the one control
            here whose result is worth nothing if attributed to the wrong
            source. */}
        {tested !== undefined && (
          <div className={local.testResult}>
            {test.isPending ? (
              <p className={styles.muted}>Testing {tested.displayName}…</p>
            ) : test.error !== null && test.error !== undefined ? (
              <p className={styles.error} role="alert">
                {errorMessage(test.error)}
              </p>
            ) : test.data !== undefined ? (
              <p role="status">
                <strong>{tested.displayName}</strong>{' '}
                {/* Keyed off the outcome enum, not the `success` boolean, so the
                    badge cannot drift from the five outcomes: they are the
                    contract, and `success` is a second encoding of the same
                    fact that could disagree with it. */}
                <span
                  className={`${styles.badge} ${test.data.outcome === 'Ok' ? styles.badgeOk : styles.badgeDanger}`}
                >
                  {outcomeLabel(test.data.outcome)}
                </span>{' '}
                <span className={styles.muted}>{test.data.message}</span>
              </p>
            ) : null}
          </div>
        )}
      </div>

      {editing !== undefined && editingId !== null && (
        <div className={local.subPanel}>
          <h3 className={local.subHeading}>Edit source</h3>
          <p className={styles.muted}>
            {editing.hasApiKey
              ? 'An API key is stored for this source. It cannot be shown. Leave the key field empty to keep it, or type a new one to replace it.'
              : 'No API key is stored for this source. Type one to set it, or leave the field empty.'}
          </p>
          <SourceForm
            draft={editDraft}
            onChange={setEditDraft}
            idPrefix="Edit source"
            hasApiKey={editing.hasApiKey}
            submitLabel={update.isPending ? 'Saving…' : 'Save changes'}
            busy={update.isPending}
            onCancel={() => setEditingId(null)}
            onSubmit={() =>
              update.mutate(
                // `editing` is the row this form was seeded from, and
                // `toUpdateRequest` needs it: an empty limit box means "leave
                // the null alone" or "clear the stored value" depending on what
                // the server last said, and the draft alone cannot tell those
                // apart. See that function.
                { id: editingId, source: toUpdateRequest(editDraft, editing) },
                {
                  // The editor stays open on failure, holding what was typed, so
                  // the operator can correct it against the server's own reason.
                  onSuccess: () => setEditingId(null),
                  onSettled: (_data, error) => settleWrite(update, error),
                },
              )
            }
          />
        </div>
      )}

      <div className={local.subPanel}>
        <h3 className={local.subHeading}>Add source</h3>
        <SourceForm
          draft={newDraft}
          onChange={setNewDraft}
          idPrefix="New source"
          submitLabel={create.isPending ? 'Adding…' : 'Add source'}
          busy={create.isPending}
          onSubmit={() =>
            create.mutate(toCreateRequest(newDraft), {
              onSuccess: () => setNewDraft(BLANK_DRAFT),
              onSettled: (_data, error) => settleWrite(create, error),
            })
          }
        />
      </div>
    </section>
  );
}
