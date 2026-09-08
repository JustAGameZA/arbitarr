import { useState } from 'react';
import type { FormEvent } from 'react';

import { QueryState, errorMessage } from '../../QueryState';
import type {
  NotificationConfig,
  NotificationDeliveryOutcome,
  NotificationTrigger,
  UpdateNotificationConfigRequest,
} from '../../../api/types';
import styles from '../../surface.module.css';
import { useSecretEvictingMutation } from '../useSecretEvictingMutation';
import local from './Notifications.module.css';
import {
  useClearWebhookMutation,
  useNotificationConfigQuery,
  useSendTestNotificationMutation,
  useUpdateNotificationConfigMutation,
} from './queries';

/**
 * The four triggers #57 ships, in the order an operator reads them: each
 * failing condition immediately followed by its closing edge.
 *
 * Enumerated here rather than derived from the server's `enabledTriggers`,
 * which lists only the ones currently ON — deriving the checkbox list from it
 * would make a disabled trigger vanish from the page entirely and leave no way
 * to switch it back on. The names are the server's `NotificationTrigger` enum
 * verbatim; the PUT rejects anything else by name.
 *
 * #57's issue text also named "Restore was performed", which has no emitter
 * until #56 lands. Adding it here and to the server's enum is the whole change
 * when it does.
 */
const TRIGGERS: { name: NotificationTrigger; label: string; description: string }[] = [
  {
    name: 'SourceFailing',
    label: 'Source started failing',
    description: 'A source crossed the consecutive-failure threshold below.',
  },
  {
    name: 'SourceRecovered',
    label: 'Source recovered',
    description: 'A source that had been reported as failing answered successfully again.',
  },
  {
    name: 'SuppressionRateHigh',
    label: 'Suppression rate high',
    description: 'The suppression rate over the window below crossed the threshold below.',
  },
  {
    name: 'SuppressionRateNormal',
    label: 'Suppression rate back to normal',
    description:
      'The rate fell back under the threshold. Without this one, a rate reported as high and never reported as recovered cannot be told from a still-broken rule.',
  },
];

/**
 * What each delivery outcome means to the operator, in the client's own words.
 *
 * DISTINCT PER OUTCOME, deliberately, and never one red "failed": a wrong
 * address, a bad certificate and an endpoint that refuses the post are three
 * different fixes, which is why the server keeps the outcome a CLOSED enum
 * rather than a message string.
 *
 * NONE OF THESE STRINGS NAMES THE TARGET, and none can: each is selected by the
 * outcome alone with no interpolation, so there is no branch here capable of
 * putting a URL, a hostname or a status code in front of the operator. These
 * caption the last-delivery indicator, where the server carries no message of
 * its own; the test button renders the server's wording instead.
 */
const OUTCOME_LABELS: Record<NotificationDeliveryOutcome, string> = {
  Delivered: 'Delivered',
  Unreachable: 'Could not reach the webhook',
  TlsFailure: 'TLS handshake failed',
  Rejected: 'The webhook refused the notification',
  NotConfigured: 'No webhook configured',
};

/** Which badge an outcome earns. Delivered is the only success. */
function outcomeBadgeClass(outcome: NotificationDeliveryOutcome): string {
  if (outcome === 'Delivered') {
    return styles.badgeOk;
  }
  return outcome === 'NotConfigured' ? styles.badgeWarn : styles.badgeDanger;
}

