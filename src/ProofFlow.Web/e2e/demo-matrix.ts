import { chromium, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { projectId as pickProject } from './tools/project';

/**
 * Runs a scenario across every environment, through the interface.
 *
 * The same principle as the other demo scripts: the boxes are ticked, the button is pressed, and
 * the grid is watched until every cell lands. A matrix assembled by calling the service would prove
 * the service works and nothing about whether anybody can read the answer.
 *
 * It refuses to report if the grid never settles, and it opens a comparison — because a matrix
 * whose cells fill in but whose compare button does nothing is the failure this phase would most
 * plausibly ship.
 *
 *   npx tsx e2e/demo-matrix.ts        (with the application running on :5290)
 */

const BASE = process.env.PROOFFLOW_URL ?? 'http://localhost:5290';
const EMAIL = process.env.PROOFFLOW_EMAIL ?? 'demo@proofflow.local';
const PASSWORD = process.env.PROOFFLOW_PASSWORD ?? '';

const OUT = resolve(import.meta.dirname, '../../../docs/ui/raw');

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

/** Ticks every scenario and every environment, then starts the batch. */
async function start(page: Page, projectId: string): Promise<void> {
  await page.goto(`${BASE}/projects/${projectId}/matrix`, { waitUntil: 'networkidle' });

  const scenarios = page.locator('input[name="scenarioIds"]');
  const environments = page.locator('input[name="environmentIds"]');

  const scenarioCount = await scenarios.count();
  const environmentCount = await environments.count();

  if (scenarioCount === 0) throw new Error('No scenario to run. Run demo-canvas.ts first.');
  if (environmentCount < 2) throw new Error('A matrix needs at least two environments.');

  for (let index = 0; index < scenarioCount; index++) await scenarios.nth(index).check();
  for (let index = 0; index < environmentCount; index++) await environments.nth(index).check();

  await page.fill('#matrix-name', 'Before the release');

  // Scoped to the matrix form. `form button[type=submit]` also matches the sign-out button inside
  // the account menu, which is in the document and hidden.
  await page.locator('form[action$="/matrix/start"] button[type="submit"]').click();

  await page.waitForURL(/\/matrix\/[0-9a-f-]+$/i, { timeout: 15_000 });
  await page.waitForSelector('[data-island-mounted="true"]');

  console.log(`Started ${scenarioCount} × ${environmentCount} = ${scenarioCount * environmentCount} runs.`);
}

/**
 * Waits for every cell to land.
 *
 * On the badge rather than a timer: a grid that is still filling and a grid that is stuck look
 * identical for the first few seconds, and only one of them is worth a screenshot.
 */
async function settle(page: Page): Promise<string> {
  const badge = page.locator('.matrix-head .badge').first();

  for (let waited = 0; waited < 120_000; waited += 500) {
    const text = (await badge.innerText().catch(() => '')).trim();
    if (text && !/Queued|Running|در صف|در حال اجرا/.test(text)) return text;

    await page.waitForTimeout(500);
  }

  throw new Error('The matrix never settled. Cells are stuck on Queued or Running.');
}

async function report(page: Page): Promise<void> {
  const columns = await page.locator('.matrix-table thead th').allInnerTexts();
  const rows = await page.locator('.matrix-table tbody tr').count();
  const cells = await page.locator('.matrix-cell').allInnerTexts();

  console.log(`Columns: ${columns.map((c) => c.trim()).filter(Boolean).join(' · ')}`);
  console.log(`${rows} row(s), ${cells.length} cell(s).`);
  console.log(`Cells: ${cells.map((c) => c.replace(/\s+/g, ' ').trim()).join(' | ')}`);
}

/** Opens a comparison between the first two columns and says what it found. */
async function compare(page: Page): Promise<void> {
  const button = page.locator('.matrix-table tbody button').first();
  if (await button.count() === 0) {
    console.log('No comparison offered — fewer than two environments.');
    return;
  }

  await button.click();
  await page.waitForTimeout(2500);

  const steps = await page.locator('.matrix-step').count();
  const notes = await page.locator('.matrix-note').allInnerTexts();

  console.log(`Comparison shows ${steps} step(s).`);
  if (notes.length) console.log(`Notes: ${notes.map((n) => n.replace(/\s+/g, ' ').trim()).join(' | ')}`);

  const verdicts = await page.locator('.matrix-step-head .badge').allInnerTexts();
  if (verdicts.length) console.log(`Per step: ${verdicts.map((v) => v.trim()).join(', ')}`);
}

async function shoot(
  browser: Browser, session: Awaited<ReturnType<BrowserContext['storageState']>>,
  path: string, name: string, language: 'fa' | 'en', theme: 'light' | 'dark', withCompare: boolean,
): Promise<void> {
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
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
  await page.waitForTimeout(1600);

  if (withCompare) {
    await page.locator('.matrix-table tbody button').first().click().catch(() => {});
    await page.waitForTimeout(2500);
  }

  await page.screenshot({ path: resolve(OUT, `${name}--${language}-${theme}-desktop.png`) });
  await context.close();
}

async function main(): Promise<void> {
  if (!PASSWORD) throw new Error('Set PROOFFLOW_PASSWORD.');
  await mkdir(OUT, { recursive: true });

  const browser = await chromium.launch();

  // Pinned to English: this script reads the settled badge by its words.
  const context = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
  await context.addCookies([{ name: '.AspNetCore.Culture', value: 'c=en|uic=en', url: BASE }]);

  const page = await context.newPage();

  try {
    await signIn(page);
    const projectId = await firstProjectId(page);

    await start(page, projectId);
    console.log(`The matrix settled: ${await settle(page)}`);

    await report(page);
    await compare(page);

    const session = await page.context().storageState();
    const gridPath = new URL(page.url()).pathname;
    await page.close();

    for (const language of ['en', 'fa'] as const) {
      for (const theme of ['light', 'dark'] as const) {
        await shoot(browser, session, gridPath, 'matrix-grid', language, theme, false);
        await shoot(browser, session, gridPath, 'matrix-compare', language, theme, true);
      }
    }

    console.log('Screenshots written to docs/ui/raw.');
  } finally {
    await browser.close();
  }
}

await main();
