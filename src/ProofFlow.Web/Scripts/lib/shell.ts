import { t } from './i18n';

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

    trigger.addEventListener('click', (event) => {
      event.stopPropagation();
      const opening = menu.classList.contains('hidden');
      closeAll(root);
      menu.classList.toggle('hidden', !opening);
      trigger.setAttribute('aria-expanded', String(opening));
      if (opening) menu.querySelector<HTMLElement>('.menu-item')?.focus();
    });

    menu.addEventListener('keydown', (event) => {
      if (event.key !== 'Escape') return;
      closeAll();
      trigger.focus();
    });
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
    if (shown.length === 0) { selected = 0; return; }
    selected = (index + shown.length) % shown.length;
    shown.forEach((item, i) => item.classList.toggle('is-selected', i === selected));
    shown[selected]?.scrollIntoView({ block: 'nearest' });
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
      case 'Enter':
        event.preventDefault();
        visible()[selected]?.click();
        break;
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
  const phrase = form.dataset.confirmPhrase;
  const overlay = document.createElement('div');
  overlay.className = 'overlay';
  overlay.setAttribute('role', 'dialog');
  overlay.setAttribute('aria-modal', 'true');

  overlay.innerHTML = `
    <div class="dialog">
      <div class="card-header">
        <div>
          <div class="card-title">${escapeHtml(form.dataset.confirmTitle ?? t('action.confirm'))}</div>
        </div>
      </div>
      <div class="card-body stack">
        <p class="muted">${escapeHtml(form.dataset.confirm ?? '')}</p>
        ${phrase ? `
          <div class="field">
            <label class="field-label" for="pf-confirm-phrase">${escapeHtml(t('common.typeToConfirm', phrase))}</label>
            <input class="input input-mono" id="pf-confirm-phrase" autocomplete="off" />
          </div>` : ''}
      </div>
      <div class="card-footer">
        <button type="button" class="btn btn-secondary" data-confirm-cancel>${escapeHtml(t('action.cancel'))}</button>
        <button type="button" class="btn btn-danger" data-confirm-accept ${phrase ? 'disabled' : ''}>
          ${escapeHtml(form.dataset.confirmAction ?? t('action.delete'))}
        </button>
      </div>
    </div>`;

  document.body.appendChild(overlay);

  const accept = overlay.querySelector<HTMLButtonElement>('[data-confirm-accept]')!;
  const field = overlay.querySelector<HTMLInputElement>('#pf-confirm-phrase');

  field?.addEventListener('input', () => { accept.disabled = field.value.trim() !== phrase; });
  (field ?? accept).focus();

  const close = () => { overlay.remove(); document.body.style.overflow = ''; };
  overlay.querySelector('[data-confirm-cancel]')?.addEventListener('click', close);
  overlay.addEventListener('click', (event) => { if (event.target === overlay) close(); });
  overlay.addEventListener('keydown', (event) => { if (event.key === 'Escape') close(); });

  accept.addEventListener('click', () => {
    form.dataset.confirmed = 'true';
    close();
    form.requestSubmit();
  });

  document.body.style.overflow = 'hidden';
  document.dispatchEvent(new CustomEvent('proofflow:content-changed'));
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
