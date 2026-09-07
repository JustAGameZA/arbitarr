import { useId, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';

import { QueryState } from '../QueryState';
import type { BackupStatusResponse } from '../../api/types';
import styles from '../surface.module.css';
import local from './System.module.css';
import {
  RESTORE_CONFIRMATION_WORD,
  downloadBackupArchive,
  restoreFromArchive,
  useBackupStatusQuery,
} from './queries';

/**
 * Renders an instant the way the Logs tab does: local time for the viewer's own clock,
 * the zone name alongside so the string is not ambiguous between two readers, and the
 * server's exact ISO instant in `title`.
 *
 * The em-dash for "no data yet" is the same convention `formatRate` uses -- one
 * no-data idiom across this page, not two.
 */
function Timestamp({ value }: { value: string | null }) {
  if (value === null) {
    return <span>—</span>;
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    // Never silently blank a malformed timestamp: the raw value makes a server-side
    // format change visible instead of looking like a missing value.
    return <span title={value}>{value}</span>;
  }

  return (
    <time dateTime={value} title={value}>
      {parsed.toLocaleString(undefined, { timeZoneName: 'short' })}
    </time>
  );
}

/**
 * What the last backup and the last restore were.
 *
 * A stale backup is the failure this panel exists to make obvious -- issue #56's "so a
 * stale backup is obvious rather than silently assumed fresh" -- so the timestamp is
 * shown even when automatic backups are off, and the off state says so explicitly rather
 * than leaving an operator to infer it from a timestamp that stopped moving.
 */
function StatusPanel({ status }: { status: BackupStatusResponse }) {
  return (
    <>
      <dl className={local.metrics}>
        <div className={local.metric}>
          <dt className={local.metricLabel}>Last backup</dt>
          <dd className={local.metricValue}>
            <Timestamp value={status.lastBackupAt} />
          </dd>
        </div>
        <div className={local.metric}>
          <dt className={local.metricLabel}>Automatic backups</dt>
          <dd className={local.metricValue}>
            {status.automaticBackupsRetained === 0
              ? 'Off'
              : `Keeping ${status.automaticBackupsRetained}`}
          </dd>
        </div>
      </dl>

      {status.lastBackupAt === null && (
        <p className={styles.empty}>
          No backup has been taken yet. Download one below, or set “Automatic backups retained”
          in Settings above zero to have Arbitarr keep them on the maintenance schedule.
        </p>
      )}

      {status.automaticBackupsRetained === 0 && (
        <p className={styles.muted}>
          Automatic backups are off, so the time above only moves when you download one.
        </p>
      )}

      {/*
        The degraded path carries its own provenance. A failed scheduled backup otherwise shows
        only as a "Last backup" time that quietly stopped moving -- which reads as a healthy
        safety net, and is the stale-backup failure this tab exists to make obvious. Rendered
        above the restore line because it describes the state the operator is in NOW.
      */}
      {status.lastBackupFailureAt !== null && (
        <p className={styles.error}>
          <strong>The last automatic backup failed</strong> at{' '}
          <Timestamp value={status.lastBackupFailureAt} />: {status.lastBackupFailureReason}
          {' '}The time above is from the last backup that succeeded, so it is older than it
          looks. Download one now, and check the config volume has free space.
        </p>
      )}

      {status.lastRestoreAt !== null && (
        <p className={status.lastRestoreSucceeded ? styles.success : styles.error}>
          Last restore <Timestamp value={status.lastRestoreAt} />: {status.lastRestoreMessage}
        </p>
      )}
    </>
  );
}

/**
 * The Backup tab (#56) -- take a backup, and restore one.
 *
 * WHAT A BACKUP COVERS, SAID PLAINLY. The archive holds `arbitarr.db` and
 * `release-guid-secret.key` and NOT the application log database, which is a separate
 * SQLite file kept apart precisely so a configuration backup does not drag log contents
 * around. The copy below says both halves, because an operator who believes a backup
 * covers their logs and one who believes it omits their GUID secret are both wrong in
 * ways that only surface after a disaster.
 *
 * THE SENSITIVITY WARNING SITS AT THE DOWNLOAD, not in a doc. The file contains the HMAC
 * secret and every configured source's API key; plan §3.3 requires it to be said at the
 * point of download rather than documented away.
 */
export function BackupTab() {
  const status = useBackupStatusQuery();
  const queryClient = useQueryClient();

  const [downloading, setDownloading] = useState(false);
  const [downloadError, setDownloadError] = useState<string | null>(null);

  const [file, setFile] = useState<File | null>(null);
  const [confirmation, setConfirmation] = useState('');
  const [restoring, setRestoring] = useState(false);
  const [restoreOutcome, setRestoreOutcome] = useState<{ ok: boolean; message: string } | null>(null);

  const fileInput = useRef<HTMLInputElement>(null);
  const fileFieldId = useId();
  const confirmFieldId = useId();

  const confirmed = confirmation === RESTORE_CONFIRMATION_WORD;
  const canRestore = file !== null && confirmed && !restoring;

  const onDownload = async () => {
    setDownloading(true);
    setDownloadError(null);
    try {
      await downloadBackupArchive();
      // The download records a last-backup time server-side, so the panel above is now
      // stale by exactly the action the operator just took.
      await queryClient.invalidateQueries({ queryKey: ['admin', 'backup', 'status'] });
    } catch (error) {
      setDownloadError(error instanceof Error ? error.message : 'The backup could not be downloaded.');
    } finally {
      setDownloading(false);
    }
  };

  const onRestore = async () => {
    if (file === null || !confirmed) {
      return;
    }

    setRestoring(true);
    setRestoreOutcome(null);
    try {
      const result = await restoreFromArchive(file);
      setRestoreOutcome({ ok: result.succeeded, message: result.message });

      if (result.succeeded) {
        // Clear the form so a second click cannot re-apply the same archive against a
        // process that is already shutting down.
        setFile(null);
        setConfirmation('');
        if (fileInput.current !== null) {
          fileInput.current.value = '';
        }
      }
    } catch (error) {
      setRestoreOutcome({
        ok: false,
        message: error instanceof Error ? error.message : 'The restore could not be attempted.',
      });
    } finally {
      setRestoring(false);
    }
  };

  return (
    <>
      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Backup</h2>
        <div className={styles.panelBody}>
          <QueryState isPending={status.isPending} error={status.error} data={status.data}>
            {(data) => <StatusPanel status={data} />}
          </QueryState>
        </div>
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Download a backup</h2>
        <div className={styles.panelBody}>
          <p className={styles.muted}>
            The archive contains your configuration database and the per-instance release-GUID
            secret. It does <strong>not</strong> contain the application log database, which is a
            separate file kept out of backups on purpose.
          </p>

          {/* Plan §3.3 / AC3: the warning is at the point of download, not in a doc. */}
          <p className={styles.error}>
            <strong>This file is a credential.</strong> It carries the API keys of every configured
            source and the secret that authenticates every release GUID this instance has issued.
            Store it as securely as you would a password file.
          </p>

          <button
            type="button"
            className={styles.button}
            disabled={downloading}
            onClick={() => void onDownload()}
          >
            {downloading ? 'Preparing backup…' : 'Download backup'}
          </button>

          {downloadError !== null && <p className={styles.error}>{downloadError}</p>}
        </div>
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Restore from a backup</h2>
        <div className={styles.panelBody}>
          <p className={styles.error}>
            <strong>Restoring replaces your current configuration and your release-GUID secret.</strong>{' '}
            Restoring an older secret invalidates every release GUID issued since that backup was
            taken, so Sonarr and Radarr will stop matching anything they previously saw from this
            instance.
          </p>

          <p className={styles.muted}>
            Arbitarr saves a backup of your current state before applying anything, so a mistaken
            restore can itself be undone. The archive is checked before any of it is applied — an
            archive from a newer version of Arbitarr is refused rather than half-applied. Arbitarr
            then shuts down so the restored files are loaded, and comes back automatically if your
            deployment restarts it.
          </p>

          <div className={styles.form}>
            <label className={styles.field} htmlFor={fileFieldId}>
              Backup archive
              <input
                ref={fileInput}
                id={fileFieldId}
                type="file"
                accept=".zip,application/zip"
                className={styles.input}
                onChange={(event) => setFile(event.target.files?.[0] ?? null)}
              />
            </label>

            <label className={styles.field} htmlFor={confirmFieldId}>
              Type {RESTORE_CONFIRMATION_WORD} to confirm
              <input
                id={confirmFieldId}
                type="text"
                className={styles.input}
                value={confirmation}
                autoComplete="off"
                onChange={(event) => setConfirmation(event.target.value)}
              />
            </label>
          </div>

          <button
            type="button"
            className={styles.buttonDanger}
            disabled={!canRestore}
            onClick={() => void onRestore()}
          >
            {restoring ? 'Restoring…' : 'Restore and restart'}
          </button>

          {restoreOutcome !== null && (
            <p className={restoreOutcome.ok ? styles.success : styles.error}>
              {restoreOutcome.message}
            </p>
          )}
        </div>
      </section>
    </>
  );
}
