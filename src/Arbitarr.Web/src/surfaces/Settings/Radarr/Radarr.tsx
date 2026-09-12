import { useState } from 'react';
import type { FormEvent } from 'react';

import { QueryState, errorMessage } from '../../QueryState';
import styles from '../../surface.module.css';
import local from './Radarr.module.css';
import {
  useRadarrConfigQuery,
  useTestRadarrMutation,
  useUpdateRadarrConfigMutation,
} from './queries';
import type { RadarrConfig } from './types';

/**
 * The operator-facing label for each probe outcome, keyed by the server's closed
 * `SourceProbeOutcome` enum.
 *
 * FIVE DISTINCT LABELS, NOT ONE RED "FAILED". The four failure modes have
 * entirely different fixes — a wrong port, an expired certificate, a rejected key
 * and a base URL pointing at some other service are not the same problem — so
 * collapsing them into a single verdict makes the button decorative. The server's
 * longer `message` is rendered underneath and says what to check next; this is
 * the short badge that makes them scannable at a glance.
 *
 * THERE IS AN "API KEY REJECTED" HERE, unlike the AI section, and that is
 * substantive: Radarr carries a key and a wrong key is the likeliest thing this
 * button exists to catch. Ollama has no authentication, so the same label there
 * would send an operator hunting for a key that does not exist.
 *
 * NONE OF THIS TEXT IS DERIVED FROM THE SERVER'S RESPONSE, and none of it can
 * be: the server's enum carries no string field, so there is no Radarr response
 * body, exception message, configured address, or API KEY anywhere in the path
 * that produces it. Do not add a free-text field to carry a server message
 * through — that is the shape this prevents.
 */
