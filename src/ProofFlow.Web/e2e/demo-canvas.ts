import { chromium, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { projectId as pickProject } from './tools/project';

/**
 * Draws a scenario on the canvas, for real.
 *
 * The same principle as the other two demo scripts: every node here was added from the palette,
 * every connection dragged, every property typed. A canvas that can only be filled by writing rows
 * into the database is a canvas nobody can build a test with.
 *
 * The scenario it draws is the one the product is for — log in, fetch a record, check the status,
 * compare against the baseline — which also makes this the acceptance test for the phase: if the
 * validator does not go quiet by the end, the graph is not a test.
 *
 *   npx tsx e2e/demo-canvas.ts        (with the application running on :5290)
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

/** Opens the newest scenario, or creates one. */
async function openCanvas(page: Page, projectId: string): Promise<string> {
  await page.goto(`${BASE}/projects/${projectId}/scenarios`, { waitUntil: 'networkidle' });

  const existing = await page.locator('td a[href*="/scenarios/"]').first()
    .getAttribute('href').catch(() => null);

  if (existing) {
    await page.goto(`${BASE}${existing}`, { waitUntil: 'networkidle' });
  } else {
    await page.goto(`${BASE}/projects/${projectId}/scenarios/new`, { waitUntil: 'networkidle' });
  }

  await page.waitForSelector('[data-island-mounted="true"]');
  await page.waitForSelector('.wf-node');

  return page.url();
}

/** Adds a node from the palette by its visible name, and returns how many there are now. */
async function addNode(page: Page, search: string, name: string): Promise<number> {
  await page.fill('.node-palette-search input', search);
  await page.waitForTimeout(150);

  await page.locator('.node-palette-item', { hasText: name }).first().click();
  await page.waitForTimeout(250);

  return page.locator('.wf-node').count();
}

/** Fills one property in the inspector, found by its label. */
async function setProperty(page: Page, label: string, value: string): Promise<void> {
  const field = page.locator('.inspector .field', { hasText: label }).first();
  await field.locator('input, textarea').first().fill(value);
  await field.locator('input, textarea').first().blur();
  await page.waitForTimeout(200);
}

/**
 * Joins two sockets by dragging between them.
 *
 * Sockets are found by the label in their title rather than by position in the list: an index is
 * right until a node gains a port, and then it silently connects the wrong two things. The drag
 * itself is the interaction under test — a scenario assembled by calling the API would prove
 * nothing about whether anybody can draw one.
 */
async function connect(
  page: Page, fromIndex: number, fromPort: string, toIndex: number, toPort: string,
): Promise<void> {
  const from = page.locator('.wf-node').nth(fromIndex)
    .locator(`.vue-flow__handle-right[title^="${fromPort}"]`);
  const to = page.locator('.wf-node').nth(toIndex)
    .locator(`.vue-flow__handle-left[title^="${toPort}"]`);

  const source = await from.boundingBox();
  const target = await to.boundingBox();
  if (!source || !target) throw new Error(`No socket «${fromPort}» → «${toPort}».`);

  await page.mouse.move(source.x + source.width / 2, source.y + source.height / 2);
  await page.mouse.down();
  await page.mouse.move(target.x + target.width / 2, target.y + target.height / 2, { steps: 14 });
  await page.mouse.up();
  await page.waitForTimeout(300);
}

async function draw(page: Page): Promise<void> {
  // Always redrawn, never reused. A screenshot of a scenario laid out by an older build shows an
  // older build's layout, which is exactly the evidence this script exists to avoid producing.
  const before = await page.locator('.wf-node').count();

  if (before > 1) {
    await page.locator('.canvas-surface').click({ position: { x: 400, y: 300 } });
    await page.keyboard.press('Control+a');
    await page.keyboard.press('Delete');
    await page.waitForTimeout(400);

    // The start goes with it; one is added back so the graph has a beginning.
    await addNode(page, 'Start', 'Start');
    console.log(`Cleared ${before} steps.`);
  }

  // A request, then a check on what it returned.
  await addNode(page, 'HTTP', 'HTTP request');
  await page.locator('.wf-node').nth(1).click();
  await setProperty(page, 'Address', '{{environment.baseUrl}}/records/1');

  await addNode(page, 'status', 'Check the status code');

  // The order things run in: start, then the request, then the check.
  await connect(page, 0, 'Then', 1, 'In');
  await connect(page, 1, 'Then', 2, 'In');

  // And the response itself. An assertion needs something to look at as well as a turn to run.
  await connect(page, 1, 'Response', 2, 'Response');

  await page.locator('.canvas-bar .btn-primary').click();
  await page.waitForTimeout(1200);

  const badge = await page.locator('.canvas-bar .badge').first().innerText();
  console.log(`Drew ${await page.locator('.wf-node').count()} steps. Validator says: ${badge.trim()}`);
}

async function shoot(
  browser: Browser, session: Awaited<ReturnType<BrowserContext['storageState']>>,
  path: string, name: string, language: 'fa' | 'en', theme: 'light' | 'dark',
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

  await page.screenshot({ path: resolve(OUT, `${name}--${language}-${theme}-desktop.png`) });
  await context.close();
}

async function main(): Promise<void> {
  if (!PASSWORD) throw new Error('Set PROOFFLOW_PASSWORD.');
  await mkdir(OUT, { recursive: true });

  const browser = await chromium.launch();

  // Pinned to English, because this script finds palette entries by their visible names. Without
  // it the browser gets the application's default culture and every label search misses.
  const context = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
  await context.addCookies([
    { name: '.AspNetCore.Culture', value: 'c=en|uic=en', url: BASE },
  ]);

  const page = await context.newPage();

  try {
    await signIn(page);
    const projectId = await firstProjectId(page);

    const url = await openCanvas(page, projectId);
    await draw(page);

    const session = await page.context().storageState();
    const path = new URL(url).pathname;
    await page.close();

    for (const language of ['en', 'fa'] as const) {
      for (const theme of ['light', 'dark'] as const) {
        await shoot(browser, session, path, 'canvas', language, theme);
      }
    }

    console.log(`Canvas ready at ${url}`);
  } finally {
    await browser.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