function formatTimestamp(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

/**
 * The last delivery attempt, or a statement that there has not been one.
 *
 * "Nothing has been sent yet" is not the same claim as a failure, and #57's own
 * rationale is that a notifier which is silently broken is worse than none — so
 * the operator is told which of the two they are looking at, and the empty
 * state names what would fill it.
 */
function LastDelivery({ config }: { config: NotificationConfig }) {
  if (config.lastDeliveryOutcome === null) {
    return (
      <p className={styles.empty}>
        No notification has been attempted yet. Sending a test below, or the first enabled trigger
        firing, fills this in.
      </p>
    );
  }

  return (
    <p className={local.lastDelivery}>
      <span className={`${styles.badge} ${outcomeBadgeClass(config.lastDeliveryOutcome)}`}>
        {OUTCOME_LABELS[config.lastDeliveryOutcome]}
      </span>
      {config.lastDeliveryAt !== null && (
        <span className={styles.muted}>{formatTimestamp(config.lastDeliveryAt)}</span>
      )}
    </p>
  );
}

/**
 * The notification configuration form.
 *
 * THE WEBHOOK URL IS A SECRET AND THIS COMPONENT NEVER HOLDS ONE FOR LONG. The
 * server reports presence as a bool and serves no value, so the field starts
 * empty and is labelled as REPLACING what is stored rather than showing it. On
 * submit the typed value goes into the request and the field is cleared in the
 * same act — including when the request then fails, because a rejected save is
 * exactly the state in which a retained value would sit on screen longest. The
 * operator retypes it; that is the price of the URL never being recoverable
 * from this page's DOM, its React state, or a screenshot of either.
 *
 * Everything else is seeded from the server and edited freely. Only the secret
 * is write-only.
 *
 * `saved` and `rejection` are NOT held here. A successful save invalidates the
 * config, the refetch changes the key this form is mounted under, and the
 * remount would destroy any outcome state owned by this component — so "Saved."
 * would only ever survive a save that changed nothing. They live in
 * `NotificationsSection`, above that boundary, and arrive as props.
 */
function NotificationForm({
  config,
  saved,
  setSaved,
  rejection,
  setRejection,
}: {
  config: NotificationConfig;
  saved: boolean;
  setSaved: (saved: boolean) => void;
  rejection: string | null;
  setRejection: (rejection: string | null) => void;
}) {
  const update = useUpdateNotificationConfigMutation();
  const clear = useClearWebhookMutation();
  const test = useSendTestNotificationMutation();
  const { settle } = useSecretEvictingMutation();

  const [enabled, setEnabled] = useState(config.enabled);
  const [webhookUrl, setWebhookUrl] = useState('');
  const [failureThreshold, setFailureThreshold] = useState(
    String(config.consecutiveFailureThreshold),
  );
  const [rateThreshold, setRateThreshold] = useState(String(config.suppressionRateThreshold));
  const [evaluationWindow, setEvaluationWindow] = useState(config.suppressionRateWindow);
  const [triggers, setTriggers] = useState<NotificationTrigger[]>(config.enabledTriggers);
  const [confirmingClear, setConfirmingClear] = useState(false);

  const toggleTrigger = (name: NotificationTrigger) =>
    setTriggers((current) =>
      current.includes(name) ? current.filter((t) => t !== name) : [...current, name],
    );

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setSaved(false);
    setRejection(null);

    // Sent as typed. No bounds pre-check of our own: the server owns validation,
    // rejects rather than clamps, and states its own reason — the same rule
    // useUpdateSettingMutation's comment gives, where a client-side guard once
    // blocked values the server would have accepted.
    const request: UpdateNotificationConfigRequest = {
      enabled,
      consecutiveFailureThreshold: Number(failureThreshold),
      suppressionRateThreshold: Number(rateThreshold),
      suppressionRateWindow: evaluationWindow,
      enabledTriggers: triggers,
    };

    // THE FIELD IS OMITTED UNLESS THE OPERATOR TYPED ONE. Omission means "leave
    // the stored URL alone"; the client never had it and so cannot reapply it,
    // which is why an ordinary threshold edit must not carry the field at all.
    const typed = webhookUrl.trim();
    if (typed !== '') {
      request.webhookUrl = typed;
    }

    // Cleared BEFORE the request resolves, so no path — success, rejection or
    // network failure — leaves the secret in state to be rendered back.
    setWebhookUrl('');

    // settle() evicts the settled entry from the MutationCache from THIS
    // per-call site, on both the success and failure path — `request` can
    // carry `webhookUrl`, so the entry must not outlive the request either
    // way. See `useSecretEvictingMutation` (arb-689) for why this must not
    // move to a hook-level onSuccess/onSettled in queries.ts.
    //
    // settle() hands back the RAW error; `rejection` here is a rendered
    // string, so it is formatted with errorMessage() at this call site —
    // the one place this component turns an error into displayed text.
    update.mutate(request, {
      onSuccess: () => {
        setSaved(true);
        settle(update, null, (captured) =>
          setRejection(captured === null ? null : errorMessage(captured)),
        );
      },
      onError: (error) =>
        settle(update, error, (captured) =>
          setRejection(captured === null ? null : errorMessage(captured)),
        ),
    });
  };

  return (
    <>
      <form className={local.form} onSubmit={submit}>
        <label className={styles.checkboxField}>
          <input
            type="checkbox"
            checked={enabled}
            onChange={(event) => setEnabled(event.target.checked)}
          />
          Send notifications
        </label>

        <div className={local.group}>
          <h3 className={local.groupHeading}>Webhook target</h3>

          <p className={local.status}>
            <span
              className={`${styles.badge} ${config.hasWebhookUrl ? styles.badgeOk : styles.badgeWarn}`}
            >
              {config.hasWebhookUrl ? 'Configured' : 'Not configured'}
            </span>
            <span className={styles.muted}>
              {config.hasWebhookUrl
                ? 'A target is stored. It is never sent back to this page, so it cannot be shown or edited in place — only replaced or cleared.'
                : 'No target is stored. Enter one below to start sending notifications.'}
            </span>
          </p>

          <label className={styles.field}>
            {config.hasWebhookUrl ? 'Replace the webhook URL' : 'Webhook URL'}
            <input
              className={local.urlInput}
              // A password field: for every provider that shapes a webhook this
              // way the token sits in the path, so the URL is the credential.
              type="password"
              autoComplete="off"
              aria-label={config.hasWebhookUrl ? 'Replace the webhook URL' : 'Webhook URL'}
              placeholder={config.hasWebhookUrl ? 'Leave blank to keep the stored target' : ''}
              value={webhookUrl}
              onChange={(event) => setWebhookUrl(event.target.value)}
            />
          </label>

          <p className={local.hint}>
            Leaving this blank keeps whatever is stored, so saving a threshold change never touches
            the target.
          </p>

          {config.hasWebhookUrl &&
            (confirmingClear ? (
              <div className={local.confirm} role="alert">
                <span>
                  Clear the stored webhook? Notifications stop until a new target is entered, and
                  the current one cannot be recovered from this page.
                </span>
                <button
                  type="button"
                  className={`${styles.button} ${styles.buttonDanger}`}
                  disabled={clear.isPending}
                  onClick={() => {
                    setConfirmingClear(false);
                    clear.mutate();
                  }}
                >
                  Clear it
                </button>
                <button
                  type="button"
                  className={`${styles.button} ${styles.buttonSecondary}`}
                  onClick={() => setConfirmingClear(false)}
                >
                  Keep it
                </button>
              </div>
            ) : (
              <button
                type="button"
                className={`${styles.button} ${styles.buttonDanger}`}
                onClick={() => setConfirmingClear(true)}
              >
                Clear webhook
              </button>
            ))}

          {clear.isError && (
            <p className={styles.error} role="alert">
              {errorMessage(clear.error)}
            </p>
          )}
        </div>

        <div className={local.group}>
          <h3 className={local.groupHeading}>Thresholds</h3>

          <div className={styles.form}>
            <label className={styles.field}>
              Consecutive failures before a source is reported
              <input
                className={`${styles.input} ${styles.inputNarrow}`}
                aria-label="Consecutive failures before a source is reported"
                value={failureThreshold}
                onChange={(event) => setFailureThreshold(event.target.value)}
              />
            </label>

            <label className={styles.field}>
              Suppression rate threshold
              <input
                className={`${styles.input} ${styles.inputNarrow}`}
                aria-label="Suppression rate threshold"
                value={rateThreshold}
                onChange={(event) => setRateThreshold(event.target.value)}
              />
            </label>

            <label className={styles.field}>
              Evaluation window
              <input
                className={styles.input}
                aria-label="Evaluation window"
                value={evaluationWindow}
                onChange={(event) => setEvaluationWindow(event.target.value)}
              />
            </label>
          </div>

          <p className={local.hint}>
            The server rejects a value outside its bounds and never quietly adjusts one, so a
            rejection here is its answer rather than this page&apos;s guess. The window is a
            duration such as 01:00:00.
          </p>
        </div>

        <div className={local.group}>
          <h3 className={local.groupHeading}>What to notify about</h3>

          {TRIGGERS.map((trigger) => (
            <label key={trigger.name} className={local.trigger}>
              <input
                type="checkbox"
                checked={triggers.includes(trigger.name)}
                onChange={() => toggleTrigger(trigger.name)}
              />
              <span className={local.triggerText}>
                <strong>{trigger.label}</strong>
                <span className={local.triggerReason}>{trigger.description}</span>
              </span>
            </label>
          ))}
        </div>

        <div className={local.actions}>
          <button type="submit" className={styles.button} disabled={update.isPending}>
            Save
          </button>
          {rejection !== null && (
            <p className={styles.error} role="alert">
              {rejection}
            </p>
          )}
          {saved && rejection === null && <p className={styles.success}>Saved.</p>}
        </div>
      </form>

      <div className={local.group}>
        <h3 className={local.groupHeading}>Last delivery</h3>
        <LastDelivery config={config} />

        <div className={local.actions}>
          <button
            type="button"
            className={`${styles.button} ${styles.buttonSecondary}`}
            disabled={test.isPending}
            onClick={() => test.mutate()}
          >
            Send a test notification
          </button>

          {/*
            The server's own wording for the outcome it actually got. Success is
            claimed only where the server said `success`: a request that
            completed is not a delivery that arrived, and the closed outcome enum
            exists so the client never has to guess which it was. On a failure
            this shows the KIND — unreachable, TLS, refused — because that is
            what the server's message carries; it cannot name the target, since
            no response on this surface is capable of carrying one.
          */}
          {test.isSuccess && (
            <p
              className={test.data.success ? styles.success : styles.error}
              role={test.data.success ? undefined : 'alert'}
            >
              {test.data.message}
            </p>
          )}

          {/*
            The request itself failed — rejected admin key, server down. That is
            NOT a delivery outcome and must not be dressed as one, so it shows
            the transport error and no outcome wording at all.
          */}
          {test.isError && (
            <p className={styles.error} role="alert">
              {errorMessage(test.error)}
            </p>
          )}
        </div>
      </div>
    </>
  );
}

