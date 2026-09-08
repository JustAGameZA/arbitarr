import { useState } from 'react';
import type { FormEvent } from 'react';

import { QueryState, errorMessage } from '../../QueryState';
import styles from '../../surface.module.css';
import local from './Sonarr.module.css';
import {
  useSonarrConfigQuery,
  useTestSonarrMutation,
  useUpdateSonarrConfigMutation,
} from './queries';
import type { ArrConfig } from './types';

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
 * substantive: Sonarr carries a key and a wrong key is the likeliest thing this
 * button exists to catch. Ollama has no authentication, so the same label there
 * would send an operator hunting for a key that does not exist.
 *
 * NONE OF THIS TEXT IS DERIVED FROM THE SERVER'S RESPONSE, and none of it can
 * be: the server's enum carries no string field, so there is no Sonarr response
 * body, exception message, configured address, or API KEY anywhere in the path
 * that produces it. Do not add a free-text field to carry a server message
 * through — that is the shape this prevents.
 */
const OUTCOME_LABELS: Record<string, string> = {
  Ok: 'Connected',
  Unreachable: 'Unreachable',
  TlsFailure: 'TLS failure',
  AuthenticationFailed: 'Key rejected',
  UnexpectedResponse: 'Not Sonarr',
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
 * The Sonarr configuration form.
 *
 * <b>THE TWO FIELDS FOLLOW OPPOSITE IDIOMS, DELIBERATELY.</b>
 *
 * THE BASE URL IS SEEDED FROM THE SERVER AND EDITED IN PLACE, like the AI
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
 * only by deleting the thing that owns it. `DELETE /api/admin/arr/sonarr`
 * unconfigures the whole instance, and surfacing that is a destructive
 * whole-section action wanting the two-step confirmation the Notifications
 * section uses — its own piece of work, not a button added in passing.
 *
 * `saved` and `rejection` are NOT held here. A successful save invalidates the
 * config, the refetch changes the key this form is mounted under, and the
 * remount would destroy any outcome state owned by this component — so "Saved."
 * would only ever survive a save that changed nothing. They live in
 * `SonarrSection`, above that boundary, and arrive as props. The probe result IS
 * held here, and losing it on a save is correct: it described the configuration
 * before the save, and the operator can ask again in one click.
 */
function SonarrForm({
  config,
  saved,
  setSaved,
  rejection,
  setRejection,
}: {
  config: ArrConfig;
  saved: boolean;
  setSaved: (saved: boolean) => void;
  rejection: string | null;
  setRejection: (rejection: string | null) => void;
}) {
  const update = useUpdateSonarrConfigMutation();
  const test = useTestSonarrMutation();

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
            : 'No key is stored. Series titles cannot be resolved until one is set.'}
        </span>
      </p>

      <label className={styles.field}>
        <span>Base URL</span>
        <input
          className={local.urlInput}
          type="url"
          inputMode="url"
          placeholder="http://sonarr.example.invalid:8989"
          value={baseUrl}
          onChange={(event) => setBaseUrl(event.target.value)}
        />
      </label>

      <label className={styles.field}>
        {/* The label states what typing here DOES, because the field cannot show
            what is stored. "Replace API key" beside a stored key and "Set API
            key" beside none — the same wording the Sources section uses, for the
            same reason: an empty field next to a configured source otherwise
            reads as "no key". */}
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
          Sources surface never offers. DELETE /api/admin/arr/sonarr unconfigures
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
                completed is not a Sonarr that answered, and the closed outcome
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
        The test asks Sonarr for its system status at /api/v3/system/status using the stored address
        and key, so save an edit before testing it.
      </p>
    </form>
  );
}

/**
 * arb-u1c: the Sonarr section of the Settings surface.
 *
 * <h3>What it is for</h3>
 * Sonarr's anime episode searches arrive as a tvdbid plus a bare absolute episode
 * number, which NZBHydra2 cannot honour as a pair. Resolving that id to the
 * series' actual title requires asking Sonarr itself — the authoritative source
 * for numbering it has already reconciled — and that needs an address and a key.
 *
 * <h3>Why its own section rather than a row in Sources</h3>
 * Sonarr is not a source. A source is SEARCHED — it is an indexer, it appears in
 * the sources health table, it carries a Torznab/Newznab API key, and its probe
 * speaks the source API. Sonarr is none of those: it is asked what a series is
 * called, and probing it means asking for its system status. A shared test button
 * would report `UnexpectedResponse` against a perfectly healthy Sonarr. The same
 * distinction `Sources.tsx` records for the AI backend applies here unchanged.
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
 * a value, this one genuinely starts unconfigured — there is no default Sonarr to
 * assume. The form renders regardless, with the badge saying so, because the
 * whole point is to be able to fill it in.
 */
export function SonarrSection() {
  const config = useSonarrConfigQuery();

  // Owned HERE, above the remount boundary below, for the same reason the
  // catalog's `savedKey` and the AI section's `saved` are: a successful save
  // invalidates the config and the refetch changes the form's key, so state held
  // inside the form is destroyed by the very save it is reporting on.
  const [saved, setSaved] = useState(false);
  const [rejection, setRejection] = useState<string | null>(null);

  return (
    <section className={styles.panel}>
      <h2 className={styles.panelHeading}>Sonarr</h2>
      <div className={styles.panelBody}>
        <p className={styles.muted}>
          Used to resolve a TVDB id to the series&rsquo; title, so an anime episode search carries
          the title upstream instead of a bare episode number.
        </p>
        <QueryState isPending={config.isPending} error={config.error} data={config.data}>
          {(data) => (
            // Remounted whenever the server's configuration changes, so a
            // successful save reseeds the address from the server rather than
            // leaving a stale local edit — the same reason the catalog rows key
            // on their value. `hasApiKey` is part of the key so that setting or
            // clearing a key reseeds the labels too; the key FIELD is never
            // seeded, it simply returns to empty on remount, which is correct.
            <SonarrForm
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
