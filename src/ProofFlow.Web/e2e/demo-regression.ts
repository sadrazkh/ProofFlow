import { chromium, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { projectId as pickProject } from './tools/project';

/**
 * Builds an endpoint checked against a list of inputs, for real, by pressing what a person presses.
 *
 * Same principle as e2e/demo.ts and the same reason: the seeder fabricates nothing, so the demo
 * data for sample-based regression is made the way a person makes it. Every row of the data set is
 * pasted, every sample in the queue came from an actual call to the local fake API, and the
 * approvals are pressed.
 *
 * It is also the acceptance test for the endpoint page. This used to walk a nine-step wizard, and
 * the wizard existed because there was no page that could do this. If the four things it does here
 * — define, give it inputs, test, approve — cannot be done from one screen without reaching into
 * the database, the page has not replaced what it removed.
 *
 *   npx tsx e2e/demo-regression.ts        (with the application running on :5290)
 */

const BASE = process.env.PROOFFLOW_URL ?? 'http://localhost:5290';
const EMAIL = process.env.PROOFFLOW_EMAIL ?? 'demo@proofflow.local';
const PASSWORD = process.env.PROOFFLOW_PASSWORD ?? '';

const OUT = resolve(import.meta.dirname, '../../../docs/ui/raw');

/** Not the account that runs the test: nobody may approve what they started. */
const REVIEWER = 'reviewer@proofflow.local';

const ENDPOINT_NAME = 'record detail';
const DATASET_NAME = 'record ids';

/** Twelve rows: enough to fill a queue, few enough to sweep in a second. */
const IDS = Array.from({ length: 12 }, (_, index) => String(index + 1)).join('\n');

/**
 * Pinned to English before the first navigation.
 *
 * This script finds its buttons by their labels, and the application's default culture is Persian
 * — so without this it hunts for «Read this» on a page that says «بخوانش» and fails thirty seconds
 * later with a timeout that says nothing about language. Bilingual regexes were the previous
 * answer and they rot: every label this script presses is two strings that have to stay in step.
 */
async function english(page: Page): Promise<void> {
  await page.context().addCookies([
    { name: '.AspNetCore.Culture', value: 'c=en|uic=en', url: BASE },
  ]);
}

async function signIn(page: Page): Promise<void> {
  await english(page);
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
 * The inputs, pasted into the data set editor the way somebody pastes a column out of a
 * spreadsheet. Reused if an earlier run already made it, because twelve identical sets is not
 * demo data, it is mess.
 */
async function inputs(page: Page, projectId: string): Promise<void> {
  await page.goto(`${BASE}/projects/${projectId}/datasets`, { waitUntil: 'networkidle' });

  if (await page.getByRole('link', { name: DATASET_NAME }).count() > 0) {
    console.log(`Data set «${DATASET_NAME}» is already here.`);
    return;
  }

  await page.goto(`${BASE}/projects/${projectId}/datasets/new`, { waitUntil: 'networkidle' });
  await page.waitForSelector('[data-island-mounted="true"]');

  // The whole editor is one island, name and all — there is no server-rendered form here.
  await page.locator('.dataset-editor input.input').first().fill(DATASET_NAME);

  // The paste box. It reads a pasted column and says what it made of it before anything is
  // written, which is the behaviour worth exercising rather than stepping around.
  //
  // Read as CSV rather than left to the detector, and the header line is why: twelve bare numbers
  // are «one per line», which gives a single column called «value» — so the endpoint's address
  // would have to say {{dataset.current.value}}, which names the mechanism rather than the thing.
  // With a header and CSV chosen, the column is called id and the address reads like an address.
  await page.locator('.dataset-editor textarea').first().fill(`id\n${IDS}`);
  await page.locator('.dataset-editor select').first().selectOption('Csv');
  await page.getByRole('button', { name: /read this/i }).click();

  // Reading is a preview, not an import. The second press is what puts the rows in the table, and
  // that separation is deliberate — it is what lets somebody see «3 lines could not be read»
  // before anything has been written.
  await page.getByRole('button', { name: /use these \d+ rows/i }).click();
  await page.waitForTimeout(400);

  await page.getByRole('button', { name: /^create$/i }).click();
  await page.waitForURL(/\/datasets\/[0-9a-f-]+$/i, { timeout: 20_000 });

  console.log(`Data set «${DATASET_NAME}» created with 12 rows.`);
}

/** Defines the endpoint, or finds the one an earlier run defined, and returns its address. */
async function endpoint(page: Page, projectId: string): Promise<string> {
  await page.goto(`${BASE}/projects/${projectId}/endpoints`, { waitUntil: 'networkidle' });

  const existing = page.getByRole('link', { name: ENDPOINT_NAME });

  if (await existing.count() > 0) {
    const href = await existing.first().getAttribute('href');
    if (href) {
      console.log(`Endpoint «${ENDPOINT_NAME}» is already here.`);
      return href;
    }
  }

  await page.goto(`${BASE}/projects/${projectId}/endpoints/new`, { waitUntil: 'networkidle' });

  await page.fill('#Name', ENDPOINT_NAME);
  await page.fill('#Url', '{{environment.baseUrl}}/records/{{dataset.current.id}}');

  await page.locator('#EnvironmentId').selectOption({ index: 1 });

  // Found by its text and selected by its value. The option reads «record ids — 12 rows», so an
  // exact label would have to know the row count, and an index would silently pick whatever
  // happened to sort first after the next seed change.
  const set = await page.locator('#DataSetId option', { hasText: DATASET_NAME })
    .first().getAttribute('value');

  if (!set) throw new Error(`The endpoint form does not offer «${DATASET_NAME}».`);
  await page.locator('#DataSetId').selectOption(set);

  // Scoped to the form. A bare button[type="submit"] also matches the sign-out item inside the
  // account menu, which is hidden — so the click waits thirty seconds for it to become visible.
  await page.locator('form[action$="/endpoints/new"] button[type="submit"]').click();
  await page.waitForURL(/\/endpoints\/[0-9a-f-]+$/i, { timeout: 20_000 });

  console.log(`Endpoint «${ENDPOINT_NAME}» defined.`);
  return new URL(page.url()).pathname;
}

/** One press. Twelve real calls to the local fake API, and a row per input underneath. */
async function test(page: Page, path: string): Promise<number> {
  await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle' });
  await page.waitForSelector('.endpoint-test [data-island-mounted="true"], .endpoint-test');

  await page.getByRole('button', { name: /^test$/i }).click();
  await page.waitForSelector('.endpoint-test-result', { timeout: 60_000 });

  const summary = await page.locator('.endpoint-test-result').innerText();
  console.log(`Test finished: ${summary.replace(/\s+/g, ' ').trim()}`);

  await page.waitForSelector('.sample', { timeout: 30_000 });
  return page.locator('.sample').count();
}

/**
 * Approves everything in the queue, as somebody who did not run it.
 *
 * A second person, and not for tidiness: the product refuses to let the account that started a
 * sweep bless its own results, so doing this as the demo account silently approves nothing. The
 * first version of this script did exactly that and then printed «Approved 12 samples», which is
 * how a second test that had compared against nothing still reported success.
 */
async function approveAll(browser: Browser, path: string): Promise<void> {
  const context = await browser.newContext();
  await context.addCookies([
    { name: '.AspNetCore.Culture', value: 'c=en|uic=en', url: BASE },
  ]);

  const page = await context.newPage();

  try {
    await page.goto(`${BASE}/account/sign-in`, { waitUntil: 'networkidle' });
    await page.fill('#Email', REVIEWER);
    await page.fill('#Password', PASSWORD);
    await page.click('button[type="submit"]');
    await page.waitForLoadState('networkidle');

    if (page.url().includes('sign-in')) throw new Error(`Sign-in failed for ${REVIEWER}.`);

    await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle' });
    await page.waitForSelector('.sample', { timeout: 30_000 });

    const before = await page.locator('.sample').count();

    await page.getByRole('button', { name: /select these/i }).click();
    await page.getByRole('button', { name: /^approve$/i }).click();
    await page.waitForTimeout(1500);

    // Counted from the screen afterwards, not assumed from what was clicked. A refusal comes back
    // as a toast and leaves every row exactly where it was, and the whole point of this step is
    // that the next test has something to compare against.
    const approved = await page.locator('.sample', { has: page.locator('.badge-pass') }).count();

    if (approved === 0) {
      throw new Error(
        `Approved nothing: ${before} samples are still waiting. ` +
        'Separation of duties refuses an approval from whoever started the run.');
    }

    console.log(`Approved ${approved} of ${before} samples, as ${REVIEWER}.`);
  } finally {
    await context.close();
  }
}

/**
 * One screenshot, reusing the one sign-in.
 *
 * Signing in per shot means twelve sign-ins, which is more than the application's own rate limit
 * on that endpoint allows in a minute — so the later ones are refused and the shots are of the
 * sign-in page under the wrong filename. The same trap e2e/shoot.ts already fell into.
 */
async function shoot(
  browser: Browser, session: Awaited<ReturnType<BrowserContext['storageState']>>,
  path: string, name: string,
  language: 'fa' | 'en', theme: 'light' | 'dark',
): Promise<void> {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 1100 },
    locale: language === 'fa' ? 'fa-IR' : 'en-GB',
    colorScheme: theme,
    deviceScaleFactor: 2,
    storageState: session,
  });

  await context.addInitScript(`localStorage.setItem('proofflow-theme', '${theme}')`);
  await context.addCookies([
    { name: '.AspNetCore.Culture', value: `c=${language}|uic=${language}`, url: BASE },
    { name: 'proofflow.tz', value: 'Asia%2FTehran', url: BASE },
  ]);

  const page = await context.newPage();
  await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(1200);

  await page.screenshot({
    path: resolve(OUT, `${name}--${language}-${theme}-desktop.png`), fullPage: true });

  await context.close();
}

