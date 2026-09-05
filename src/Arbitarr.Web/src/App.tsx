import { Route, Routes } from 'react-router-dom';

/**
 * Route stubs only. PR #3 replaces this with the real shell (sidebar, top bar,
 * content pane) and PR #4 ports the five surfaces into these slots.
 */
export default function App() {
  return (
    <Routes>
      <Route path="/" element={<h1>Arbitarr</h1>} />
    </Routes>
  );
}
