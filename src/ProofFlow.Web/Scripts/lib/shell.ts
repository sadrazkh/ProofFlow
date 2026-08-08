import { t } from './i18n';
import { toast } from './toast';

/**
 * The chrome around every page: sidebar, dropdown menus, the command palette, confirmations.
 *
 * Plain DOM rather than a Vue island. None of it holds state worth reactivity, it must work on the
 * first paint of every page including the sign-in screen, and mounting a framework to toggle a
 * class is how a 56-kilobyte shell becomes a 300-kilobyte one.
 */

const SIDEBAR_KEY = 'proofflow-sidebar';

export function mountSidebar(): void {
  const root = document.documentElement;

  if (localStorage.getItem(SIDEBAR_KEY) === 'collapsed') root.classList.add('sidebar-collapsed');

  document.querySelector('[data-sidebar-collapse]')?.addEventListener('click', () => {
    const collapsed = !root.classList.contains('sidebar-collapsed');
    root.classList.toggle('sidebar-collapsed', collapsed);
    localStorage.setItem(SIDEBAR_KEY, collapsed ? 'collapsed' : 'expanded');
  });

  const setOpen = (open: boolean) => {
    root.classList.toggle('sidebar-open', open);
    document.querySelector('[data-sidebar-toggle]')?.setAttribute('aria-expanded', String(open));
  };

  document.querySelector('[data-sidebar-toggle]')?.addEventListener('click', () =>
    setOpen(!root.classList.contains('sidebar-open')));
  document.querySelector('[data-sidebar-backdrop]')?.addEventListener('click', () => setOpen(false));

  // Following a link on a phone should reveal the page it went to, not the menu it came from.
  document.querySelectorAll('.sidebar a').forEach((link) =>
    link.addEventListener('click', () => setOpen(false)));

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') setOpen(false);
  });
}

/**
 * Dropdowns. One open at a time; outside click and Escape both close.
 *
 * Focus returns to the trigger on Escape — without that, dismissing a menu with the keyboard drops
 * the caret at the top of the document and the next Tab starts the page over.
 */
export function mountMenus(): void {
  const roots = Array.from(document.querySelectorAll<HTMLElement>('[data-menu-root]'));

  const closeAll = (except?: HTMLElement) => {
    for (const root of roots) {
      if (root === except) continue;
      root.querySelector<HTMLElement>('[data-menu]')?.classList.add('hidden');
      root.querySelector<HTMLElement>('[data-menu-trigger]')?.setAttribute('aria-expanded', 'false');
    }
  };

  for (const root of roots) {
    const trigger = root.querySelector<HTMLElement>('[data-menu-trigger]');
    const menu = root.querySelector<HTMLElement>('[data-menu]');
    if (!trigger || !menu) continue;

    // A menu is a menu to a screen reader too, and its items are menuitems. Without the roles the
    // whole thing announces as a stack of links that happens to be on screen.
    menu.setAttribute('role', 'menu');
    trigger.setAttribute('aria-haspopup', 'true');
    menu.querySelectorAll<HTMLElement>('.menu-item').forEach((item) => {
      item.setAttribute('role', 'menuitem');
      // Reachable by the arrow keys below, not by Tab: Tab should step past the whole menu.
      if (!item.hasAttribute('tabindex')) item.setAttribute('tabindex', '-1');
    });

    trigger.addEventListener('click', (event) => {
      event.stopPropagation();
      const opening = menu.classList.contains('hidden');
      closeAll(root);
      menu.classList.toggle('hidden', !opening);
      trigger.setAttribute('aria-expanded', String(opening));
      if (opening) menu.querySelector<HTMLElement>('.menu-item')?.focus();
    });

    trigger.addEventListener('keydown', (event) => {
      if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
      event.preventDefault();
      if (menu.classList.contains('hidden')) trigger.click();
      const items = menu.querySelectorAll<HTMLElement>('.menu-item');
      (event.key === 'ArrowDown' ? items[0] : items[items.length - 1])?.focus();
    });

    menu.addEventListener('keydown', (event) => {
      if (event.key !== 'Escape') return;
      closeAll();
      // Back to where the keyboard was. Without this the caret is left at the top of the document
      // and the next Tab starts the page over.
      trigger.focus();
    });

    bindMenuKeys(menu);
  }

  document.addEventListener('click', () => closeAll());
  document.addEventListener('keydown', (event) => { if (event.key === 'Escape') closeAll(); });
}

/**
 * The command palette.
 *
 * Its entries are rendered server-side from the same navigation map as the sidebar, so it can only
 * ever offer routes the signed-in member is authorised to reach. Building the list client-side
 * would mean shipping the full route table to every account, including the ones that may not see
 * most of it.
 */
