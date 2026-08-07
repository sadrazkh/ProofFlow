import { api, ApiError } from './api';
import { t } from './i18n';
import { toast } from './toast';

/**
 * Reveals one secret, briefly.
 *
 * Worth being precise about what this is and is not. The hiding is a courtesy against someone
 * walking past a screen; it is not a security control, and nothing here pretends otherwise — the
 * value has already been sent to this browser and could be read from the network tab by whoever
 * asked for it. The controls that matter are on the server: the capability, the audit entry
 * written before the value is handed over, and one secret per request.
 *
 * What this does add is that a revealed value does not stay on screen: thirty seconds, or the
 * moment the tab loses focus, whichever comes first.
 */

const VISIBLE_MS = 30_000;

export function mountSecretReveal(): void {
  const timers = new Map<string, number>();

  const hide = (id: string) => {
    const holder = document.querySelector<HTMLElement>(`[data-secret-id="${id}"]`);
    const mask = holder?.querySelector<HTMLElement>('[data-secret-mask]');
    const shown = holder?.querySelector<HTMLElement>('[data-secret-shown]');

    shown?.remove();
    if (mask) mask.hidden = false;

    const timer = timers.get(id);
    if (timer) { window.clearTimeout(timer); timers.delete(id); }

    document.querySelector<HTMLElement>(`[data-secret-reveal="${id}"]`)
      ?.setAttribute('aria-pressed', 'false');
  };

  document.querySelectorAll<HTMLElement>('[data-secret-reveal]').forEach((button) => {
    button.setAttribute('aria-pressed', 'false');

    button.addEventListener('click', async () => {
      const id = button.dataset.secretReveal!;
      const url = button.dataset.secretUrl!;
      const holder = document.querySelector<HTMLElement>(`[data-secret-id="${id}"]`);
      if (!holder) return;

      // A second press hides it again, rather than fetching the value — and writing a second
      // audit entry — for something already on screen.
      if (holder.querySelector('[data-secret-shown]')) {
        hide(id);
        return;
      }

      try {
        const result = await api.post<{ value: string }>(url);

        const mask = holder.querySelector<HTMLElement>('[data-secret-mask]');
        if (mask) mask.hidden = true;

        const shown = document.createElement('span');
        shown.dataset.secretShown = 'true';
        shown.className = 'secret-revealed';
        // textContent, never innerHTML: this string came from a database and could contain
        // anything, including markup somebody stored on purpose.
        shown.textContent = result.value;
        holder.appendChild(shown);

        button.setAttribute('aria-pressed', 'true');
        timers.set(id, window.setTimeout(() => hide(id), VISIBLE_MS));
      } catch (error) {
        toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
      }
    });
  });

  // Switching tab or window hides everything. Screen shares and screenshots are the realistic way
  // a revealed value escapes, and both start with attention going elsewhere.
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState !== 'visible') timers.forEach((_, id) => hide(id));
  });
  window.addEventListener('blur', () => timers.forEach((_, id) => hide(id)));
}
