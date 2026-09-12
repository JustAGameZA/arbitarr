import { useState } from 'react';

import { PageHeader } from '../../components/shell/PageHeader';
import { QueryState, errorMessage } from '../QueryState';
import type { SettingCatalogEntry } from '../../api/types';
import styles from '../surface.module.css';
import local from './Settings.module.css';
import { NotificationsSection } from './Notifications/Notifications';
import { useSettingsQuery, useUpdateSettingMutation } from './queries';
import { SourcesSection } from './Sources/Sources';
import { SonarrSection } from './Sonarr/Sonarr';
import { RadarrSection } from './Radarr/Radarr';
import { ApiKeysSection } from './ApiKeys/ApiKeys';
import { AccountSection } from './Account/Account';
import { AiSection } from './Ai/Ai';
import { SectionNav, slugifyGroup, type SectionNavEntry } from './SectionNav';

/**
 * The seven static sections, in the order the page renders them.
 *
 * This array is the SINGLE source for both the rendered sections and the nav
 * entries below, which is the load-bearing property of arb-5oe: the nav cannot
 * list a section the page does not render, or miss one it does. The dynamic
 * catalog groups are appended to it from the same groupSettings() call that
 * renders them, so a group added to the server's catalog appears in the nav
 * automatically with no change here (Settings.test.tsx asserts exactly that).
 *
 * The ids are hard-coded rather than slugified from the labels: they are anchor
 * targets that may end up in a bookmark or a linked-to URL, so they should not
 * silently change if a label is reworded.
 */
const STATIC_SECTIONS = [
  { id: 'account', label: 'Account' },
  { id: 'sources', label: 'Sources' },
  { id: 'sonarr', label: 'Sonarr' },
  { id: 'radarr', label: 'Radarr' },
  { id: 'api-keys', label: 'API keys' },
  { id: 'notifications', label: 'Notifications' },
  { id: 'ai', label: 'AI backend' },
] as const;

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

  /**
   * The nav's entries: the seven static sections, then one per catalog group.
   *
   * Derived from the SAME groupSettings() call that renders the dynamic panels
   * and the SAME STATIC_SECTIONS array the static ones are wrapped with, so a
   * group added to the server's catalog appears in the nav with no change here.
   * Building this from a second, hand-written list is the obvious shortcut and
   * it is exactly what would drift -- Settings.test.tsx pins the automatic
   * behaviour by adding a group to the mocked catalog and expecting a new link.
   *
   * While the query is pending or failed there are no groups yet, so the nav
   * shows the static entries alone rather than nothing: those sections render
   * regardless of the catalog request, and a nav that vanished on a failed
   * request would strand the operator on a page they can still use.
   */
  const navEntries: SectionNavEntry[] = [
    ...STATIC_SECTIONS.map((section) => ({ id: section.id, label: section.label })),
    ...groupSettings(settings.data ?? []).map(([group]) => ({
      id: slugifyGroup(group),
      label: group,
    })),
  ];

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

      <div className={local.layout}>
        <SectionNav entries={navEntries} />

        <div>
          {/* Each static section is wrapped rather than given an id of its own:
              the seven section components take no props, and threading one
              through all seven would be a seven-file diff across files that are
              CRLF, for an anchor target that a wrapper provides just as well.
              The wrapper carries the scroll-margin-top that keeps the target
              clear of the sticky bar. */}
          {/* The operator's own account before the machines' credentials. */}
          <div id="account" className={local.section}>
            <AccountSection />
          </div>

          <div id="sources" className={local.section}>
            <SourcesSection />
          </div>

          {/* Beside Sources rather than in it: Sonarr is the other machine Arbitarr
              talks to, but it is not a source — it is not searched, and its probe
              speaks its own API. See SonarrSection for the full distinction. */}
          <div id="sonarr" className={local.section}>
            <SonarrSection />
          </div>

          {/* Its own section rather than a second field on Sonarr's: the server
              keeps RadarrConfigResponse a separate record from ArrConfigResponse
              even though the shapes match today (arb-arrq D3), and Radarr is
              deliberately out of identity resolution (D4). See RadarrSection. */}
          <div id="radarr" className={local.section}>
            <RadarrSection />
          </div>

          <div id="api-keys" className={local.section}>
            <ApiKeysSection />
          </div>

          <div id="notifications" className={local.section}>
            <NotificationsSection />
          </div>

          <div id="ai" className={local.section}>
            <AiSection />
          </div>

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
                    <section
                      key={group}
                      id={slugifyGroup(group)}
                      className={`${styles.panel} ${local.section}`}
                    >
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
        </div>
      </div>
    </>
  );
}