export function mountCommandPalette(): void {
  const overlay = document.querySelector<HTMLElement>('[data-palette]');
  if (!overlay) return;

  const input = overlay.querySelector<HTMLInputElement>('[data-palette-input]');
  const empty = overlay.querySelector<HTMLElement>('[data-palette-empty]');
  const items = Array.from(overlay.querySelectorAll<HTMLAnchorElement>('[data-palette-item]'));
  let selected = 0;

  const visible = () => items.filter((item) => !item.classList.contains('hidden'));

  const paint = (index: number) => {
    const shown = visible();

    if (shown.length === 0) {
      selected = 0;
      // Nothing highlighted means nothing to announce. Leaving a stale id here points a screen
      // reader at a row that is filtered out and no longer on screen.
      input?.removeAttribute('aria-activedescendant');
      items.forEach((item) => item.setAttribute('aria-selected', 'false'));
      return;
    }

    selected = (index + shown.length) % shown.length;

    shown.forEach((item, i) => {
      const active = i === selected;
      item.classList.toggle('is-selected', active);
      item.setAttribute('aria-selected', String(active));
    });

    // Focus never leaves the input, so this is the only thing telling a screen reader which row
    // the arrow keys are on.
    const current = shown[selected];
    if (current?.id) input?.setAttribute('aria-activedescendant', current.id);
    current?.scrollIntoView({ block: 'nearest' });
  };

  const open = (isOpen: boolean) => {
    overlay.classList.toggle('hidden', !isOpen);
    overlay.setAttribute('aria-hidden', String(!isOpen));
    document.body.style.overflow = isOpen ? 'hidden' : '';
    if (!isOpen) return;
    if (input) { input.value = ''; input.focus(); }
    items.forEach((item) => item.classList.remove('hidden'));
    if (empty) empty.classList.add('hidden');
    paint(0);
  };

  document.querySelectorAll('[data-palette-open]').forEach((button) =>
    button.addEventListener('click', () => open(true)));
  overlay.querySelector('[data-palette-backdrop]')?.addEventListener('click', () => open(false));

  input?.addEventListener('input', () => {
    const query = input.value.trim().toLocaleLowerCase();
    for (const item of items) {
      const haystack = (item.dataset.search ?? item.textContent ?? '').toLocaleLowerCase();
      item.classList.toggle('hidden', query.length > 0 && !haystack.includes(query));
    }
    if (empty) {
      empty.classList.toggle('hidden', visible().length > 0);
      empty.textContent = t('nav.noResults');
    }
    paint(0);
  });

  document.addEventListener('keydown', (event) => {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      open(true);
      return;
    }
    if (overlay.classList.contains('hidden')) return;

    switch (event.key) {
      case 'Escape': open(false); break;
      case 'ArrowDown': event.preventDefault(); paint(selected + 1); break;
      case 'ArrowUp': event.preventDefault(); paint(selected - 1); break;
      case 'Home': event.preventDefault(); paint(0); break;
      case 'End': event.preventDefault(); paint(visible().length - 1); break;
      case 'Enter':
        event.preventDefault();
        visible()[selected]?.click();
        break;
    }
  });
}

/**
 * Arrow-key navigation inside an open menu.
 *
 * A dropdown that can only be walked with Tab leaks focus out of itself on the last item, which
 * puts the caret somewhere behind the still-open menu — the point at which a keyboard user has to
 * reach for the mouse. Escape already returned focus to the trigger; these are the rest of the
 * keys the pattern requires.
 */
function bindMenuKeys(menu: HTMLElement): void {
  menu.addEventListener('keydown', (event) => {
    const items = Array.from(menu.querySelectorAll<HTMLElement>('.menu-item:not([disabled])'))
      .filter((item) => item.offsetParent !== null);
    if (items.length === 0) return;

    const current = items.indexOf(document.activeElement as HTMLElement);
    const move = (to: number) => {
      event.preventDefault();
      items[(to + items.length) % items.length]?.focus();
    };

    switch (event.key) {
      case 'ArrowDown': move(current + 1); break;
      case 'ArrowUp': move(current - 1); break;
      case 'Home': move(0); break;
      case 'End': move(items.length - 1); break;
    }
  });
}

/**
 * Confirmation before anything irreversible.
 *
 * Destructive forms opt in with `data-confirm`, and the ones that destroy something named also
 * carry `data-confirm-phrase` — typing the name is what separates "yes, this project" from a
 * reflexive Enter on a dialog that appeared where the reader expected the page.
 */
export function mountConfirmations(): void {
  document.querySelectorAll<HTMLFormElement>('form[data-confirm]').forEach((form) => {
    form.addEventListener('submit', (event) => {
      if (form.dataset.confirmed === 'true') return;
      event.preventDefault();
      openConfirm(form);
    });
  });
}

function openConfirm(form: HTMLFormElement): void {
  void confirmAction({
    title: form.dataset.confirmTitle ?? t('action.confirm'),
    body: form.dataset.confirm ?? '',
    confirm: form.dataset.confirmAction ?? t('action.delete'),
    phrase: form.dataset.confirmPhrase,
  }).then((agreed) => {
    if (!agreed) return;

    form.dataset.confirmed = 'true';
    form.requestSubmit();
  });
}

export type Confirmation = {
  title: string;
  body: string;

  /** The word on the button that does it. Always a verb, never "OK". */
  confirm: string;

  /**
   * When set, the reader has to type it.
   *
   * What separates "yes, this project" from a reflexive Enter on a dialog that appeared where the
   * reader expected the page.
   */
  phrase?: string;
};

