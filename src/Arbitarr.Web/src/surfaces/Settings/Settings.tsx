import { useState } from 'react';

import { PageHeader } from '../../components/shell/PageHeader';
import { QueryState, errorMessage } from '../QueryState';
import type { SettingCatalogEntry } from '../../api/types';
import styles from '../surface.module.css';
import local from './Settings.module.css';
import { NotificationsSection } from './Notifications/Notifications';
import { useSettingsQuery, useUpdateSettingMutation } from './queries';
import { ApiKeysSection } from './ApiKeys/ApiKeys';

/** Groups the flat catalog into its declared groups, preserving server order. */
export function groupSettings(entries: SettingCatalogEntry[]): [string, SettingCatalogEntry[]][] {
  const groups = new Map<string, SettingCatalogEntry[]>();
  for (const entry of entries) {
    const existing = groups.get(entry.group);
    if (existing === undefined) {
      groups.set(entry.group, [entry]);
    } else {
      existing.push(entry);
    }
  }
  return [...groups.entries()];
}

/**
 * The bounds line: min, max, and — where there is no maximum — the server's
 * stated reason for that.
 *
 * AC10 asks for the rationale behind each bound to be visible where the value
 * is edited. NoMaximumReason, RestartReason, GovernedTable and
 * GovernedTableRows are all carried by the DTO and none of them was rendered by
 * the legacy admin-settings.js, so the operator saw a floor and ceiling with no
 * account of why they were there.
 */
function Bounds({ entry }: { entry: SettingCatalogEntry }) {
  if (entry.isBoolean) {
    return null;
  }

  return (
    <p className={local.bounds}>
      <span>
        Minimum: <strong>{entry.min ?? 'none'}</strong>
      </span>
      <span>
        Maximum: <strong>{entry.max ?? 'none'}</strong>
      </span>
      {entry.max === null && entry.noMaximumReason !== null && (
        <span className={local.reason}>{entry.noMaximumReason}</span>
      )}
    </p>
  );
}

function SettingRow({
  entry,
  onSave,
  saving,
  savedKey,
  failedKey,
  failure,
}: {
  entry: SettingCatalogEntry;
  onSave: (key: string, value: string) => void;
  saving: boolean;
  savedKey: string | null;
  failedKey: string | null;
  failure: unknown;
}) {
  // Seeded from the server's value, then owned by the operator until the next
  // successful save refetches the catalog and remounts this row by key.
  const [value, setValue] = useState(entry.value);
  const rejected = failedKey === entry.key;

  return (
    <div className={local.setting}>
      <div className={local.settingHead}>
        <strong>{entry.displayName}</strong>
        <code className={local.key}>{entry.key}</code>
        {entry.requiresRestart && (
          <span className={`${styles.badge} ${styles.badgeWarn}`}>Restart required</span>
        )}
      </div>

      <p className={local.rationale}>{entry.rationale}</p>

      {entry.requiresRestart && entry.restartReason !== null && (
        <p className={local.reason}>{entry.restartReason}</p>
      )}

      {entry.governedTable !== null && (
        <p className={local.reason}>
          Governs <code className={local.key}>{entry.governedTable}</code>
          {entry.governedTableRows !== null && ` — ${entry.governedTableRows} rows today`}
        </p>
      )}

      <Bounds entry={entry} />

      <form
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          // Straight to the server, whatever was typed. No bounds pre-check:
          // the server rejects out-of-range values and never clamps them, and a
          // second opinion here would either block a value the server accepts
          // or invent a rejection it never issued.
          onSave(entry.key, value);
        }}
      >
        {entry.isBoolean ? (
          <label className={styles.field}>
            Value
            <select
              className={styles.select}
              aria-label={`${entry.displayName} value`}
              value={value}
              onChange={(event) => setValue(event.target.value)}
            >
              <option value="True">True</option>
              <option value="False">False</option>
            </select>
          </label>
        ) : (
          <label className={styles.field}>
            Value
            <input
              className={styles.input}
              aria-label={`${entry.displayName} value`}
              value={value}
              onChange={(event) => setValue(event.target.value)}
            />
          </label>
        )}
        <button type="submit" className={styles.button} disabled={saving}>
          Save
        </button>
      </form>

      {rejected && (
        <p className={styles.error} role="alert">
          {errorMessage(failure)}
        </p>
      )}
      {savedKey === entry.key && !rejected && <p className={styles.success}>Saved.</p>}
    </div>
  );
}

/**
 * Settings (AC10).
 *
 * Every value is edited against the server's own validation: reject, never
 * clamp. The rejection the operator reads is the server's exact words, and the
 * entry they typed stays in the field so it can be corrected.
 */
export default function SettingsPage() {
  const settings = useSettingsQuery();
  const update = useUpdateSettingMutation();
  const [savedKey, setSavedKey] = useState<string | null>(null);
  const [failedKey, setFailedKey] = useState<string | null>(null);

  const save = (key: string, value: string) => {
    setSavedKey(null);
    setFailedKey(null);
    update.mutate(
      { key, value },
      {
        onSuccess: () => setSavedKey(key),
        onError: () => setFailedKey(key),
      },
    );
  };

  return (
    <>
      <PageHeader
        title="Settings"
        description="Tunable values, with the bounds and rationale the server enforces."
      />

      <ApiKeysSection />

      <NotificationsSection />

      <QueryState isPending={settings.isPending} error={settings.error} data={settings.data}>
        {(entries) =>
          entries.length === 0 ? (
            <section className={styles.panel}>
              <div className={styles.panelBody}>
                <p className={styles.empty}>No editable settings.</p>
              </div>
            </section>
          ) : (
            <>
              {groupSettings(entries).map(([group, groupEntries]) => (
                <section key={group} className={styles.panel}>
                  <h2 className={styles.panelHeading}>{group}</h2>
                  <div className={styles.panelBody}>
                    {groupEntries.map((entry) => (
                      <SettingRow
                        // Keyed by key AND value so a successful save, which
                        // refetches the catalog, reseeds the field from the
                        // server rather than leaving a stale local edit.
                        key={`${entry.key}:${entry.value}`}
                        entry={entry}
                        onSave={save}
                        saving={update.isPending}
                        savedKey={savedKey}
                        failedKey={failedKey}
                        failure={update.error}
                      />
                    ))}
                  </div>
                </section>
              ))}
            </>
          )
        }
      </QueryState>
    </>
  );
}
