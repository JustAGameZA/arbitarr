import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { PageToolbarMenu } from './PageToolbarMenu';
import { PageToolbarMenuItem } from './PageToolbarMenuItem';

function renderMenu(onSelect: (value: string) => void, checked = 'all') {
  return render(
    <div>
      <button type="button">Outside</button>
      <PageToolbarMenu label="Kind: Everything">
        <PageToolbarMenuItem
          label="Everything"
          checked={checked === 'all'}
          onSelect={() => onSelect('all')}
        />
        <PageToolbarMenuItem
          label="Decisions"
          checked={checked === 'decision'}
          onSelect={() => onSelect('decision')}
        />
      </PageToolbarMenu>
    </div>,
  );
}

describe('PageToolbarMenu', () => {
  it('starts closed, with no panel and an unexpanded trigger', () => {
    renderMenu(() => {});

    expect(screen.getByRole('button', { name: 'Kind: Everything' })).toHaveAttribute(
      'aria-expanded',
      'false',
    );
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  });

  it('opens on the trigger and closes on it again', async () => {
    renderMenu(() => {});
    const trigger = screen.getByRole('button', { name: 'Kind: Everything' });

    await userEvent.click(trigger);
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('menu')).toBeInTheDocument();
    // The panel is only addressable if the trigger points at it.
    expect(trigger.getAttribute('aria-controls')).toBe(screen.getByRole('menu').id);

    await userEvent.click(trigger);
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  });

  it('closes on Escape and returns focus to the trigger', async () => {
    renderMenu(() => {});
    const trigger = screen.getByRole('button', { name: 'Kind: Everything' });

    await userEvent.click(trigger);
    await userEvent.keyboard('{Escape}');

    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    // Without the explicit refocus this lands on <body> and a keyboard user
    // restarts their traversal from the top of the document.
    expect(trigger).toHaveFocus();
  });

  it('closes on a pointer press outside it', async () => {
    renderMenu(() => {});

    await userEvent.click(screen.getByRole('button', { name: 'Kind: Everything' }));
    expect(screen.getByRole('menu')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Outside' }));
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  });

  it('invokes onSelect exactly once with the chosen value and closes', async () => {
    const onSelect = vi.fn();
    renderMenu(onSelect);

    await userEvent.click(screen.getByRole('button', { name: 'Kind: Everything' }));
    await userEvent.click(screen.getByRole('menuitemradio', { name: 'Decisions' }));

    // Once, not merely "called": the panel's own click handler also fires on
    // this event, and a handler wired to both would double-apply the filter.
    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith('decision');
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  });

  it('marks the active item checked and the others unchecked', async () => {
    renderMenu(() => {}, 'decision');

    await userEvent.click(screen.getByRole('button', { name: 'Kind: Everything' }));

    expect(screen.getByRole('menuitemradio', { name: 'Decisions' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
    expect(screen.getByRole('menuitemradio', { name: 'Everything' })).toHaveAttribute(
      'aria-checked',
      'false',
    );
  });

  it('renders a toggle item as a checkbox rather than a radio', async () => {
    render(
      <PageToolbarMenu label="View" align="end">
        <PageToolbarMenuItem label="Compact rows" kind="checkbox" checked onSelect={() => {}} />
      </PageToolbarMenu>,
    );

    await userEvent.click(screen.getByRole('button', { name: 'View' }));

    expect(screen.getByRole('menuitemcheckbox', { name: 'Compact rows' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
  });
});
