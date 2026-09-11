import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { PageToolbar } from './PageToolbar';
import { PageToolbarButton } from './PageToolbarButton';
import { PageToolbarSection } from './PageToolbarSection';
import { PageToolbarSeparator } from './PageToolbarSeparator';

describe('PageToolbar', () => {
  it('renders a named toolbar landmark holding both sections in DOM order', () => {
    render(
      <PageToolbar label="Activity actions">
        <PageToolbarSection>
          <PageToolbarButton label="Refresh" onClick={() => {}} />
        </PageToolbarSection>
        <PageToolbarSection align="end">
          <PageToolbarButton label="Filter" onClick={() => {}} />
        </PageToolbarSection>
      </PageToolbar>,
    );

    const toolbar = screen.getByRole('toolbar', { name: 'Activity actions' });
    const buttons = within(toolbar).getAllByRole('button');

    // The order matters: the left section carries context and actions, the
    // right one the menus. Asserting the sequence catches a swap that a
    // per-button lookup would not.
    expect(buttons.map((button) => button.textContent)).toEqual(['Refresh', 'Filter']);
  });

  /**
   * AC2b: exactly one <h1> per route, owned by PageHeader. The toolbar sits
   * directly beneath it, so a heading added here — at any level — is the most
   * likely way that rule gets broken. pageTitle.test.tsx guards the h1 count
   * across routes; this guards the component in isolation, so the cause is
   * named at the point of the mistake rather than as a distant route failure.
   */
  it('contributes no heading of any level', () => {
    render(
      <PageToolbar label="Activity actions">
        <PageToolbarSection>
          <PageToolbarButton label="Refresh" onClick={() => {}} />
        </PageToolbarSection>
      </PageToolbar>,
    );

    const toolbar = screen.getByRole('toolbar', { name: 'Activity actions' });
    expect(within(toolbar).queryAllByRole('heading')).toHaveLength(0);
  });

  it('does not expose the separator to assistive technology', () => {
    render(
      <PageToolbar label="Activity actions">
        <PageToolbarSection>
          <PageToolbarButton label="Refresh" onClick={() => {}} />
          <PageToolbarSeparator />
        </PageToolbarSection>
      </PageToolbar>,
    );

    const toolbar = screen.getByRole('toolbar', { name: 'Activity actions' });
    expect(within(toolbar).queryAllByRole('separator')).toHaveLength(0);
  });

  it('does not fire a disabled button', async () => {
    let clicks = 0;
    render(
      <PageToolbar label="Activity actions">
        <PageToolbarSection>
          <PageToolbarButton
            label="Refresh"
            disabled
            onClick={() => {
              clicks += 1;
            }}
          />
        </PageToolbarSection>
      </PageToolbar>,
    );

    const button = screen.getByRole('button', { name: 'Refresh' });
    expect(button).toBeDisabled();
    expect(clicks).toBe(0);
  });
});
