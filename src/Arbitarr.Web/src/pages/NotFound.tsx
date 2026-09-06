import { Link, useLocation } from 'react-router-dom';
import { PageHeader } from '../components/shell/PageHeader';

/**
 * Client-side 404.
 *
 * It renders INSIDE the shell (it is a child of the AppShell route), so an
 * unknown path still shows the sidebar and the operator can navigate away
 * instead of hitting a bare dead end. routing.test.tsx asserts both halves:
 * the not-found copy and the still-present navigation landmark.
 */
export default function NotFoundPage() {
  const { pathname } = useLocation();

  return (
    <>
      <PageHeader title="Page not found" description={`No page is routed at ${pathname}.`} />
      <p>
        <Link to="/">Back to the dashboard</Link>
      </p>
    </>
  );
}