const OUTCOME_LABELS: Record<string, string> = {
  Ok: 'Connected',
  Unreachable: 'Unreachable',
  TlsFailure: 'TLS failure',
  AuthenticationFailed: 'Key rejected',
  UnexpectedResponse: 'Not Radarr',
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
 * The Radarr configuration form.
 *
 * <b>THE TWO FIELDS FOLLOW OPPOSITE IDIOMS, DELIBERATELY.</b>
 *
 * THE BASE URL IS SEEDED FROM THE SERVER AND EDITED IN PLACE, like the Sonarr
 * section's address: it is not a credential, the server serves the value back,
 * and it rejects a URL containing userinfo so that stays true. An operator can
 * therefore see and correct the address rather than retyping it blind.
 *
 * THE API KEY STARTS EMPTY AND IS LABELLED AS REPLACING WHAT IS STORED, like the
 * source-key and webhook fields: its value is a credential the client must never
 * hold, so there is nothing to seed it from. Leaving it blank sends no `apiKey`
 * property at all, which the server reads as "leave the stored key alone" — NOT
 * as "clear it". Conflating the two would let an ordinary address edit silently
 * destroy a working credential.
 *
 * THERE IS NO CLEAR AFFORDANCE HERE AT ALL (ADR 0010; bead arb-c26). A key-only
 * clear does not exist on the server: under the shared rule a secret is cleared
 * only by deleting the thing that owns it. `DELETE /api/admin/arr/radarr`
 * unconfigures the whole instance, and surfacing that is a destructive
 * whole-section action wanting the two-step confirmation the Notifications
 * section uses — its own piece of work, not a button added in passing.
 *
 * `saved` and `rejection` are NOT held here. A successful save invalidates the
 * config, the refetch changes the key this form is mounted under, and the
 * remount would destroy any outcome state owned by this component — so "Saved."
 * would only ever survive a save that changed nothing. They live in
 * `RadarrSection`, above that boundary, and arrive as props. The probe result IS
 * held here, and losing it on a save is correct: it described the configuration
 * before the save, and the operator can ask again in one click.
 */
function RadarrForm({
  config,
  saved,
  setSaved,
  rejection,
  setRejection,
}: {
  config: RadarrConfig;
  saved: boolean;
  setSaved: (saved: boolean) => void;
  rejection: string | null;
  setRejection: (rejection: string | null) => void;
}) {
  const update = useUpdateRadarrConfigMutation();
  const test = useTestRadarrMutation();

  const [baseUrl, setBaseUrl] = useState(config.baseUrl ?? '');
  // Starts EMPTY and is never seeded from `config` — there is nothing to seed it
  // from. See the type doc.
  const [apiKey, setApiKey] = useState('');

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setSaved(false);
    setRejection(null);

    // Sent as typed. No client-side URL check of our own: the server owns
    // validation, rejects rather than clamps, and states its own reason.
    //
    // `apiKey` is included ONLY when the operator typed one. An empty string
    // would be sent as a replacement and rejected by the server as a blank
    // credential; omitting the property is what means "leave the stored key
    // alone", and JSON.stringify drops an undefined property, which is exactly
    // the behaviour wanted.
    const request = apiKey === '' ? { baseUrl } : { baseUrl, apiKey };

    update.mutate(request, {
      // Captured from the PER-CALL callbacks into component state, which is the
      // shape that is correct by construction — such a callback runs after
      // delivery by definition, so there is nothing to sequence around.
      onSuccess: () => {
        setSaved(true);
        // The typed key is dropped from component state the moment it is
        // delivered. The mutation's own copy in the MutationCache is dropped by
        // gcTime: 0 plus the reset() below — component state alone is not enough,
        // because the cache is a second copy.
        setApiKey('');
      },
      onError: (error) => setRejection(errorMessage(error)),
      // Drops the settled mutation — `variables` included, which is where the
      // plaintext key would otherwise linger — immediately rather than at gc.
      onSettled: () => update.reset(),
    });
  };

  return (
    <form className={local.form} onSubmit={submit}>
      <p className={local.status}>
        <span
          className={`${styles.badge} ${config.hasApiKey ? styles.badgeOk : styles.badgeWarn}`}
        >
          {config.hasApiKey ? 'Key configured' : 'No key'}
        </span>
        <span className={styles.muted}>
          {config.hasApiKey
            ? 'A key is stored. It is never shown or sent back — type a new one only to replace it.'
            : 'No key is stored. The movie library and queue cannot be read until one is set.'}
        </span>
      </p>

      <label className={styles.field}>
        <span>Base URL</span>
        <input
          className={local.urlInput}
          type="url"
          inputMode="url"
          placeholder="http://radarr.example.invalid:7878"
          value={baseUrl}
          onChange={(event) => setBaseUrl(event.target.value)}
        />
      </label>

      <label className={styles.field}>
        {/* The label states what typing here DOES, because the field cannot show
            what is stored. "Replace API key" beside a stored key and "Set API
            key" beside none — the same wording the Sources and Sonarr sections
            use, for the same reason: an empty field next to a configured
            instance otherwise reads as "no key". */}
        <span>{config.hasApiKey ? 'Replace API key' : 'Set API key'}</span>
        <input
          className={styles.input}
          type="password"
          autoComplete="new-password"
          placeholder={config.hasApiKey ? 'Leave blank to keep the stored key' : ''}
          value={apiKey}
          onChange={(event) => setApiKey(event.target.value)}
        />
      </label>

      <div className={local.actions}>
        <button className={styles.button} type="submit" disabled={update.isPending}>
          {update.isPending ? 'Saving…' : 'Save'}
        </button>

        <button
          className={styles.buttonSecondary}
          type="button"
          onClick={() => {
            setSaved(false);
            setRejection(null);
            test.mutate();
          }}
          disabled={test.isPending}
        >
          {test.isPending ? 'Testing…' : 'Test connection'}
        </button>

        {/*
          NO CLEAR BUTTON, DELIBERATELY (ADR 0010; bead arb-c26). A key-only clear
          does not exist on the server — under the shared rule a secret is cleared
          only by deleting the thing that owns it, and clearing this key alone
          would leave an address with no credential, the half-configured state the
          Sources surface never offers. DELETE /api/admin/arr/radarr unconfigures
          the whole instance and is deliberately not surfaced here yet: it is a
          destructive whole-section action and wants the two-step confirmation the
          Notifications section uses, which is its own piece of work rather than a
          button added in passing.
        */}

        {saved && <span className={styles.success}>Saved.</span>}
      </div>

      {rejection !== null && (
        <p className={styles.error} role="alert">
          {rejection}
        </p>
      )}

      {(test.isSuccess || test.isError) && (
        <div className={local.testResult}>
          {test.isSuccess && (
            <>
              <p className={local.testHead}>
                <span
                  className={`${styles.badge} ${test.data.success ? styles.badgeOk : styles.badgeDanger}`}
                >
                  {outcomeLabel(test.data.outcome)}
                </span>
              </p>
              {/*
                The server's own wording for the outcome it actually got. Success
                is claimed only where the server said `success`: a request that
                completed is not a Radarr that answered, and the closed outcome
                enum exists so the client never has to guess which it was.
              */}
              <p
                className={test.data.success ? styles.success : styles.error}
                role={test.data.success ? undefined : 'alert'}
              >
                {test.data.message}
              </p>
            </>
          )}

          {/*
            The request itself failed — rejected admin key, server down. That is
            NOT a probe outcome and must not be dressed as one, so it shows the
            transport error and no outcome badge at all.
          */}
          {test.isError && (
            <p className={styles.error} role="alert">
              {errorMessage(test.error)}
            </p>
          )}
        </div>
      )}

      <p className={local.hint}>
        The test asks Radarr for its system status at /api/v3/system/status using the stored address
        and key, so save an edit before testing it.
      </p>
    </form>
  );
}

