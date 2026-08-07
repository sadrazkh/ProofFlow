import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import DiffViewer from './DiffViewer.vue';
import type { DiffKind, DiffResult, DiffRow } from './baselineTypes';

/**
 * The viewer's job is to not lie about a large response.
 *
 * Every test here is about something that would be invisible if it went wrong: a hidden row that
 * was never counted, an ignored field quietly dropped, an accept that sends more paths than were
 * ticked. All three produce a page that looks right.
 */

let nextIndex = 0;

function row(kind: DiffKind, path: string, overrides: Partial<DiffRow> = {}): DiffRow {
  return {
    index: nextIndex++,
    path,
    leaf: path.split('.').pop() ?? path,
    depth: 1,
    kind,
    expected: 'before',
    actual: 'after',
    reason: null,
    rulePath: null,
    ruleKind: null,
    hasChildren: false,
    hasFindings: kind !== 'Unchanged' && kind !== 'Ignored',
    ...overrides,
  };
}

function result(rows: DiffRow[]): DiffResult {
  const counts: Record<string, number> = {};
  for (const entry of rows) {
    if (entry.kind !== 'Unchanged') counts[entry.kind] = (counts[entry.kind] ?? 0) + 1;
  }

  return {
    matches: rows.every((entry) => entry.kind === 'Unchanged' || entry.kind === 'Ignored'),
    rows,
    counts,
    findingIndexes: rows
      .filter((entry) => entry.kind !== 'Unchanged' && entry.kind !== 'Ignored')
      .map((entry) => entry.index),
    invalidRules: [],
    failureMessage: null,
    baselineVersion: 'v3',
    statusCode: 200,
    durationMs: 42,
  };
}

function fixture(): DiffResult {
  nextIndex = 0;
  return result([
    { ...row('Unchanged', '$'), depth: 0, hasChildren: true, leaf: '$' },
    row('Unchanged', '$.id'),
    row('Changed', '$.name'),
    row('Unchanged', '$.kind'),
    row('Added', '$.extra'),
    row('Ignored', '$.timestamp', { reason: 'Ignored by rule' }),
    row('Removed', '$.gone'),
  ]);
}

function open(diff: DiffResult | null = fixture(), canAccept = true) {
  return mount(DiffViewer, {
    props: { result: diff, pending: false, canAccept },
    attachTo: document.body,
  });
}

