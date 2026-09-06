import { PageHeader } from '../components/shell/PageHeader';

/**
 * Placeholder surface. PR #4 replaces the body with the real Suppressions view; the
 * route, the title and the shell placement are already under test here so that
 * port is a body swap rather than a structural change.
 */
export default function SuppressionsPage() {
  return <PageHeader title="Suppressions" description="Releases currently suppressed from results." />;
}
