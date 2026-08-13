import { chromium, type Browser, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { projectId as pickProject } from './tools/project';

/**
 * Fills the demo workspace with a baseline that was genuinely captured.
 *
 * The seeder deliberately fabricates nothing beyond an account and some project names. A baseline
 * with an invented response hash would pass every test and then fail the first time something
 * compared it against a real response — so the demo baseline is made the way a person makes one:
 * through the interface, against the local fake API, using the real endpoints.
 *
 * It drives /fake/volatile on purpose. Three of its five fields change on every call, so the
 * comparison it sets up shows the actual working state of this feature — findings, dynamic-field
 * suggestions, and a decision to make — rather than a green "identical" that proves only that the
 * page renders.
 *
 * Idempotent: it stops at the first step that has already been done.
 *
 *   npx tsx e2e/demo.ts             (with the application running on :5290)
 */

const BASE = process.env.PROOFFLOW_URL ?? 'http://localhost:5290';
const EMAIL = process.env.PROOFFLOW_EMAIL ?? 'demo@proofflow.local';
const PASSWORD = process.env.PROOFFLOW_PASSWORD ?? '';

const OUT = resolve(import.meta.dirname, '../../../docs/ui/raw');

const ENVIRONMENT_NAME = 'Local fake API';
const BASELINE_NAME = 'volatile';

async function signIn(page: Page): Promise<void> {
  await page.goto(`${BASE}/account/sign-in`, { waitUntil: 'networkidle' });
  await page.fill('#Email', EMAIL);
  await page.fill('#Password', PASSWORD);
  await page.click('button[type="submit"]');
  await page.waitForLoadState('networkidle');

  if (page.url().includes('sign-in')) throw new Error('Sign-in failed. Check PROOFFLOW_PASSWORD.');
}

async function firstProjectId(page: Page): Promise<string> {
  const id = await pickProject(page, BASE);

  if (!id) throw new Error('No project found. Is Demo:Seed on?');
  return id;
}

/**
 * Adds the environment if it is not already there.
 *
 * Two steps, because the page has two forms and that is deliberate: the short one at the bottom
 * creates, and the full one appears once there is something to edit. The private-network switch
 * only exists on the second, which is the right place for it — allowing a request to loopback is a
 * decision about an environment that exists, not a field on a create form nobody reads.
 */
async function ensureEnvironment(page: Page, projectId: string): Promise<void> {
  await page.goto(`${BASE}/projects/${projectId}/environments`, { waitUntil: 'networkidle' });

  if (await page.locator('.master-item').filter({ hasText: ENVIRONMENT_NAME }).count() === 0) {
    await page.fill('#new-env-name', ENVIRONMENT_NAME);
    await page.fill('#new-env-url', `${BASE}/fake`);
    await page.click('#new-environment button[type="submit"]');
    await page.waitForLoadState('networkidle');
    console.log('Environment created.');
  }

  await page.locator('.master-item').filter({ hasText: ENVIRONMENT_NAME }).first().click();
  await page.waitForSelector('#env-name');

  // The fake API is on the same host as the application, which the guard treats as loopback and
  // refuses by default. Saying so explicitly is the point of the switch.
  const privateNetwork = page.locator('input[name="AllowPrivateNetwork"]');
  if (!await privateNetwork.isChecked()) {
    await privateNetwork.check();
    await page.locator('form[data-guard-unsaved] button[type="submit"]').first().click();
    await page.waitForLoadState('networkidle');
    console.log('Loopback allowed for the environment.');
  }
}

/** Sends a request and records the response as a baseline, through the interface. */
async function ensureBaseline(page: Page, projectId: string): Promise<void> {
  await page.goto(`${BASE}/projects/${projectId}/endpoints`, { waitUntil: 'networkidle' });

  if (await page.getByRole('link', { name: BASELINE_NAME, exact: true }).count() > 0) {
    console.log('Baseline already present.');
    return;
  }

  await page.goto(`${BASE}/projects/${projectId}/request`, { waitUntil: 'networkidle' });
  await page.waitForSelector('[data-island-mounted="true"]');

  await page.fill('.url-input, input[name="url"], .request-url input', '{{environment.baseUrl}}/volatile')
    .catch(async () => {
      // The lab's URL box is an island, so its selector is whatever the component renders. Fall
      // back to the first monospace text box on the page rather than guessing twice.
      await page.locator('input.input-mono').first().fill('{{environment.baseUrl}}/volatile');
    });

  await page.getByRole('button', { name: /send|ارسال/i }).click();
  await page.waitForSelector('.response-head', { timeout: 20_000 });

  await page.getByRole('button', { name: /save as baseline|ذخیره به‌عنوان/i }).click();
  await page.waitForSelector('[role="dialog"]');
  await page.locator('[role="dialog"] input').first().fill(BASELINE_NAME);
  await page.locator('[role="dialog"] .btn-primary').click();

  await page.waitForURL(/\/endpoints\/[0-9a-f-]+$/i, { timeout: 20_000 });
  console.log('Baseline captured.');
}

/**
 * Runs one comparison and leaves the result on screen.
 *
 * Against /fake/volatile this always finds something, which is the state worth photographing: the
 * summary bar with counts in it, the suggestion list, and an accept decision waiting to be made.
 */
async function compareOnce(page: Page, projectId: string): Promise<string> {
  await page.goto(`${BASE}/projects/${projectId}/endpoints`, { waitUntil: 'networkidle' });
  const href = await page.getByRole('link', { name: BASELINE_NAME, exact: true }).first()
    .getAttribute('href');

  if (!href) throw new Error('The baseline was not created.');

  await page.goto(`${BASE}${href}`, { waitUntil: 'networkidle' });
  await page.waitForSelector('[data-island-mounted="true"]');

  await page.locator('.workbench-bar .btn-primary').first().click();
  await page.waitForSelector('.diff-summary', { timeout: 20_000 });

  console.log('Comparison run.');
  return href;
}

/**
 * Photographs the compared state.
 *
 * The screenshot matrix cannot reach this: a comparison is not persisted, so a page loaded cold
 * shows "nothing compared yet" — which is correct, and useless for reviewing the one screen this
 * whole phase is about. So the shot is taken here, while the result is on screen.
 */
async function shootCompared(
  browser: Browser, href: string, language: 'fa' | 'en', theme: 'light' | 'dark',
): Promise<void> {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 1100 },
    locale: language === 'fa' ? 'fa-IR' : 'en-GB',
    colorScheme: theme,
    deviceScaleFactor: 2,
  });

  await context.addInitScript(`localStorage.setItem('proofflow-theme', '${theme}')`);
  await context.addCookies([
    { name: '.AspNetCore.Culture', value: `c=${language}|uic=${language}`, url: BASE },
    { name: 'proofflow.tz', value: 'Asia%2FTehran', url: BASE },
  ]);

  const page = await context.newPage();
  await signIn(page);

  await page.goto(`${BASE}${href}`, { waitUntil: 'networkidle' });
  await page.waitForSelector('[data-island-mounted="true"]');
  await page.locator('.workbench-bar .btn-primary').first().click();
  await page.waitForSelector('.diff-summary', { timeout: 20_000 });
  await page.waitForTimeout(400);

  await page.screenshot({
    path: resolve(OUT, `baseline-compared--${language}-${theme}-desktop.png`), fullPage: true });

  // Side by side, which only exists above 900px and so can only be photographed here.
  await page.locator('.diff-summary .segmented button').nth(1).click();
  await page.waitForTimeout(250);
  await page.screenshot({
    path: resolve(OUT, `baseline-split--${language}-${theme}-desktop.png`), fullPage: true });

  await page.locator('.segmented button').first().click();
  await page.waitForTimeout(150);

  await page.locator('.segmented').last().click().catch(() => undefined);
  await page.locator('.workbench > .segmented button').nth(1).click();
  await page.waitForTimeout(250);
  await page.screenshot({
    path: resolve(OUT, `baseline-rules--${language}-${theme}-desktop.png`), fullPage: true });

  await context.close();
}

