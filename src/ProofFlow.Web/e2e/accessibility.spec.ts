import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * Automated accessibility checks on every page, in both themes and both languages.
 *
 * axe finds perhaps a third of what is wrong — it cannot tell whether a label makes sense or
 * whether a keyboard path is usable — but the third it finds is the third that is tedious to
 * check by hand and easy to regress: contrast that drifts when a token changes, a control that
 * loses its name, a heading level that skips.
 *
 * Serious and critical fail the build. Minor and moderate are reported but do not, because a gate
 * that fires on a decorative-image warning is a gate people learn to skip.
 */

const BASE = process.env.PROOFFLOW_URL ?? 'http://localhost:5290';
const PASSWORD = process.env.PROOFFLOW_PASSWORD ?? '';

const ANONYMOUS = [
  { name: 'sign in', path: '/account/sign-in' },
  { name: 'sign up', path: '/account/sign-up' },
  { name: 'not found', path: '/no-such-page' },
];

const AUTHENTICATED = [
  { name: 'dashboard', path: '/' },
  { name: 'projects', path: '/projects' },
  { name: 'new project', path: '/projects/new' },
  { name: 'activity', path: '/activity' },
  { name: 'design system', path: '/design' },
];

/**
 * Pages inside a project. Their address depends on a project existing, so it is discovered once
 * and shared — a hard-coded id would pass by auditing a 404 page.
 */
const PROJECT_PAGES = [
  { name: 'environments', path: (id: string) => `/projects/${id}/environments` },
  { name: 'request lab', path: (id: string) => `/projects/${id}/request` },
];

let projectId: string | null = null;

async function firstProject(page: Page): Promise<string | null> {
  if (projectId) return projectId;

  await page.goto(`${BASE}/projects`, { waitUntil: 'networkidle' });
  const href = await page.locator('a.project-card').first().getAttribute('href').catch(() => null);
  projectId = href?.split('/').pop() ?? null;
  return projectId;
}

/**
 * Refuses to audit a page whose stylesheet did not load.
 *
 * This is not paranoia — it happened. A frontend rebuild changes Vite's hashed filenames, the
 * running application kept serving the previous ones, every page rendered as unstyled HTML, and
 * the whole accessibility suite went green: black text on a white background passes every contrast
 * rule there is. A gate that reports success when the thing under test is missing is worse than
 * no gate, so the presence of the design system is checked before anything is measured.
 */
async function assertStyled(page: Page, label: string): Promise<void> {
  const accent = await page.evaluate(() =>
    getComputedStyle(document.documentElement).getPropertyValue('--accent').trim());

  expect(accent, `${label} rendered without the stylesheet — run "npm run build".`).not.toBe('');
}

async function audit(page: Page, label: string): Promise<void> {
  await assertStyled(page, label);

  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze();

  const blocking = results.violations.filter(
    (v) => v.impact === 'serious' || v.impact === 'critical',
  );

  for (const violation of results.violations) {
    const nodes = violation.nodes.map((n) => n.target.join(' ')).slice(0, 3).join(', ');
    console.log(`[${violation.impact}] ${label}: ${violation.id} — ${violation.help} (${nodes})`);
  }

  expect(blocking, blocking.map((v) => `${v.id}: ${v.help}`).join('\n')).toEqual([]);
}

/** Confirms the browser is on the page whose name the result will carry. */
async function assertOn(page: Page, path: string): Promise<void> {
  const url = new URL(page.url());
  expect(url.pathname, 'the page redirected somewhere else').toBe(path);
}

for (const theme of ['light', 'dark'] as const) {
  for (const language of ['fa', 'en'] as const) {
    test.describe(`${language} · ${theme}`, () => {
      test.use({ colorScheme: theme, locale: language === 'fa' ? 'fa-IR' : 'en-GB' });

      test.beforeEach(async ({ context }) => {
        await context.addInitScript(`localStorage.setItem('proofflow-theme', '${theme}')`);
        await context.addCookies([
          { name: '.AspNetCore.Culture', value: `c=${language}|uic=${language}`, url: BASE },
        ]);
      });

      // The project carries a signed-in session, so these get a fresh context without one —
      // otherwise the sign-in page redirects to the dashboard and is never audited.
      for (const target of ANONYMOUS) {
        test(target.name, async ({ browser }) => {
          const context = await browser.newContext({
            storageState: { cookies: [], origins: [] },
            colorScheme: theme,
            locale: language === 'fa' ? 'fa-IR' : 'en-GB',
          });
          await context.addInitScript(`localStorage.setItem('proofflow-theme', '${theme}')`);
          await context.addCookies([
            { name: '.AspNetCore.Culture', value: `c=${language}|uic=${language}`, url: BASE },
          ]);

          const page = await context.newPage();
          await page.goto(`${BASE}${target.path}`, { waitUntil: 'networkidle' });
          await assertOn(page, target.path);
          await audit(page, `${target.name} (${language}/${theme})`);
          await context.close();
        });
      }

      for (const target of AUTHENTICATED) {
        test(target.name, async ({ page }) => {
          // Skipped rather than failed without a password: this suite is also run locally, where
          // nobody has seeded a demo account yet, and a red suite people expect to be red is a
          // suite nobody reads.
          test.skip(!PASSWORD, 'PROOFFLOW_PASSWORD is not set.');

          await page.goto(`${BASE}${target.path}`, { waitUntil: 'networkidle' });
          await assertOn(page, target.path);
          await audit(page, `${target.name} (${language}/${theme})`);
        });
      }

      for (const target of PROJECT_PAGES) {
        test(target.name, async ({ page }) => {
          test.skip(!PASSWORD, 'PROOFFLOW_PASSWORD is not set.');

          const id = await firstProject(page);
          expect(id, 'the demo seed should have created a project').not.toBeNull();

          const path = target.path(id!);
          await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle' });
          await assertOn(page, path);
          // The request lab is an island: give it a frame to mount before measuring, or the audit
          // is of the skeleton rather than the component.
          await page.waitForTimeout(500);
          await audit(page, `${target.name} (${language}/${theme})`);
        });
      }
    });
  }
}