/**
 * #83: the notification configuration section of the Settings surface — #57's
 * AC1, "webhook target configurable from the UI, with a working test button".
 *
 * Its own section so the secret-bearing panels on this surface read as one
 * idiom: a Configured / Not configured indicator, a write-only field that
 * REPLACES rather than shows, omission meaning "leave alone", and an explicit
 * clear behind a confirmation.
 */
export function NotificationsSection() {
  const config = useNotificationConfigQuery();

  // The save outcome is owned HERE, above the remount boundary below, for the
  // same reason the catalog's `savedKey` / `failedKey` are owned by
  // `SettingsPage`: a successful save invalidates the config and the refetch
  // changes the form's key, so state held inside the form is destroyed by the
  // very save it is reporting on. Held there, "Saved." would render only for a
  // save that changed nothing.
  const [saved, setSaved] = useState(false);
  // The server's rejection is held as text rather than read from
  // `update.error`, because the mutation resets itself the moment it settles so
  // the webhook URL cannot linger in the mutation cache as `variables` (see
  // queries.ts). That reset also clears `error`, so the operator's rejection
  // text has to be captured on the way past or it would vanish a tick after it
  // appeared — which would quietly defeat the reject-never-clamp rule this
  // surface exists to honour.
  const [rejection, setRejection] = useState<string | null>(null);

  return (
    <section className={styles.panel}>
      <h2 className={styles.panelHeading}>Notifications</h2>
      <div className={styles.panelBody}>
        <QueryState isPending={config.isPending} error={config.error} data={config.data}>
          {(data) => (
            // Remounted whenever the server's configuration changes, so a
            // successful save reseeds every field from the server rather than
            // leaving a stale local edit — the same reason the catalog rows key
            // on their value. That remount is exactly why the save outcome is
            // hoisted above this line and passed down: state owned by the form
            // does not survive the refetch its own save triggers.
            <NotificationForm
              key={`${data.enabled}:${data.hasWebhookUrl}:${data.consecutiveFailureThreshold}:${data.suppressionRateThreshold}:${data.suppressionRateWindow}:${data.enabledTriggers.join(',')}`}
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
