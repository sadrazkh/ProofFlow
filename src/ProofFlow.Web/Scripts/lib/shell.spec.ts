import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mountCommandPalette } from './shell';

/**
 * The command palette, once it has two kinds of row in it.
 *
 * What is checked here is the thing that was wrong and could not be seen: the rows used to be
 * collected once at mount, so anything appended afterwards was on screen, clickable with a mouse,
 * and invisible to the arrow keys and to `aria-activedescendant`. A keyboard user would have
 * walked the destinations, reached the last one, and wrapped back to the top straight past the
 * results they had just asked for.
 */

const ANSWER = {
  groups: [
    {
      labelKey: 'nav.endpoints',
      items: [
        { title: 'GET /orders', subtitle: 'Billing', path: '/projects/p/endpoints/1', icon: 'target' },
        { title: 'POST /orders', subtitle: 'Billing', path: '/projects/p/endpoints/2', icon: 'target' },
      ],
    },
  ],
};

let fetchMock: ReturnType<typeof vi.fn>;

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

function markup(): void {
  document.body.innerHTML = `
    <div class="palette-overlay hidden" data-palette aria-hidden="true" data-palette-project="p">
      <div data-palette-backdrop></div>
      <input data-palette-input role="combobox" />
      <div id="pf-palette-list" role="listbox">
        <a class="menu-item" data-palette-item href="/" id="pf-palette-0" role="option"
           aria-selected="false" data-search="dashboard /"></a>
        <a class="menu-item" data-palette-item href="/projects" id="pf-palette-1" role="option"
           aria-selected="false" data-search="projects /projects"></a>
        <div data-palette-results></div>
        <div class="hidden" data-palette-loading role="status"></div>
        <div class="hidden" data-palette-empty role="status"></div>
      </div>
    </div>`;
}

const input = () => document.querySelector<HTMLInputElement>('[data-palette-input]')!;
const loading = () => document.querySelector('[data-palette-loading]')!;
const empty = () => document.querySelector('[data-palette-empty]')!;
const named = () => input().getAttribute('aria-activedescendant');

const rows = () => [...document.querySelectorAll('[data-palette-item]')];
const shown = () => rows().filter((row) => !row.classList.contains('hidden'));
const highlighted = () => document.querySelector('.is-selected')?.textContent?.trim() ?? '';

function type(value: string): void {
  input().value = value;
  input().dispatchEvent(new Event('input', { bubbles: true }));
}

function press(key: string, ctrlKey = false): void {
  document.dispatchEvent(new KeyboardEvent('keydown', { key, ctrlKey, bubbles: true }));
}

describe('the command palette', () => {
  beforeEach(() => {
    markup();

    fetchMock = vi.fn();
    fetchMock.mockImplementation(async () => json(ANSWER));
    vi.stubGlobal('fetch', fetchMock);

    mountCommandPalette();
    press('k', true);
  });

  afterEach(() => {
    // Closes every palette this file has mounted, including the detached ones whose document-level
    // key handler is still registered. Without it they keep reacting to the next test's keystrokes.
    press('Escape');
    vi.unstubAllGlobals();
    document.body.innerHTML = '';
  });

  it('asks the server for the things somebody made and puts them under the destinations', async () => {
    type('orders');

    await vi.waitFor(() => expect(rows()).toHaveLength(4));

    const asked = fetchMock.mock.calls[0]![0] as string;
    expect(asked).toContain('/search?q=orders');
    expect(asked).toContain('project=p');

    // Below the destinations rather than mixed into them.
    expect(rows()[0]!.id).toBe('pf-palette-0');
    expect(rows()[2]!.textContent).toContain('GET /orders');
    expect(rows()[2]!.getAttribute('href')).toBe('/projects/p/endpoints/1');

    // The group is named for a screen reader, and named from the catalogue rather than by the
    // server — the response carried the key «nav.endpoints» and nothing readable.
    expect(document.querySelector('[role="group"]')?.getAttribute('aria-label')).toBe('Endpoints');

    // One debounced request for the whole word, not one per keystroke.
    expect(fetchMock.mock.calls).toHaveLength(1);
  });

  it('lets the arrow keys walk onto rows that arrived after it was mounted', async () => {
    // «orders» matches no destination, so everything still visible is a found row — which is the
    // case the old code could not reach at all.
    type('orders');
    await vi.waitFor(() => expect(shown()).toHaveLength(2));

    expect(highlighted()).toContain('GET /orders');
    expect(named()).toBe('pf-palette-found-0');

    press('ArrowDown');
    expect(highlighted()).toContain('POST /orders');
    expect(named()).toBe('pf-palette-found-1');

    press('Home');
    expect(named()).toBe('pf-palette-found-0');

    // Round the top and out the bottom, over the combined list.
    press('ArrowUp');
    expect(named()).toBe('pf-palette-found-1');

    press('End');
    expect(named()).toBe('pf-palette-found-1');
  });

  it('never names a row that is not on the page', async () => {
    type('orders');
    await vi.waitFor(() => expect(named()).toBe('pf-palette-found-0'));

    expect(document.getElementById(named()!)).not.toBeNull();

    // Emptying the box drops the found rows, and the id has to go with them: a screen reader left
    // pointed at an element that no longer exists announces nothing at all.
    type('');
    await vi.waitFor(() => expect(rows()).toHaveLength(2));

    expect(named()).toBe('pf-palette-0');
    expect(document.getElementById(named()!)).not.toBeNull();
  });

  it('says nothing matched only once it knows that', async () => {
    let answer: () => void = () => {};
    const held = new Promise<void>((resolve) => { answer = resolve; });

    fetchMock.mockImplementation(async () => {
      await held;
      return json({ groups: [] });
    });

    type('nothinglikethis');

    // While it is still looking, «nothing matches that» is not yet true — and this one is a live
    // region, so saying it early means saying it out loud.
    await vi.waitFor(() => expect(loading().classList.contains('hidden')).toBe(false));
    expect(empty().classList.contains('hidden')).toBe(true);

    answer();

    await vi.waitFor(() => expect(loading().classList.contains('hidden')).toBe(true));
    expect(empty().classList.contains('hidden')).toBe(false);
  });

  it('keeps the destinations when the search cannot be reached', async () => {
    fetchMock.mockImplementation(async () => { throw new Error('the network is gone'); });

    type('projects');

    await vi.waitFor(() => expect(loading().classList.contains('hidden')).toBe(true));

    // A search that failed is not an answer of «you have nothing».
    expect(shown()).toHaveLength(1);
    expect(shown()[0]!.id).toBe('pf-palette-1');
  });
});