async function main(): Promise<void> {
  if (!PASSWORD) throw new Error('Set PROOFFLOW_PASSWORD.');
  await mkdir(OUT, { recursive: true });

  const browser = await chromium.launch();
  const page = await browser.newPage();

  try {
    await signIn(page);
    const projectId = await firstProjectId(page);

    await inputs(page, projectId);
    const path = await endpoint(page, projectId);

    // The first test, against nothing. Every row comes back «not checked», because there is no
    // approved answer for any of them yet — which is what the first test of a new set of inputs
    // honestly is, and is why the result is not green.
    const samples = await test(page, path);
    if (samples === 0) throw new Error('The test produced no samples.');

    await approveAll(browser, path);

    // And the second, which is the one that means something: the same twelve calls, now compared
    // against twelve approved answers. If this is not green the comparison is finding noise, and
    // if it is green while the first one was too then «passed» never meant anything.
    const second = await test(page, path);
    if (second === 0) throw new Error('The second test produced no samples.');

    const session = await page.context().storageState();
    await page.close();

    for (const language of ['en', 'fa'] as const) {
      for (const theme of ['light', 'dark'] as const) {
        await shoot(browser, session, path, 'endpoint-detail', language, theme);
        await shoot(browser, session, `/projects/${projectId}/endpoints`, 'endpoints', language, theme);
        await shoot(browser, session, `/projects/${projectId}/datasets`, 'datasets', language, theme);
      }
    }

    console.log(`Demo ready. Endpoint at ${BASE}${path}`);
  } finally {
    await browser.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
