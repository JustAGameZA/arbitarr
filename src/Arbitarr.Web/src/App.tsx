import { AppRoutes } from './routes';

/**
 * The app is the route table and nothing else. Providers (router, query client)
 * live in main.tsx so a test can mount App under a MemoryRouter of its own.
 */
export default function App() {
  return <AppRoutes />;
}
