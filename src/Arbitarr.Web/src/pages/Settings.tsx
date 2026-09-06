import { PageHeader } from '../components/shell/PageHeader';

/**
 * Placeholder surface. PR #4 replaces the body with the real Settings view; the
 * route, the title and the shell placement are already under test here so that
 * port is a body swap rather than a structural change.
 */
export default function SettingsPage() {
  return <PageHeader title="Settings" description="Proxy, indexer and application configuration." />;
}