/**
 * Walks the loop to its end: the three dynamic fields become rules, and the next comparison is
 * clean.
 *
 * This is the part worth proving. A diff that renders is a page; a diff whose findings can be
 * turned into a decision that actually silences them is the feature, and nothing short of running
 * it twice shows that.
 */
async function applySuggestionsAndReCompare(page: Page, href: string): Promise<void> {
  await page.goto(`${BASE}${href}`, { waitUntil: 'networkidle' });
  await page.waitForSelector('[data-island-mounted="true"]');
  await page.locator('.workbench-bar .btn-primary').first().click();
  await page.waitForSelector('.diff-summary', { timeout: 20_000 });

  if (await page.locator('.suggestions').count() === 0) {
    console.log('Nothing left to suggest; rules are already in place.');
  } else {
    await page.locator('.suggestion-foot .btn-ghost').first().click();
    await page.locator('.suggestion-foot .btn-primary').click();
    await page.waitForSelector('.suggestions', { state: 'detached', timeout: 20_000 });
    console.log('Suggestions saved as rules.');
  }

  await page.locator('.workbench-bar .btn-primary').first().click();
  await page.waitForSelector('.diff-summary', { timeout: 20_000 });

  const identical = await page.locator('.diff-summary .badge-pass').count() > 0;
  console.log(identical
    ? 'Compared again under the new rules: identical.'
    : 'Compared again: still reporting differences.');

  await page.screenshot({
    path: resolve(OUT, 'baseline-identical--en-light-desktop.png'), fullPage: true });

  if (!identical) throw new Error('The rules did not silence the dynamic fields.');
}

async function main(): Promise<void> {
  if (!PASSWORD) throw new Error('Set PROOFFLOW_PASSWORD.');

  await mkdir(OUT, { recursive: true });

  const browser = await chromium.launch();
  const page = await browser.newPage();

  try {
    await signIn(page);
    const projectId = await firstProjectId(page);

    await ensureEnvironment(page, projectId);
    await ensureBaseline(page, projectId);
    const href = await compareOnce(page, projectId);
    await page.close();

    for (const language of ['en', 'fa'] as const) {
      for (const theme of ['light', 'dark'] as const) {
        await shootCompared(browser, href, language, theme);
      }
    }

    const last = await browser.newPage();
    await signIn(last);
    await applySuggestionsAndReCompare(last, href);

    console.log(`Demo baseline ready at ${BASE}${href}; four compared-state shots in ${OUT}`);
  } finally {
    await browser.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