/**
 * arb-6l9b.2: the Radarr section of the Settings surface.
 *
 * <h3>What it is for</h3>
 * The Library screen's movie list and download queue are read from Radarr
 * directly (arb-arrq beads 3 and 4), and reaching it needs an address and a key.
 * This section is where both are configured.
 *
 * <h3>Why its own section rather than a row in Sources</h3>
 * Radarr is not a source. A source is SEARCHED — it is an indexer, it appears in
 * the sources health table, it carries a Torznab/Newznab API key, and its probe
 * speaks the source API. Radarr is none of those: it is asked what it holds and
 * what it is downloading, and probing it means asking for its system status. A
 * shared test button would report `UnexpectedResponse` against a perfectly
 * healthy Radarr. The same distinction `Sources.tsx` records for the AI backend,
 * and `Sonarr.tsx` for Sonarr, applies here unchanged.
 *
 * <h3>Why a section of its own rather than a Sonarr field</h3>
 * The server keeps `RadarrConfigResponse` a separate record from
 * `ArrConfigResponse` even though the shapes match today (arb-arrq D3), and
 * Radarr is deliberately OUT of identity resolution (D4) — `ArrApiProvider` stays
 * Sonarr-only. Folding the two instances into one form would join two contracts
 * that the server keeps apart on purpose, and would make one save write both.
 *
 * <h3>Why not a row on the settings catalog below</h3>
 * Both reasons this codebase already has apply at once. The KEY is off the
 * catalog because publishing it would leak a secret — its row name is
 * colon-namespaced, which no `SettingKey` can produce, so that is structural
 * rather than merely intended. The BASE URL is off it because it needs an
 * affordance a generic catalog row cannot provide (a connectivity probe), and an
 * entry there would render the address a second time as an unexplained text field
 * beside this section.
 *
 * <h3>The empty state is real here</h3>
 * Unlike the AI section, which is seeded on first start and therefore always has
 * a value, this one genuinely starts unconfigured — there is no default Radarr to
 * assume, and an installation with no Radarr at all is a supported shape. The
 * form renders regardless, with the badge saying so, because the whole point is
 * to be able to fill it in.
 */
export function RadarrSection() {
  const config = useRadarrConfigQuery();

  // Owned HERE, above the remount boundary below, for the same reason the
  // catalog's `savedKey` and the Sonarr section's `saved` are: a successful save
  // invalidates the config and the refetch changes the form's key, so state held
  // inside the form is destroyed by the very save it is reporting on.
  const [saved, setSaved] = useState(false);
  const [rejection, setRejection] = useState<string | null>(null);

  return (
    <section className={styles.panel}>
      <h2 className={styles.panelHeading}>Radarr</h2>
      <div className={styles.panelBody}>
        <p className={styles.muted}>
          Used to read the movie library and the download queue, so the Library screen can show what
          Radarr holds and what it is fetching.
        </p>
        <QueryState isPending={config.isPending} error={config.error} data={config.data}>
          {(data) => (
            // Remounted whenever the server's configuration changes, so a
            // successful save reseeds the address from the server rather than
            // leaving a stale local edit — the same reason the catalog rows key
            // on their value. `hasApiKey` is part of the key so that setting or
            // clearing a key reseeds the labels too; the key FIELD is never
            // seeded, it simply returns to empty on remount, which is correct.
            <RadarrForm
              key={`${data.baseUrl ?? ''} ${data.hasApiKey}`}
              config={data}
              saved={saved}
              setSaved={setSaved}
              rejection={rejection}
              setRejection={setRejection}
            />
          )}
        </QueryState>
      </div>
    </section>
  );
}
