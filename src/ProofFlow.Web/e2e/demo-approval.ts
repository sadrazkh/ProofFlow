import { chromium, type Page } from '@playwright/test';

/**
 * Leaves the approval inbox with both halves of the rule in it.
 *
 * The point of this phase is a rule that needs two people, so demonstrating it needs two people.
 * The designer proposes a new version of one baseline and the owner proposes one of another. Read
 * as the owner, the inbox then shows both cases on one screen: something they may approve, and
 * something they may not, because they recorded it themselves.
 *
 * Nothing is fabricated. Both baselines are real responses from the local fake API, captured and
 * compared through the interface the way a person does it — /volatile on purpose, because three of
 * its five fields change on every call and a comparison against it therefore always has something
 * to accept.
 *
 *   npx tsx e2e/demo-approval.ts    (with the application running on :5290)
 */

const BASE = process.env.PROOFFLOW_URL ?? 'http://localhost:5290';
const PASSWORD = process.env.PROOFFLOW_PASSWORD ?? '';

const OWNER = process.env.PROOFFLOW_EMAIL ?? 'demo@proofflow.local';
const DESIGNER = 'designer@proofflow.local';

async function signIn(page: Page, email: string): Promise<void> {
  await page.goto(`${BASE}/account/sign-in`, { waitUntil: 'networkidle' });
  await page.fill('#Email', email);
  await page.fill('#Password', PASSWORD);
  await page.click('button[type="submit"]');
  await page.waitForLoadState('networkidle');

  if (page.url().includes('sign-in')) throw new Error(`Sign-in failed for ${email}.`);
}

async function firstProjectId(page: Page): Promise<string> {
  await page.goto(`${BASE}/projects`, { waitUntil: 'networkidle' });
  const href = await page.locator('a.project-card').first().getAttribute('href');
  const id = href?.split('/').pop();

  if (!id) throw new Error('No project found. Is Demo:Seed on?');
  return id;
}

/** Sends a request and records the response, through the request lab. */
async function record(page: Page, projectId: string, name: string): Promise<string> {
  await page.goto(`${BASE}/projects/${projectId}/baselines`, { waitUntil: 'networkidle' });

  const existing = await page.getByRole('link', { name, exact: true }).first()
    .getAttribute('href').catch(() => null);

  if (existing) {
    console.log(`"${name}" is already recorded.`);
    return existing;
  }

  await page.goto(`${BASE}/projects/${projectId}/request`, { waitUntil: 'networkidle' });
  await page.waitForSelector('[data-island-mounted="true"]');

  await page.locator('input.input-mono').first().fill('{{environment.baseUrl}}/volatile');
  await page.getByRole('button', { name: /send|ارسال/i }).click();
  await page.waitForSelector('.response-head', { timeout: 20_000 });

  await page.getByRole('button', { name: /save as baseline|ذخیره به‌عنوان/i }).click();
  await page.waitForSelector('[role="dialog"]');
  await page.locator('[role="dialog"] input').first().fill(name);
  await page.locator('[role="dialog"] .btn-primary').click();

  await page.waitForURL(/\/baselines\/[0-9a-f-]+$/i, { timeout: 20_000 });
  console.log(`"${name}" recorded.`);

  return new URL(page.url()).pathname;
}

/**
 * Compares once and folds the differences into a proposed version.
 *
 * This is what puts a row in the inbox: the first version of a baseline is approved as it is
 * recorded — recording it is the act of saying it is correct — so only a second version is
 * something anybody has to decide about.
 */
async function propose(page: Page, path: string, name: string): Promise<void> {
  await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle' });
  await page.waitForSelector('[data-island-mounted="true"]');

  if (await page.locator('.badge-warn').count() > 0) {
    console.log(`"${name}" already has a version waiting.`);
    return;
  }

  await page.locator('.workbench-bar .btn-primary').first().click();
  await page.waitForSelector('.diff-summary', { timeout: 20_000 });

  const acceptAll = page.locator('.diff-foot .btn-ghost').first();

  if (await acceptAll.count() === 0) {
    console.log(`"${name}" compared identical, so there is nothing to propose.`);
    return;
  }

  await acceptAll.click();
  await page.locator('.diff-foot .btn-primary').click();
  await page.waitForTimeout(1500);

  console.log(`"${name}": a new version is waiting on somebody.`);
}

async function main(): Promise<void> {
  if (!PASSWORD) throw new Error('Set PROOFFLOW_PASSWORD.');

  const browser = await chromium.launch();

  // The designer's proposal, which the owner may approve.
  const designerContext = await browser.newContext();
  const designer = await designerContext.newPage();

  await signIn(designer, DESIGNER);
  const projectId = await firstProjectId(designer);
  await propose(designer, await record(designer, projectId, 'orders'), 'orders');
  await designerContext.close();

  // The owner's own, which they may not — that is the whole rule.
  const ownerContext = await browser.newContext();
  const owner = await ownerContext.newPage();

  await signIn(owner, OWNER);
  await propose(owner, await record(owner, projectId, 'checkout'), 'checkout');
  await ownerContext.close();

  await browser.close();

  console.log(`Inbox ready at ${BASE}/projects/${projectId}/approvals`);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
