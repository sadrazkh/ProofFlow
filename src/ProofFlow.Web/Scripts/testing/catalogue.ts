import { beforeAll } from 'vitest';
import en from '../../Resources/en.json';
import { initTranslations } from '../lib/i18n';

/**
 * Gives the components under test the real English catalogue.
 *
 * Loaded rather than stubbed, and flattened here exactly as the server flattens it, so a component
 * that asks for a key nobody wrote renders the key itself and any assertion on its text fails
 * loudly. Stubbing `t` would make every one of those tests pass.
 */
function flatten(node: unknown, prefix: string, into: Record<string, string>): void {
  if (typeof node === 'string') {
    into[prefix] = node;
    return;
  }

  if (node && typeof node === 'object') {
    for (const [key, value] of Object.entries(node)) {
      flatten(value, prefix ? `${prefix}.${key}` : key, into);
    }
  }
}

beforeAll(() => {
  const flat: Record<string, string> = {};
  flatten(en, '', flat);

  const element = document.createElement('script');
  element.type = 'application/json';
  element.id = 'pf-i18n';
  element.textContent = JSON.stringify(flat);
  document.head.appendChild(element);

  initTranslations();
});