/**
 * Asks before something irreversible, and resolves to what the reader chose.
 *
 * One implementation for the markup-driven forms and for the islands. Two dialogs that looked
 * almost the same would be two dialogs to keep accessible, and the second one always loses.
 */
export function confirmAction(options: Confirmation): Promise<boolean> {
  const { phrase } = options;
  const overlay = document.createElement('div');
  overlay.className = 'overlay';
  overlay.setAttribute('role', 'dialog');
  overlay.setAttribute('aria-modal', 'true');

  overlay.innerHTML = `
    <div class="dialog">
      <div class="card-header">
        <div>
          <div class="card-title">${escapeHtml(options.title)}</div>
        </div>
      </div>
      <div class="card-body stack">
        <p class="muted">${escapeHtml(options.body)}</p>
        ${phrase ? `
          <div class="field">
            <label class="field-label" for="pf-confirm-phrase">${escapeHtml(t('common.typeToConfirm', phrase))}</label>
            <input class="input input-mono" id="pf-confirm-phrase" autocomplete="off" />
          </div>` : ''}
      </div>
      <div class="card-footer">
        <button type="button" class="btn btn-secondary" data-confirm-cancel>${escapeHtml(t('action.cancel'))}</button>
        <button type="button" class="btn btn-danger" data-confirm-accept ${phrase ? 'disabled' : ''}>
          ${escapeHtml(options.confirm)}
        </button>
      </div>
    </div>`;

  document.body.appendChild(overlay);

  const accept = overlay.querySelector<HTMLButtonElement>('[data-confirm-accept]')!;
  const field = overlay.querySelector<HTMLInputElement>('#pf-confirm-phrase');

  field?.addEventListener('input', () => { accept.disabled = field.value.trim() !== phrase; });
  (field ?? accept).focus();

  document.body.style.overflow = 'hidden';
  document.dispatchEvent(new CustomEvent('proofflow:content-changed'));

  return new Promise<boolean>((resolve) => {
    let settled = false;

    const close = (agreed: boolean) => {
      if (settled) return;
      settled = true;

      overlay.remove();
      document.body.style.overflow = '';
      resolve(agreed);
    };

    overlay.querySelector('[data-confirm-cancel]')?.addEventListener('click', () => close(false));
    overlay.addEventListener('click', (event) => { if (event.target === overlay) close(false); });
    overlay.addEventListener('keydown', (event) => { if (event.key === 'Escape') close(false); });
    accept.addEventListener('click', () => close(true));
  });
}

function escapeHtml(value: string): string {
  const element = document.createElement('span');
  element.textContent = value;
  return element.innerHTML;
}

/**
 * Warns before leaving a form with unsaved edits.
 *
 * Compares against the values the page loaded with rather than tracking every keystroke, so
 * typing something and undoing it does not count as a change.
 */
/**
 * The cron presets.
 *
 * A button that writes an expression into a box, rather than a select that replaces it. Cron's
 * field order is something people look up every single time, and the six rhythms anybody actually
 * wants should be one click — but the box stays editable, because the seventh rhythm always exists.
 */
export function mountCronPresets(): void {
  document.querySelectorAll<HTMLButtonElement>('button[data-cron]').forEach((button) => {
    button.addEventListener('click', () => {
      const target = document.getElementById(button.dataset.cronTarget ?? '');
      if (!(target instanceof HTMLInputElement)) return;

      target.value = button.dataset.cron ?? '';
      target.dispatchEvent(new Event('input', { bubbles: true }));
      target.focus();
    });
  });
}

/**
 * Copy buttons.
 *
 * For values that exist once and cannot be fetched again — an API key, most of all. Selecting
 * forty characters of base64 by hand is how somebody loses the last character and spends an
 * afternoon on a 401.
 */
export function mountCopyButtons(): void {
  document.querySelectorAll<HTMLButtonElement>('button[data-copy]').forEach((button) => {
    button.addEventListener('click', async () => {
      const value = button.dataset.copy ?? '';

      try {
        await navigator.clipboard.writeText(value);
      } catch {
        // Refused, usually because the page is not on a secure origin. Said rather than swallowed:
        // a button that appears to work and does not is worse than one that admits it cannot.
        toast(t('action.copyFailed'), 'warn');
        return;
      }

      const original = button.textContent;
      button.textContent = t('action.copied');

      window.setTimeout(() => { button.textContent = original; }, 1600);
    });
  });
}

export function mountUnsavedGuard(): void {
  document.querySelectorAll<HTMLFormElement>('form[data-guard-unsaved]').forEach((form) => {
    const initial = new FormData(form);
    let submitting = false;

    form.addEventListener('submit', () => { submitting = true; });

    window.addEventListener('beforeunload', (event) => {
      if (submitting) return;

      const current = new FormData(form);
      for (const [key, value] of current.entries()) {
        if (String(initial.get(key) ?? '') !== String(value)) {
          event.preventDefault();
          return;
        }
      }
    });
  });
}
