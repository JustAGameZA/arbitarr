import { useState } from 'react';
import type { FormEvent } from 'react';

import { QueryState, errorMessage } from '../../QueryState';
import styles from '../../surface.module.css';
import local from './Ai.module.css';
import {
  useOllamaConfigQuery,
  useTestOllamaMutation,
  useUpdateOllamaConfigMutation,
} from './queries';
import type { OllamaConfig } from './types';

/**
 * The operator-facing label for each probe outcome, keyed by the server's closed
 * `OllamaProbeOutcome` enum.
 *
 * FOUR DISTINCT LABELS, NOT ONE RED "FAILED". The three failure modes have
 * entirely different fixes — a wrong port, an expired certificate, and a base URL
 * pointing at some other service are not the same problem — so collapsing them
 * into a single verdict makes the button decorative. The server's longer
 * `message` is rendered underneath and says what to check next; this is the short
 * badge that makes them scannable at a glance.
 *
 * THERE IS NO "API KEY REJECTED" HERE, and adding one to match the Sources
 * section would be wrong: Ollama has no authentication, so that outcome cannot
 * occur and offering it would send an operator hunting for a key that does not
 * exist.
 *
 * NONE OF THIS TEXT IS DERIVED FROM THE SERVER'S RESPONSE, and none of it can
 * be: the server's enum carries no string field, so there is no upstream body,
 * exception message, or configured address anywhere in the path that produces
 * it. Do not add a free-text field to carry a server message through — that is
 * the shape this prevents.
 */
const OUTCOME_LABELS: Record<string, string> = {
  Ok: 'Connected',
  Unreachable: 'Unreachable',
  TlsFailure: 'TLS failure',
  UnexpectedResponse: 'Not Ollama',
};

/**
 * Falls back to the outcome string itself rather than to a generic "failed".
 *
 * If the server ever grows a fifth outcome, an operator seeing its raw enum name
 * still learns more than one seeing "Error" — and the fallback is safe to render
 * because the outcome is a closed enum name, never free text.
 */
const outcomeLabel = (outcome: string): string => OUTCOME_LABELS[outcome] ?? outcome;

/**
 * The Ollama configuration form.
 *
 * THE BASE URL IS SEEDED FROM THE SERVER AND EDITED IN PLACE — the opposite of
 * the webhook and source-key fields on this same surface, deliberately. Those
 * start empty and are labelled as REPLACING what is stored, because their value
 * is a credential the client must never hold. This one is not a secret: Ollama
 * has no authentication, the server serves the value back, and it rejects a URL
 * containing credentials so that stays true. An operator can therefore see and
 * correct the address rather than retyping it blind.
 *
 * `saved` and `rejection` are NOT held here. A successful save invalidates the
 * config, the refetch changes the key this form is mounted under, and the
 * remount would destroy any outcome state owned by this component — so "Saved."
 * would only ever survive a save that changed nothing. They live in
 * `AiSection`, above that boundary, and arrive as props.
 */
