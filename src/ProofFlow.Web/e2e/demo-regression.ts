import { chromium, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { projectId as pickProject } from './tools/project';

/**
 * Walks the nine-step wizard, for real.
 *
 * Same principle as e2e/demo.ts and the same reason: the seeder fabricates nothing, so the demo
 * data for sample-based regression is made the way a person makes it. Every row of the data set is
 * pasted, every sample in the queue came from an actual call to the local fake API, and the
 * approvals are pressed.
 *
 * It also is the acceptance test for this phase. If the wizard cannot be walked from step one to
 * step nine without reaching into the database, it is not finished.
 *
 *   npx tsx e2e/demo-regression.ts        (with the application running on :5290)
 */

const BASE = process.env.PROOFFLOW_URL ?? 'http://localhost:5290';
const EMAIL = process.env.PROOFFLOW_EMAIL ?? 'demo@proofflow.local';
const PASSWORD = process.env.PROOFFLOW_PASSWORD ?? '';

const OUT = resolve(import.meta.dirname, '../../../docs/ui/raw');

const BASELINE_NAME = 'record detail';

/** Twelve rows: enough to fill a queue, few enough to sweep in a second. */
const IDS = Array.from({ length: 12 }, (_, index) => String(index + 1)).join('\n');

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

const next = (page: Page) => page.locator('.wizard-foot .btn-primary').click();

/** Steps one to six, in order, pressing what a person presses. */
async function walkTheWizard(page: Page, projectId: string): Promise<string> {
  // The wizard resumes where it was left, which is the point of it and is wrong for a demo run
  // that has to start at step one every time.
  await page.goto(`${BASE}/projects/${projectId}/wizard`, { waitUntil: 'networkidle' });
  await page.evaluate((id) => localStorage.removeItem(`proofflow-wizard-${id}`), projectId);
  await page.reload({ waitUntil: 'networkidle' });
  await page.waitForSelector('[data-island-mounted="true"]');

  // 1 — the endpoint, with a reference to the current row.
  await page.locator('.wizard-panel input.input-mono').fill(
    '{{environment.baseUrl}}/records/{{dataset.current.value}}');
  await next(page);

  // 2 — where to send it. The environment created by e2e/demo.ts is already selected.
  await next(page);

  // 3 — try it once. Nothing to press here; the link goes to the request lab.
  await next(page);

  // 4 — the baseline. Chosen if a previous run already defined it, and defined if not.
  const existing = page.locator('.wizard-panel select option', { hasText: BASELINE_NAME });

  if (await existing.count() > 0) {
    await page.locator('.wizard-panel select').selectOption({ label: BASELINE_NAME });
    await next(page);
  } else {
    await page.locator('.wizard-panel input.input').first().fill(BASELINE_NAME);
    await page.getByRole('button', { name: /define the baseline|تعریف/i }).click();
    await page.waitForTimeout(700);
  }

  // 5 — the inputs. A set from an earlier run is picked rather than duplicated.
  await page.waitForSelector('.wizard-panel');
  const sets = page.locator('.wizard-panel select option');

  if (await sets.count() > 1) {
    await page.locator('.wizard-panel select').selectOption({ index: 1 });
    await next(page);
  } else {
    await page.locator('.wizard-panel textarea').fill(IDS);
    await page.getByRole('button', { name: /read this|بخوانش/i }).click();
    await page.waitForSelector('.wizard-panel .badge-accent');
    await page.getByRole('button', { name: /create a data set|ساخت مجموعه‌داده/i }).click();
    await page.waitForTimeout(1000);
  }

  // 6 — the sweep. Real calls, twelve of them.
  await page.getByRole('button', { name: /run the sweep|اجرای/i }).click();
  await page.waitForSelector('.wizard-panel .badge-idle', { timeout: 30_000 });

  const summary = await page.locator('.wizard-panel .row').first().innerText();
  console.log(`Sweep finished: ${summary.replace(/\s+/g, ' ').trim()}`);

  const queue = await page.getByRole('link', { name: /open the review queue|بازکردن/i })
    .getAttribute('href');

  if (!queue) throw new Error('The sweep produced no session.');
  return queue;
}

/** Approves everything in the queue, which is what makes the second sweep meaningful. */
async function approveAll(page: Page, queue: string): Promise<void> {
  await page.goto(`${BASE}${queue}`, { waitUntil: 'networkidle' });
  await page.waitForSelector('[data-island-mounted="true"]');
  await page.waitForSelector('.sample');

  const before = await page.locator('.sample').count();

  await page.getByRole('button', { name: /select these|انتخاب همین/i }).click();
  await page.getByRole('button', { name: /^(approve|تأیید)$/i }).click();
  await page.waitForTimeout(1200);

  console.log(`Approved ${before} samples.`);
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
  await page.waitForTimeout(1000);

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

    const queue = await walkTheWizard(page, projectId);
    await approveAll(page, queue);

    const session = await page.context().storageState();
    await page.close();

    for (const language of ['en', 'fa'] as const) {
      for (const theme of ['light', 'dark'] as const) {
        await shoot(browser, session, queue, 'review-queue', language, theme);
        await shoot(browser, session, `/projects/${projectId}/wizard`, 'wizard', language, theme);
        await shoot(browser, session, `/projects/${projectId}/datasets`, 'datasets', language, theme);
      }
    }

    console.log(`Demo ready. Queue at ${BASE}${queue}`);
  } finally {
    await browser.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
