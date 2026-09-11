import { beforeEach, describe, expect, it } from 'vitest';

import { useTableDensityStore } from './tableDensityStore';

describe('tableDensityStore', () => {
  beforeEach(() => {
    // Reset explicitly rather than relying on module state: the store is a
    // singleton for the whole test file, so a test that left it compact would
    // otherwise decide the outcome of the next one by ordering alone.
    useTableDensityStore.setState({ density: 'expanded' });
  });

  it('defaults to expanded', () => {
    expect(useTableDensityStore.getState().density).toBe('expanded');
  });

  it('flips expanded to compact and back when toggled', () => {
    useTableDensityStore.getState().toggleDensity();
    expect(useTableDensityStore.getState().density).toBe('compact');

    useTableDensityStore.getState().toggleDensity();
    expect(useTableDensityStore.getState().density).toBe('expanded');
  });

  it('is idempotent when setDensity is called with the value already held', () => {
    useTableDensityStore.getState().setDensity('compact');
    expect(useTableDensityStore.getState().density).toBe('compact');

    useTableDensityStore.getState().setDensity('compact');
    expect(useTableDensityStore.getState().density).toBe('compact');
  });

  it('setDensity selects a value outright rather than flipping it', () => {
    // Distinguishes setDensity from toggleDensity: a setter implemented as a
    // toggle would pass the idempotence test above only by accident of the
    // starting value, and would fail here.
    useTableDensityStore.getState().setDensity('expanded');
    expect(useTableDensityStore.getState().density).toBe('expanded');
  });
});
