/**
 * Strings for the Vue islands.
 *
 * Section 8 of the brief forbids hard-coding text inside components, and the mechanism matters
 * more than the rule: without one, the first component under deadline gets an English string
 * inlined "just for now" and the Persian panel grows an English paragraph in the middle of it.
 *
 * So Razor emits the subset of the catalogue a page needs into a script tag, and every island
 * reads from here. A component that wants a word it was not given renders the key — visibly wrong,
 * which is the point.
 */

type Catalogue = Record<string, string>;

let catalogue: Catalogue = {};
let direction: 'rtl' | 'ltr' = 'ltr';
let language = 'en';

export function initTranslations(): void {
  const element = document.getElementById('pf-i18n');
  if (element?.textContent) {
    try {
      catalogue = JSON.parse(element.textContent) as Catalogue;
    } catch {
      // A malformed payload must not take the page down with it. Keys render as themselves.
      catalogue = {};
    }
  }

  direction = document.documentElement.dir === 'rtl' ? 'rtl' : 'ltr';
  language = document.documentElement.lang || 'en';
}

/** Looks up `key`, substituting {0}, {1}, … positionally. */
export function t(key: string, ...args: (string | number)[]): string {
  const template = catalogue[key];
  if (template === undefined) return key;
  if (args.length === 0) return template;

  return template.replace(/\{(\d+)\}/g, (match, index: string) => {
    const value = args[Number(index)];
    return value === undefined ? match : String(value);
  });
}

/** Everything under a prefix, with the prefix stripped — what a component is handed at mount. */
export function subset(prefix: string): Catalogue {
  const result: Catalogue = {};
  for (const [key, value] of Object.entries(catalogue)) {
    if (key.startsWith(prefix)) result[key.slice(prefix.length)] = value;
  }
  return result;
}

export function isRtl(): boolean {
  return direction === 'rtl';
}

export function currentLanguage(): string {
  return language;
}

/**
 * A duration a person can read.
 *
 * Kept out of the components because every one of them shows durations and they must agree — a run
 * list saying "1.2s" beside a node inspector saying "1200ms" reads as two different numbers.
 */
export function formatDuration(milliseconds: number): string {
  if (milliseconds < 1000) return t('time.milliseconds', Math.round(milliseconds));
  if (milliseconds < 60_000) return t('time.seconds', (milliseconds / 1000).toFixed(1));

  const minutes = Math.floor(milliseconds / 60_000);
  const seconds = Math.round((milliseconds % 60_000) / 1000);
  return `${minutes}:${String(seconds).padStart(2, '0')}`;
}