describe('DiffViewer', () => {
  it('hides unchanged rows but says how many it hid', async () => {
    const viewer = open();
    const paths = viewer.findAll('.diff-row .diff-path').map((node) => node.text());

    expect(paths).not.toContain('id');
    expect(paths).not.toContain('kind');
    // Two hidden — the root stays because it is depth 0, which is what carries "this is a response".
    expect(viewer.find('.diff-foot .check-row').text()).toContain('2');

    await viewer.find('.diff-foot input[type="checkbox"]').setValue(true);
    expect(viewer.findAll('.diff-row')).toHaveLength(7);
  });

  it('keeps ignored rows on screen', () => {
    // A diff that silently drops what a rule set aside cannot be audited: the reader's next
    // question is always "what else did you not show me".
    const viewer = open();
    expect(viewer.find('.diff-row.is-ignored').exists()).toBe(true);
  });

  it('gives every category its own marker, so colour is never the only signal', () => {
    const viewer = open();
    const markers = viewer.findAll('.diff-row').map((node) => ({
      kind: [...node.classes()].find((name) => name.startsWith('is-')),
      marker: node.find('.diff-marker').text(),
    }));

    const changed = markers.find((entry) => entry.kind === 'is-changed');
    const added = markers.find((entry) => entry.kind === 'is-added');
    const removed = markers.find((entry) => entry.kind === 'is-removed');

    expect(changed?.marker).toBe('~');
    expect(added?.marker).toBe('+');
    expect(removed?.marker).toBe('−');
    expect(new Set([changed?.marker, added?.marker, removed?.marker]).size).toBe(3);
  });

  it('emits only the paths that were ticked', async () => {
    const viewer = open();

    const checkboxes = viewer.findAll('.diff-accept input');
    expect(checkboxes.length).toBe(3);

    await checkboxes[0]!.setValue(true);
    await viewer.find('.diff-foot .btn-primary').trigger('click');

    expect(viewer.emitted('accept')).toHaveLength(1);
    expect(viewer.emitted('accept')![0]![0]).toEqual(['$.name']);
  });

  it('offers no acceptance at all without the capability', () => {
    const viewer = open(fixture(), false);
    expect(viewer.findAll('.diff-accept input')).toHaveLength(0);
    expect(viewer.find('.diff-foot .btn-primary').exists()).toBe(false);
  });

  it('steps through findings with n and wraps round', async () => {
    const viewer = open();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'n' }));
    await viewer.vm.$nextTick();
    expect(viewer.find('.diff-row.is-cursor .diff-path').text()).toBe('name');

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'n' }));
    await viewer.vm.$nextTick();
    expect(viewer.find('.diff-row.is-cursor .diff-path').text()).toBe('extra');

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'p' }));
    await viewer.vm.$nextTick();
    expect(viewer.find('.diff-row.is-cursor .diff-path').text()).toBe('name');
  });

  it('leaves n and p alone while somebody is typing', async () => {
    const viewer = open();
    const field = document.createElement('input');
    document.body.appendChild(field);

    field.dispatchEvent(new KeyboardEvent('keydown', { key: 'n', bubbles: true }));
    await viewer.vm.$nextTick();

    expect(viewer.find('.diff-row.is-cursor').exists()).toBe(false);
    field.remove();
  });

  it('gives every row two aligned cells in side-by-side mode', async () => {
    const viewer = open();

    // Inline: a row shows only the cells it has, and an arrow between them when it has both.
    expect(viewer.findAll('.diff-row.is-added .diff-old')).toHaveLength(0);
    expect(viewer.findAll('.diff-row.is-changed .diff-arrow')).toHaveLength(1);

    await viewer.findAll('.diff-summary .segmented button')[1]!.trigger('click');

    // Split: both cells always exist, so the two columns line up down the whole list, and the
    // arrow goes because the columns say the same thing.
    expect(viewer.find('section.diff').classes()).toContain('is-split');
    expect(viewer.findAll('.diff-row.is-added .diff-old')).toHaveLength(1);
    expect(viewer.find('.diff-row.is-added .diff-old').text()).toBe('—');
    expect(viewer.findAll('.diff-arrow')).toHaveLength(0);
  });

  it('says a request failed instead of showing an empty comparison', () => {
    const viewer = open({ ...fixture(), rows: [], failureMessage: 'The host refused the connection.' });

    expect(viewer.text()).toContain('The host refused the connection.');
    expect(viewer.find('.diff-scroll').exists()).toBe(false);
  });

  it('renders a bounded number of rows however large the response', () => {
    // Forty thousand fields is one page of search results from a catalogue API, not an edge case.
    // Building that many rows locks the tab for seconds, so the whole point of this component is
    // that the count on screen does not depend on the count in the response.
    nextIndex = 0;
    const many = result([
      { ...row('Unchanged', '$'), depth: 0, hasChildren: true, leaf: '$' },
      ...Array.from({ length: 40_000 }, (_, i) => row('Changed', `$.items[${i}].value`)),
    ]);

    const viewer = open(many);

    expect(viewer.props('result')!.rows).toHaveLength(40_001);
    // One viewport plus overscan at both ends — a constant, not a fraction of the total.
    expect(viewer.findAll('.diff-row').length).toBeLessThan(60);
    // And the scroll surface still describes the whole list, so the bar is honest about its size.
    expect(viewer.find('.diff-spacer').attributes('style')).toContain('1200030px');
  });

  it('names the rules it could not read rather than dropping them', () => {
    const viewer = open({ ...fixture(), invalidRules: ['$.items[', '$..broken'] });
    expect(viewer.find('.response-notice').text()).toContain('$.items[');
  });
});
