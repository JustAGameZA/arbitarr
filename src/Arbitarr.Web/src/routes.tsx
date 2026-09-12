import { Route, Routes } from 'react-router-dom';

import { AppShell } from './components/shell/AppShell';
import { RequireSession } from './components/shell/RequireSession';
import LoginPage from './surfaces/Login/Login';
import SetupPage from './surfaces/Login/Setup';
import DashboardPage from './surfaces/Dashboard/Dashboard';
import SearchPage from './surfaces/Search/Search';
import RulesPage from './surfaces/Rules/Rules';
import SuppressionsPage from './surfaces/Suppressions/Suppressions';
import ActivityPage from './surfaces/Activity/Activity';
import LibraryPage from './surfaces/Library/Library';
import SettingsPage from './surfaces/Settings/Settings';
import SystemPage from './surfaces/System/System';
import NotFoundPage from './pages/NotFound';

/**
 * The route table.
 *
 * Every route -- the catch-all included -- is a child of the AppShell layout
 * route, which is what keeps the chrome mounted across navigation: React Router
 * swaps only the <Outlet /> content, so the sidebar never remounts and a 404
 * still renders with navigation available.
 *
 * The paths here are the same eight the sidebar links to (SidebarNav.NAV_ENTRIES);
 * routing.test.tsx walks the nav and asserts each destination resolves to
 * something other than the not-found page, so a nav entry pointing at a path
 * with no route fails rather than silently rendering the 404.
 *
 * #44 ADDS TWO ROUTES OUTSIDE THE SHELL, AND THEIR PLACEMENT IS LOAD-BEARING.
 * /login and /setup are SIBLINGS of the shell route, not children of it, for two
 * reasons that must both keep holding:
 *
 *  1. A signed-out visitor has no navigation to offer, so rendering them inside
 *     the sidebar/top-bar chrome would show an operator a menu of pages they
 *     cannot open.
 *  2. More importantly, they sit outside <RequireSession>, which is what makes a
 *     redirect loop impossible: a guard that wrapped its own redirect target
 *     would send /login to /login forever. Moving either of these inside the
 *     guarded branch reintroduces exactly that. See RequireSession's own note
 *     for the three properties this depends on.
 *
 * They are deliberately NOT in routes.titles.ts's ROUTES table either: that table
 * is the sidebar's eight surfaces, and SidebarNav's count comment says eight.
 */
export function AppRoutes() {
  return (
    <Routes>
      {/* Outside the shell AND outside the guard -- see the note above. */}
      <Route path="login" element={<LoginPage />} />
      <Route path="setup" element={<SetupPage />} />
      <Route
        element={
          <RequireSession>
            <AppShell />
          </RequireSession>
        }
      >
        <Route index element={<DashboardPage />} />
        <Route path="search" element={<SearchPage />} />
        <Route path="rules" element={<RulesPage />} />
        <Route path="suppressions" element={<SuppressionsPage />} />
        <Route path="activity" element={<ActivityPage />} />
        <Route path="library" element={<LibraryPage />} />
        <Route path="settings" element={<SettingsPage />} />
        <Route path="system" element={<SystemPage />} />
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}
