import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import LibraryPage from './Library';
import { renderSurface } from '../../test/renderSurface';

/**
 * A minimal smoke test for the placeholder (arb-6l9b.5). This bead only wires
 * the surface into the shell -- no data fetching, no tabs -- so there is
 * nothing to mock; bead 6 (arr-queues-screen plan, §4.2) adds the real
 * content and its own coverage.
 */
describe('Library (placeholder)', () => {
  it('renders one page header and an empty state', () => {
    renderSurface(<LibraryPage />);

    // AC2b: exactly one <h1> per view.
    const headings = screen.getAllByRole('heading', { level: 1 });
    expect(headings).toHaveLength(1);
    expect(headings[0]).toHaveTextContent('Library');

    expect(
      screen.getByText('Sonarr and Radarr queues and libraries will appear here.'),
    ).toBeInTheDocument();
  });
});
