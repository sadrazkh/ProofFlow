/**
 * Light, dark, or whatever the operating system says.
 *
 * The choice is applied before first paint by a small inline script in the layout — this module
 * only handles switching afterwards. Splitting it that way is what avoids the white flash on a
 * dark-mode load: a module cannot run early enough, because it arrives as a deferred bundle.
 */

export type ThemeChoice = 'light' | 'dark' | 'system';

const STORAGE_KEY = 'proofflow-theme';

export function currentChoice(): ThemeChoice {
  const stored = localStorage.getItem(STORAGE_KEY);
  return stored === 'light' || stored === 'dark' || stored === 'system' ? stored : 'system';
}

export function resolve(choice: ThemeChoice): 'light' | 'dark' {
  if (choice !== 'system') return choice;
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function apply(choice: ThemeChoice): void {
  const root = document.documentElement;
  const dark = resolve(choice) === 'dark';

  root.classList.toggle('dark', dark);
  // data-theme on the document, data-theme-choice on the buttons. They meant different things
  // under one name, so the button selector below matched <html> and gave it aria-pressed.
  root.dataset.theme = choice;

  // The address bar and the task switcher take their colour from this. Left unchanged, a dark
  // page gets a white chrome bar above it on mobile.
  document.querySelector('meta[name="theme-color"]')
    ?.setAttribute('content', dark ? '#100e1d' : '#f9f8fc');

  document.querySelectorAll<HTMLElement>('button[data-theme-choice]').forEach((button) => {
    button.setAttribute('aria-pressed', String(button.dataset.themeChoice === choice));
  });
}

export function setChoice(choice: ThemeChoice): void {
  localStorage.setItem(STORAGE_KEY, choice);
  apply(choice);
  // Persisted server-side too, so the choice follows the account to another browser. Failure is
  // not worth surfacing: the local setting already took effect.
  void fetch('/settings/theme', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrfToken() },
    body: JSON.stringify({ choice }),
  }).catch(() => undefined);
}

export function csrfToken(): string {
  return document.querySelector<HTMLInputElement>('input[name="__RequestVerificationToken"]')?.value ?? '';
}

export function mountThemeControls(): void {
  apply(currentChoice());

  document.querySelectorAll<HTMLElement>('button[data-theme-choice]').forEach((button) => {
    button.addEventListener('click', () => {
      const choice = button.dataset.themeChoice as ThemeChoice | undefined;
      if (choice) setChoice(choice);
    });
  });

  // "System" has to keep meaning system: follow the OS while that is what was chosen.
  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (currentChoice() === 'system') apply('system');
  });
}
