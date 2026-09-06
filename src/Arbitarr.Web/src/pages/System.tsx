import { PageHeader } from '../components/shell/PageHeader';

/**
 * Placeholder surface. PR #4 replaces the body with the real System view; the
 * route, the title and the shell placement are already under test here so that
 * port is a body swap rather than a structural change.
 */
export default function SystemPage() {
  return <PageHeader title="System" description="Build information, logs and runtime diagnostics." />;
}
