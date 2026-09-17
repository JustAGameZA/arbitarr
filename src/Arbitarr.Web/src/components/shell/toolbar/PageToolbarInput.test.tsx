import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { PageToolbar } from './PageToolbar';
import { PageToolbarInput } from './PageToolbarInput';
import { PageToolbarSection } from './PageToolbarSection';

describe('PageToolbarInput', () => {
  it('renders a text box whose accessible name is its label', () => {
    render(
      <PageToolbar label="Logs filters">
        <PageToolbarSection align="end">
          <PageToolbarInput label="Message" value="" onChange={() => {}} />
        </PageToolbarSection>
      </PageToolbar>,
    );

    // By ROLE and name, not by test id: the name is what an operator using a
    // screen reader hears and what the surface tests query, so asserting it
    // here is asserting the contract rather than the markup.
    const input = screen.getByRole('textbox', { name: 'Message' });
    expect(input).toHaveValue('');

    const toolbar = screen.getByRole('toolbar', { name: 'Logs filters' });
    expect(within(toolbar).getByRole('textbox', { name: 'Message' })).toBe(input);
  });

  it('gives each instance its own label association', () => {
    render(
      <PageToolbar label="Logs filters">
        <PageToolbarSection align="end">
          <PageToolbarInput label="Message" value="" onChange={() => {}} />
          <PageToolbarInput label="Query key" value="" onChange={() => {}} />
        </PageToolbarSection>
      </PageToolbar>,
    );

    // Two fields on one row must not share an id -- a duplicated `htmlFor`
    // would point both labels at the first box, so clicking the second label
    // would focus the wrong control while both still "have" a label.
    const message = screen.getByRole('textbox', { name: 'Message' });
    const queryKey = screen.getByRole('textbox', { name: 'Query key' });
    expect(message.id).not.toBe(queryKey.id);
  });

  it('reports each keystroke to its caller without holding a value of its own', async () => {
    const seen: string[] = [];
    const user = userEvent.setup();

    render(
      <PageToolbar label="Logs filters">
        <PageToolbarSection align="end">
          <PageToolbarInput label="Message" value="pro" onChange={(next) => seen.push(next)} />
        </PageToolbarSection>
      </PageToolbar>,
    );

    // The component is controlled and owns no debounce: the caller decides what
    // a keystroke costs. Typing against a fixed `value` prop must still report
    // the change rather than swallowing it, which is what proves the timer lives
    // in LogsTab and not in here.
    await user.type(screen.getByRole('textbox', { name: 'Message' }), 'b');
    expect(seen).toEqual(['prob']);
  });

  it('applies on Enter only for a caller that asked for it', async () => {
    let submits = 0;
    const user = userEvent.setup();

    const { rerender } = render(
      <PageToolbar label="Logs filters">
        <PageToolbarSection align="end">
          <PageToolbarInput label="Query key" value="" onChange={() => {}} />
        </PageToolbarSection>
      </PageToolbar>,
    );

    // A live-filtering caller (Logs) passes no onSubmit, and Enter must then do
    // nothing rather than throw -- the positive control below is what proves
    // this half is not vacuous.
    await user.type(screen.getByRole('textbox', { name: 'Query key' }), '{Enter}');
    expect(submits).toBe(0);

    rerender(
      <PageToolbar label="Logs filters">
        <PageToolbarSection align="end">
          <PageToolbarInput
            label="Query key"
            value=""
            onChange={() => {}}
            onSubmit={() => {
              submits += 1;
            }}
          />
        </PageToolbarSection>
      </PageToolbar>,
    );

    // Enter-to-apply is what the <form> Suppressions replaced gave for free; a
    // toolbar button is a type="button", so losing this would be a silent
    // keyboard regression.
    await user.type(screen.getByRole('textbox', { name: 'Query key' }), '{Enter}');
    expect(submits).toBe(1);
  });

  /**
   * The same per-member rule PageToolbar.test.tsx asserts for the row: AC2b
   * gives PageHeader the single <h1> per route, and a field that captioned
   * itself with a heading rather than a <label> would break it from inside the
   * toolbar.
   */
  it('contributes no heading of any level', () => {
    render(
      <PageToolbar label="Logs filters">
        <PageToolbarSection align="end">
          <PageToolbarInput label="Message" value="" onChange={() => {}} />
        </PageToolbarSection>
      </PageToolbar>,
    );

    const toolbar = screen.getByRole('toolbar', { name: 'Logs filters' });
    expect(within(toolbar).queryAllByRole('heading')).toHaveLength(0);
  });
});
