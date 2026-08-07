import { t } from './i18n';

/**
 * The confirmation people get after doing something.
 *
 * The brief asks for clear feedback after every important operation. A toast is the cheapest form
 * of it, so this is deliberately small: no queueing library, no store, one stack in the corner.
 *
 * Errors do not auto-dismiss. A success message that disappears is fine; an error message that
 * disappears before it is read leaves someone believing the thing worked.
 */

type ToastKind = 'success' | 'error' | 'warn' | 'info';

const ICONS: Record<ToastKind, string> = {
  success: 'circle-check',
  error: 'circle-alert',
  warn: 'triangle-alert',
  info: 'info',
};

let stack: HTMLElement | null = null;

function container(): HTMLElement {
  if (stack?.isConnected) return stack;

  stack = document.createElement('div');
  stack.className = 'toast-stack';
  // Announced by a screen reader without stealing focus. `polite` rather than `assertive`: these
  // confirm an action the person just took, so interrupting them mid-sentence is rude, not urgent.
  stack.setAttribute('role', 'status');
  stack.setAttribute('aria-live', 'polite');
  document.body.appendChild(stack);
  return stack;
}

export function toast(message: string, kind: ToastKind = 'info', timeoutMs?: number): void {
  const element = document.createElement('div');
  element.className = `toast toast-${kind}`;

  const icon = document.createElement('i');
  icon.setAttribute('data-lucide', ICONS[kind]);
  element.appendChild(icon);

  const text = document.createElement('div');
  text.className = 'grow';
  text.textContent = message;
  element.appendChild(text);

  const close = document.createElement('button');
  close.className = 'btn btn-ghost btn-icon btn-sm';
  close.setAttribute('aria-label', t('action.close'));
  close.innerHTML = '<i data-lucide="x"></i>';
  close.addEventListener('click', () => element.remove());
  element.appendChild(close);

  container().appendChild(element);
  document.dispatchEvent(new CustomEvent('proofflow:content-changed'));

  const timeout = timeoutMs ?? (kind === 'error' ? 0 : 4500);
  if (timeout > 0) window.setTimeout(() => element.remove(), timeout);
}

/**
 * The buttons on the design reference that raise one of each kind.
 *
 * Lives here rather than in a script tag on that page so the reference shows the *real* toast,
 * built by the real function — a demo with its own copy of the markup is a demo that stops
 * matching. Finds nothing and returns on every other page.
 */
export function mountToastDemos(): void {
  document.querySelectorAll<HTMLElement>('[data-design-toast]').forEach((button) => {
    button.addEventListener('click', () => {
      const kind = (button.dataset.designToast ?? 'info') as ToastKind;
      const messages: Record<ToastKind, string> = {
        success: 'Project «Orders API» is ready.',
        error: 'The request did not reach the server. Nothing was changed.',
        warn: 'Two baselines are waiting for approval.',
        info: 'Nothing has run in this project yet.',
      };
      toast(messages[kind], kind);
    });
  });
}

/**
 * Messages the server left for us in the layout, shown once on load.
 *
 * Carried through TempData so a redirect-after-post can still say what happened — the page that
 * performed the action is gone by the time the reader sees anything.
 */
export function flushServerToasts(): void {
  document.querySelectorAll<HTMLElement>('[data-toast]').forEach((element) => {
    const kind = (element.dataset.toastKind as ToastKind | undefined) ?? 'info';
    if (element.dataset.toast) toast(element.dataset.toast, kind);
    element.remove();
  });
}
