import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import VersionDiff from './VersionDiff.vue';

/**
 * The expander, and the two things it must never get wrong.
 *
 * It must not fetch a comparison nobody asked to read — eight waiting rows would otherwise be
 * eight comparisons computed on page load, which is the cost this whole change exists to avoid.
 * And it must not draw a diff with no rows in it for a version that had nothing before it: an
 * empty diff and "there was nothing to compare against" look identical and mean opposite things.
 */

const fetched: string[] = [];

let answer: { first: boolean; diff: unknown } = { first: true, diff: null };

vi.mock('../lib/api', () => {
  class FakeApiError extends Error {}

  return {
    ApiError: FakeApiError,
    api: {
      get: vi.fn(async (url: string) => {
        fetched.push(url);
        return answer;
      }),
    },
  };
});

vi.mock('../lib/toast', () => ({ toast: vi.fn() }));

const PATH = '/projects/p/endpoints/e/versions/v/diff';

function changed() {
  return {
    matches: false,
    rows: [
      {
        index: 0, path: '$', leaf: '$', depth: 0, kind: 'Unchanged',
        expected: null, actual: null, reason: null, rulePath: null, ruleKind: null,
        hasChildren: true, hasFindings: true,
      },
      {
        index: 1, path: '$.price', leaf: 'price', depth: 1, kind: 'Changed',
        expected: '10', actual: '12', reason: null, rulePath: null, ruleKind: null,
        hasChildren: false, hasFindings: true,
      },
    ],
    counts: { Changed: 1 },
    findingIndexes: [1],
    invalidRules: [],
    failureMessage: null,
    baselineVersion: 'v2',
    statusCode: 200,
    durationMs: 0,
  };
}

function open() {
  return mount(VersionDiff, {
    props: {
      path: PATH,
      endpointName: 'GET /products',
      number: 3,
      panelId: 'version-diff-v',
    },
    attachTo: document.body,
  });
}

describe('VersionDiff', () => {
  beforeEach(() => {
    fetched.length = 0;
    answer = { first: true, diff: null };
  });

  it('asks for nothing until somebody opens it, and says so on the control', async () => {
    const row = open();
    const button = row.find('button');

    expect(fetched).toHaveLength(0);
    expect(button.attributes('aria-expanded')).toBe('false');
    // The control has to point at what it controls, or the state it announces belongs to nothing.
    expect(button.attributes('aria-controls')).toBe('version-diff-v');
    expect(row.find('#version-diff-v').exists()).toBe(true);

    await button.trigger('click');
    await flushPromises();

    expect(fetched).toEqual([PATH]);
    expect(button.attributes('aria-expanded')).toBe('true');
  });

  it('fetches once however often it is opened and closed', async () => {
    answer = { first: false, diff: changed() };

    const row = open();

    await row.find('button').trigger('click');
    await flushPromises();
    await row.find('button').trigger('click');
    await row.find('button').trigger('click');
    await flushPromises();

    expect(fetched).toHaveLength(1);
  });

  it('says a version with nothing before it is the first answer, rather than drawing an empty diff', async () => {
    const row = open();

    await row.find('button').trigger('click');
    await flushPromises();

    expect(row.find('.approval-first').exists()).toBe(true);
    expect(row.text()).toContain('first answer');

    // The viewer would have reported "no differences", which is the opposite of what is true.
    expect(row.find('.diff').exists()).toBe(false);
  });

  it('hands a real comparison to the shared viewer rather than drawing its own', async () => {
    answer = { first: false, diff: changed() };

    const row = open();

    await row.find('button').trigger('click');
    await flushPromises();

    expect(row.find('.approval-first').exists()).toBe(false);
    expect(row.find('.diff').exists()).toBe(true);
    expect(row.text()).toContain('price');
  });

  it('closes when another row opens, so one keypress does not walk three diffs at once', async () => {
    answer = { first: false, diff: changed() };

    const row = open();

    await row.find('button').trigger('click');
    await flushPromises();
    expect(row.find('button').attributes('aria-expanded')).toBe('true');

    document.dispatchEvent(
      new CustomEvent('proofflow:approval-diff', { detail: 'version-diff-somebody-else' }));
    await flushPromises();

    expect(row.find('button').attributes('aria-expanded')).toBe('false');
  });
});
