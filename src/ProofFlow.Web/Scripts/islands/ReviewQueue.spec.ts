import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import ReviewQueue from './ReviewQueue.vue';
import type { SampleRow, SampleStatus } from './dataTypes';

/**
 * The queue's decisions, without a server.
 *
 * Everything here is about a mistake that would be silent. `a` deciding about the wrong sample,
 * "approve" sending forty ids when one was under the cursor, a keystroke firing while somebody is
 * typing a note — none of those look wrong on screen, and all of them change what a baseline says
 * is correct.
 */

const posted: { url: string; body: unknown }[] = [];

/*
  The class is declared inside the factory, not above it.

  vi.mock is hoisted above the imports, so the factory runs while every top-level binding in this
  file is still in its temporal dead zone. `posted` survives that because the factory only closes
  over it and reads it later; a class referenced by value at factory time does not.
*/
vi.mock('../lib/api', () => {
  class FakeApiError extends Error {}

  return {
    ApiError: FakeApiError,
    api: {
      get: vi.fn(async (url: string) => {
        if (url.includes('/diff')) {
          return {
            matches: true, rows: [], counts: {}, findingIndexes: [], invalidRules: [],
            failureMessage: null, baselineVersion: null, statusCode: 200, durationMs: 1,
          };
        }

        return {
          session: {
            id: 's', mode: 'Capture', status: 'Completed', totalRows: 4,
            completed: 4, differing: 2, failed: 0, stoppedReason: null,
            counts: { Captured: 3, Failed: 1 },
          },
          total: 4,
          rows: ROWS,
        };
      }),
      post: vi.fn(async (url: string, body: unknown) => {
        posted.push({ url, body });
        return { reviewed: 1 };
      }),
    },
  };
});

vi.mock('../lib/toast', () => ({ toast: vi.fn() }));

function row(key: string, status: SampleStatus, differs: boolean): SampleRow {
  return {
    id: `id-${key}`,
    key,
    ordinal: Number(key),
    status,
    differs,
    statusCode: differs ? 200 : 200,
    durationMs: 4,
    failureMessage: status === 'Failed' ? 'The host refused the connection.' : null,
    diffCounts: differs ? { Changed: 2 } : {},
    reviewNote: null,
  };
}

const ROWS: SampleRow[] = [
  row('1', 'Captured', true),
  row('2', 'Captured', false),
  row('3', 'Captured', true),
  row('4', 'Failed', false),
];

let mounted: ReturnType<typeof mount> | null = null;

async function open(canReview = true) {
  // Unmounted between tests, and the reason is worth stating: the keyboard listener is on the
  // document, so a component left mounted by an earlier test still answers `a` and approves things
  // in a test that never pressed anything.
  const queue = mount(ReviewQueue, {
    props: { base: '/projects/p/endpoints/e/tests/s', canReview },
    attachTo: document.body,
  });

  mounted = queue;
  await flushPromises();
  await flushPromises();
  return queue;
}

function press(key: string): void {
  document.dispatchEvent(new KeyboardEvent('keydown', { key }));
}

describe('ReviewQueue', () => {
  beforeEach(() => { posted.length = 0; });
  afterEach(() => { mounted?.unmount(); mounted = null; });

  it('opens the first sample rather than waiting to be clicked', async () => {
    const queue = await open();

    expect(queue.findAll('.sample')).toHaveLength(4);
    expect(queue.find('.sample.is-cursor .sample-key').text()).toBe('1');
  });

  it('walks the list with j and k', async () => {
    const queue = await open();

    press('j');
    await flushPromises();
    expect(queue.find('.sample.is-cursor .sample-key').text()).toBe('2');

    press('j');
    await flushPromises();
    expect(queue.find('.sample.is-cursor .sample-key').text()).toBe('3');

    press('k');
    await flushPromises();
    expect(queue.find('.sample.is-cursor .sample-key').text()).toBe('2');
  });

  it('decides about the row under the cursor when nothing is selected', async () => {
    const queue = await open();

    press('j');
    await flushPromises();
    press('a');
    await flushPromises();

    expect(posted).toHaveLength(1);
    expect(posted[0]!.body).toMatchObject({ sampleIds: ['id-2'], status: 'Approved' });
    void queue;
  });

  it('decides about the selection when there is one', async () => {
    const queue = await open();

    await queue.findAll('.sample input[type="checkbox"]')[0]!.setValue(true);
    await queue.findAll('.sample input[type="checkbox"]')[2]!.setValue(true);

    press('r');
    await flushPromises();

    expect(posted).toHaveLength(1);
    expect(posted[0]!.body).toMatchObject({ sampleIds: ['id-1', 'id-3'], status: 'Rejected' });
  });

  it('leaves the keys alone while somebody is typing', async () => {
    const queue = await open();
    const field = document.createElement('input');
    document.body.appendChild(field);

    field.dispatchEvent(new KeyboardEvent('keydown', { key: 'a', bubbles: true }));
    await flushPromises();

    expect(posted).toHaveLength(0);
    field.remove();
    void queue;
  });

  it('decides nothing at all without the capability', async () => {
    const queue = await open(false);

    expect(queue.findAll('.sample input[type="checkbox"]')).toHaveLength(0);
    expect(queue.find('.review-actions').exists()).toBe(false);

    press('a');
    await flushPromises();
    expect(posted).toHaveLength(0);
  });

  it('says why a failed sample failed instead of calling it a difference', async () => {
    const queue = await open();
    const failed = queue.findAll('.sample')[3]!;

    expect(failed.find('.sample-failed').text()).toContain('refused');
    expect(failed.text()).toContain('Request failed');
  });

  it('shows the shape of a difference without loading the bodies', async () => {
    const queue = await open();

    // The queue has diff counts from the sweep, so a row can say "2 changed" before anything is
    // fetched. That is what makes a two-thousand-sample list open at all.
    expect(queue.findAll('.sample')[0]!.find('.sample-summary').text()).toContain('2');
    expect(queue.findAll('.sample')[1]!.find('.sample-summary').text()).toBe('Matches');
  });
});