function OllamaForm({
  config,
  saved,
  setSaved,
  rejection,
  setRejection,
}: {
  config: OllamaConfig;
  saved: boolean;
  setSaved: (saved: boolean) => void;
  rejection: string | null;
  setRejection: (rejection: string | null) => void;
}) {
  const update = useUpdateOllamaConfigMutation();
  const test = useTestOllamaMutation();

  const [baseUrl, setBaseUrl] = useState(config.baseUrl);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setSaved(false);
    setRejection(null);

    // Sent as typed. No client-side URL check of our own: the server owns
    // validation, rejects rather than clamps, and states its own reason.
    update.mutate(
      { baseUrl },
      {
        // Captured from the PER-CALL callbacks into component state, which is
        // the shape that is correct by construction — such a callback runs after
        // delivery by definition, so there is nothing to sequence around. (There
        // is no reset() to pair it with here: this mutation carries no secret, so
        // it sets no gcTime: 0 either. See queries.ts and bead arb-689.)
        onSuccess: () => setSaved(true),
        onError: (error) => setRejection(errorMessage(error)),
      },
    );
  };

  return (
    <>
      <p className={local.status}>
        <span className={`${styles.badge} ${styles.badgeOk}`}>Configured</span>
        <span className={styles.muted}>
          Classification and the test button below both use this address. It is read from the
          database, so changing it here takes effect on the next classification — no restart.
        </span>
      </p>

      <form className={local.form} onSubmit={submit}>
        <label className={styles.field}>
          Ollama base URL
          <input
            className={local.urlInput}
            // A plain text field, never type="password": this is not a
            // credential, and masking it would only stop the operator checking
            // the address they are about to save.
            type="text"
            autoComplete="off"
            spellCheck={false}
            aria-label="Ollama base URL"
            value={baseUrl}
            onChange={(event) => setBaseUrl(event.target.value)}
          />
        </label>

        <p className={local.hint}>
          The address of your Ollama instance, such as http://ollama:11434. It must be an absolute
          http or https URL, and it must not contain a username or password — Ollama has no
          authentication, and credentials in the address would be written to the request log.
        </p>

        <div className={local.actions}>
          <button type="submit" className={styles.button} disabled={update.isPending}>
            Save
          </button>

          <button
            type="button"
            className={`${styles.button} ${styles.buttonSecondary}`}
            disabled={test.isPending}
            onClick={() => test.mutate()}
          >
            Test connection
          </button>

          {rejection !== null && (
            <p className={styles.error} role="alert">
              {rejection}
            </p>
          )}
          {saved && rejection === null && <p className={styles.success}>Saved.</p>}
        </div>
      </form>

      {/*
        The probe tests the STORED address, not what is currently in the field —
        so a test run before saving an edit reports on the address actually in
        force. The hint below says so, because the alternative reading ("it
        tested what I typed") would make a passing test after a bad edit
        misleading.
      */}
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
                completed is not a backend that answered, and the closed outcome
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
        The test asks Ollama for its model list at /api/tags. It checks the stored address, so save
        an edit before testing it.
      </p>
    </>
  );
}

/**
 * #89: the AI backend section of the Settings surface.
 *
 * <h3>Why its own section rather than a row in Sources</h3>
 * `Sources.tsx` records the reasoning in full: Ollama is an AI backend, not an
 * indexer — it is not searched, it carries no Torznab API key, and the sources
 * probe speaks the source API, so a shared test button would report
 * `UnexpectedResponse` against a perfectly healthy Ollama. This section is what
 * that comment points at.
 *
 * <h3>Why not a row on the settings catalog below</h3>
 * The value IS stored as an ordinary setting and takes the same
 * reject-never-clamp validation path every other setting takes — but it is off
 * `SettingsCatalog.Entries`, so it does not appear in the generic catalog list
 * further down this page. A catalog row cannot carry a connectivity probe, and
 * an entry there would additionally render the address a second time as an
 * unexplained text field beside this section. The rejected alternative was a
 * dedicated table: one non-secret scalar does not warrant an EF migration when
 * the Settings table already holds exactly this shape.
 *
 * <h3>No empty state</h3>
 * Unlike the sibling sections there is no "not configured" branch, because there
 * is no unconfigured state to be in: the address is seeded on first start (from
 * the environment, or from the built-in default), so this section always has a
 * value to show. That is the point of seeding the default rather than leaving
 * the row absent — an empty field reading "not configured" while classification
 * quietly worked against an invisible default would be the worse outcome.
 */
export function AiSection() {
  const config = useOllamaConfigQuery();

  // Owned HERE, above the remount boundary below, for the same reason the
  // catalog's `savedKey` and the notifications section's `saved` are: a
  // successful save invalidates the config and the refetch changes the form's
  // key, so state held inside the form is destroyed by the very save it is
  // reporting on.
  const [saved, setSaved] = useState(false);
  const [rejection, setRejection] = useState<string | null>(null);

  return (
    <section className={styles.panel}>
      <h2 className={styles.panelHeading}>AI backend</h2>
      <div className={styles.panelBody}>
        <QueryState isPending={config.isPending} error={config.error} data={config.data}>
          {(data) => (
            // Remounted whenever the server's configuration changes, so a
            // successful save reseeds the field from the server rather than
            // leaving a stale local edit — the same reason the catalog rows key
            // on their value.
            <OllamaForm
              key={data.baseUrl}
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
