import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { TabPanel, Tabs, useTabs } from './Tabs';

type Id = 'one' | 'two' | 'three';

const ITEMS = [
  { id: 'one', label: 'One' },
  { id: 'two', label: 'Two' },
  { id: 'three', label: 'Three' },
] as const satisfies readonly { id: Id; label: string }[];

function Harness({ initial = 'one' as Id }: { initial?: Id }) {
  const tabs = useTabs<Id>(initial);

  return (
    <>
      <Tabs label="Example sections" items={ITEMS} tabs={tabs} />
      <TabPanel id={tabs.activeId} tabs={tabs}>
        Content for {tabs.activeId}
      </TabPanel>
    </>
  );
}

describe('Tabs', () => {
  it('renders a named tablist with the active tab selected and a tab stop', () => {
    render(<Harness />);

    const tablist = screen.getByRole('tablist', { name: 'Example sections' });
    expect(tablist).toBeInTheDocument();

    const one = screen.getByRole('tab', { name: 'One' });
    const two = screen.getByRole('tab', { name: 'Two' });
    expect(one).toHaveAttribute('aria-selected', 'true');
    expect(one).toHaveAttribute('tabIndex', '0');
    expect(two).toHaveAttribute('aria-selected', 'false');
    // Only the active tab is a tab stop -- Tab alone must not step through
    // every tab as a separate stop.
    expect(two).toHaveAttribute('tabIndex', '-1');
  });

  it('wires aria-controls on the tab and aria-labelledby on the panel to the same pair of ids', () => {
    render(<Harness />);

    const activeTab = screen.getByRole('tab', { name: 'One' });
    const panel = screen.getByRole('tabpanel');

    expect(activeTab.getAttribute('aria-controls')).toBe(panel.id);
    expect(panel.getAttribute('aria-labelledby')).toBe(activeTab.id);
    expect(panel).toHaveTextContent('Content for one');
  });

  it('moves selection and focus with ArrowRight, wrapping from the last tab to the first', async () => {
    render(<Harness />);
    const user = userEvent.setup();

    const one = screen.getByRole('tab', { name: 'One' });
    one.focus();

    await user.keyboard('{ArrowRight}');
    const two = screen.getByRole('tab', { name: 'Two' });
    expect(two).toHaveAttribute('aria-selected', 'true');
    expect(two).toHaveFocus();

    await user.keyboard('{ArrowRight}');
    const three = screen.getByRole('tab', { name: 'Three' });
    expect(three).toHaveAttribute('aria-selected', 'true');
    expect(three).toHaveFocus();

    // Wraparound: past the last tab, selection returns to the first rather
    // than staying put or throwing. A naive extraction that drops the modulo
    // wraparound (e.g. clamping the index instead) fails this assertion by
    // leaving "Three" selected here.
    await user.keyboard('{ArrowRight}');
    const oneAgain = screen.getByRole('tab', { name: 'One' });
    expect(oneAgain).toHaveAttribute('aria-selected', 'true');
    expect(oneAgain).toHaveFocus();
  });

  it('moves selection and focus with ArrowLeft, wrapping from the first tab to the last', async () => {
    render(<Harness />);
    const user = userEvent.setup();

    const one = screen.getByRole('tab', { name: 'One' });
    one.focus();

    // Wraparound at the other end: ArrowLeft from the first tab must reach
    // the last one, not merely refuse to move.
    await user.keyboard('{ArrowLeft}');
    const three = screen.getByRole('tab', { name: 'Three' });
    expect(three).toHaveAttribute('aria-selected', 'true');
    expect(three).toHaveFocus();
  });

  it('selects a tab on click without requiring a keyboard event', async () => {
    render(<Harness />);
    const user = userEvent.setup();

    await user.click(screen.getByRole('tab', { name: 'Three' }));

    expect(screen.getByRole('tab', { name: 'Three' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tabpanel')).toHaveTextContent('Content for three');
  });

  it('ignores keys other than the arrow keys', async () => {
    render(<Harness />);
    const user = userEvent.setup();

    const one = screen.getByRole('tab', { name: 'One' });
    one.focus();

    await user.keyboard('{Enter}');
    expect(one).toHaveAttribute('aria-selected', 'true');
  });
});
