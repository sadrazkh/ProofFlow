import { createApp, type Component } from 'vue';

/**
 * The islands registry.
 *
 * Razor renders the page; Vue is mounted only onto the regions that genuinely need reactivity — a
 * canvas, a diff viewer, a request builder. Everything else stays server-rendered HTML, which is
 * why a page still shows its content with JavaScript disabled or still downloading.
 *
 * A mount point declares itself with `data-island="name"` and passes its props as `data-*`
 * attributes or one `data-props` JSON blob.
 */

type Mounter = (element: HTMLElement) => void;

const registry = new Map<string, Mounter>();

/** Registers a component under a name, reading its props from `data-props` on the element. */
export function island<T extends Component>(name: string, component: T): void {
  registry.set(name, (element) => {
    const props = readProps(element);
    const app = createApp(component, props);
    app.mount(element);
    element.dataset.islandMounted = 'true';
  });
}

/** Registers a mounter that needs to do something beyond `createApp`. */
export function customIsland(name: string, mounter: Mounter): void {
  registry.set(name, mounter);
}

export function mountIslands(root: ParentNode = document): void {
  for (const [name, mount] of registry) {
    root.querySelectorAll<HTMLElement>(`[data-island="${name}"]`).forEach((element) => {
      if (element.dataset.islandMounted === 'true') return;
      try {
        mount(element);
      } catch (error) {
        // One island failing must not take the rest of the page with it. The region stays as the
        // server rendered it, which for most of them is a usable read-only view.
        console.error(`Island "${name}" failed to mount.`, error);
        element.dataset.islandFailed = 'true';
      }
    });
  }

  document.dispatchEvent(new CustomEvent('proofflow:content-changed'));
}

function readProps(element: HTMLElement): Record<string, unknown> {
  const raw = element.dataset.props;
  if (!raw) return {};

  try {
    return JSON.parse(raw) as Record<string, unknown>;
  } catch (error) {
    console.error('An island had malformed data-props.', error);
    return {};
  }
}
