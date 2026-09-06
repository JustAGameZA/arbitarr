import { Route, Routes } from 'react-router-dom';

import { AppShell } from './components/shell/AppShell';
import DashboardPage from './pages/Dashboard';
import SearchPage from './pages/Search';
import RulesPage from './pages/Rules';
import SuppressionsPage from './pages/Suppressions';
import SettingsPage from './pages/Settings';
import SystemPage from './pages/System';
import NotFoundPage from './pages/NotFound';

/**
 * The route table.
 *
 * Every route -- the catch-all included -- is a child of the AppShell layout
 * route, which is what keeps the chrome mounted across navigation: React Router
 * swaps only the <Outlet /> content, so the sidebar never remounts and a 404
 * still renders with navigation available.
 *
 * The paths here are the same six the sidebar links to (SidebarNav.NAV_ENTRIES);
 * routing.test.tsx walks the nav and asserts each destination resolves to
 * something other than the not-found page, so a nav entry pointing at a path
 * with no route fails rather than silently rendering the 404.
 */
export function AppRoutes() {
  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route index element={<DashboardPage />} />
        <Route path="search" element={<SearchPage />} />
        <Route path="rules" element={<RulesPage />} />
        <Route path="suppressions" element={<SuppressionsPage />} />
        <Route path="settings" element={<SettingsPage />} />
        <Route path="system" element={<SystemPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
