import { chromium, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { projectId as pickProject } from './tools/project';

/**
 * Runs a scenario through the interface, for real.
 *
 * The same principle as the other demo scripts, and here it is the whole point of the phase: the
 * run is started by pressing the button on the canvas, the console is watched while it happens, and
 * the screenshots are of what a person would actually see. A run started by calling the controller
 * from a test proves the engine works and proves nothing about whether anybody can use it.
 *
 * It also fails loudly if the run does not reach a terminal state, because a console stuck on
 * "Running" for ever is the failure this phase is most likely to ship.
 *
 *   npx tsx e2e/demo-run.ts        (with the application running on :5290)
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

/** Opens the scenario the canvas demo drew, or makes one. */
async function openScenario(page: Page, projectId: string): Promise<string> {
  await page.goto(`${BASE}/projects/${projectId}/scenarios`, { waitUntil: 'networkidle' });

  const existing = await page.locator('td a[href*="/scenarios/"]').first()
    .getAttribute('href').catch(() => null);

  if (!existing) throw new Error('No scenario to run. Run demo-canvas.ts first.');

  await page.goto(`${BASE}${existing}`, { waitUntil: 'networkidle' });
  await page.waitForSelector('[data-island-mounted="true"]');

  return existing;
}

/**
 * Presses Run and waits for the console to reach a verdict.
 *
 * The wait is on the badge rather than on a timer: a run that takes four seconds and a run that
 * hangs look identical for the first four seconds, and only one of them is worth a screenshot.
 */
async function run(page: Page): Promise<string> {
  await page.locator('.canvas-page-bar button[type="submit"]', { hasText: /Run|اجرا/ }).first().click();
  await page.waitForURL(/\/runs\/[0-9a-f-]+$/i, { timeout: 15_000 });
  await page.waitForSelector('[data-island-mounted="true"]');

  const badge = page.locator('.run-head .badge').first();

  for (let waited = 0; waited < 60_000; waited += 500) {
    const text = (await badge.innerText()).trim();
    if (!/Queued|Running|در صف|در حال اجرا/.test(text)) return text;

    await page.waitForTimeout(500);
  }

  throw new Error('The run never finished. It is stuck on Queued or Running.');
}

async function report(page: Page): Promise<void> {
  // The badge changes on the live message; the rest arrives on the read that follows it. Counting
  // straight after the badge measures the gap rather than the console.
  await page.waitForTimeout(1500);

  const figures = await page.locator('.run-figures > div').allInnerTexts();
  const lines = await page.locator('.run-log-line').count();
  const steps = await page.locator('.run-timeline-row').count();

  console.log(`Figures: ${figures.map((f) => f.replace(/\n/g, ' ')).join(' · ')}`);
  console.log(`Log shows ${lines} lines in the window.`);

  // The timeline is behind a toggle, so it is counted after switching to it.
  await page.locator('.run-tabs button').nth(1).click();
  await page.waitForTimeout(400);
  console.log(`Timeline shows ${await page.locator('.run-timeline-row').count() || steps} steps.`);

  const outcome = await page.locator('.run-outcome').first().innerText().catch(() => '—');
  console.log(`Outcome: ${outcome.trim()}`);
}

async function shoot(
  browser: Browser, session: Awaited<ReturnType<BrowserContext['storageState']>>,
  path: string, name: string, language: 'fa' | 'en', theme: 'light' | 'dark', tab: number,
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
  await page.waitForTimeout(1400);

  if (tab > 0) {
    await page.locator('.run-tabs button').nth(tab).click();
    await page.waitForTimeout(600);
  }

  await page.screenshot({ path: resolve(OUT, `${name}--${language}-${theme}-desktop.png`) });
  await context.close();
}

async function main(): Promise<void> {
  if (!PASSWORD) throw new Error('Set PROOFFLOW_PASSWORD.');
  await mkdir(OUT, { recursive: true });

  const browser = await chromium.launch();

  // Pinned to English: this script finds the Run button and the status badge by their words.
  const context = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
  await context.addCookies([{ name: '.AspNetCore.Culture', value: 'c=en|uic=en', url: BASE }]);

  const page = await context.newPage();

  try {
    await signIn(page);
    const projectId = await firstProjectId(page);
    await openScenario(page, projectId);

    const verdict = await run(page);
    console.log(`The run finished: ${verdict}`);

    await report(page);

    const session = await page.context().storageState();
    const consolePath = new URL(page.url()).pathname;
    await page.close();

    for (const language of ['en', 'fa'] as const) {
      for (const theme of ['light', 'dark'] as const) {
        await shoot(browser, session, consolePath, 'run-console', language, theme, 0);
        await shoot(browser, session, consolePath, 'run-timeline', language, theme, 1);
        await shoot(browser, session, `/projects/${projectId}/runs`, 'run-history', language, theme, 0);
      }
    }

    console.log('Screenshots written to docs/ui/raw.');
  } finally {
    await browser.close();
  }
}

await main();
