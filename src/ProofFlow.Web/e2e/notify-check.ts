import { chromium } from '@playwright/test';
import { projectId as pickProject } from './tools/project';

/**
 * When something fails, somebody finds out — walked in a real browser.
 *
 * Configures the project's webhook to point at the fake API's own recording receiver, proves the
 * test button round-trips, breaks something for real by re-running a failed run, and then waits for
 * the two outlets that need no mail relay: the bell fills, and the webhook receiver holds a signed
 * payload. Nothing here is mocked — the delivery worker sweeps on its own clock, which is why the
 * end of this script is a patient loop rather than an assertion.
 */

const BASE = process.env.PROOFFLOW_URL ?? 'http://localhost:5290';
const PASSWORD = process.env.PROOFFLOW_PASSWORD;

if (!PASSWORD) {
  console.error('Set PROOFFLOW_PASSWORD.');
  process.exit(1);
}

const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
const page = await context.newPage();

await page.goto(`${BASE}/account/sign-in`, { waitUntil: 'networkidle' });
await page.fill('#Email', 'demo@proofflow.local');
await page.fill('#Password', PASSWORD);
await page.click('button[type="submit"]');
await page.waitForLoadState('networkidle');

const projectId = await pickProject(page, BASE);

// 1 — point the webhook at the receiver this application serves, and prove it round-trips.
await page.goto(`${BASE}/projects/${projectId}/settings`, { waitUntil: 'networkidle' });
await page.fill('#WebhookUrl', `${BASE}/fake/hook`);
await page.locator('input[name="WebhookAllowPrivate"]').check();
await page.locator('form[action$="/settings"] button[type="submit"]').last().click();
await page.waitForLoadState('networkidle');

await page.locator('form[action$="webhook-secret"] button').click();
await page.waitForLoadState('networkidle');

const secretShown = await page.locator('.key-issued code').last().innerText();
if (!/^[A-Za-z0-9_-]{40,50}$/.test(secretShown.trim())) {
  throw new Error(`The signing secret was not shown once: ${secretShown}`);
}

await page.locator('form[action$="webhook-test"] button').click();
await page.waitForLoadState('networkidle');
await page.waitForTimeout(600);

const toasts = (await page.locator('[role="status"], .toast').allInnerTexts()).join(' | ');
if (!/200/.test(toasts)) throw new Error(`The test delivery did not report success: ${toasts}`);
console.log('1  The webhook test round-tripped and said so.');

// 2 — break something for real: run a scenario again on an environment where it fails.
await page.goto(`${BASE}/projects/${projectId}/runs`, { waitUntil: 'networkidle' });
await page.locator('form[action*="/again"] button').first().click();
await page.waitForLoadState('networkidle');
console.log('2  A failing run is on its way at', page.url());

// 3 — the bell fills on the worker's clock, not ours. Poll with reloads, up to a minute.
let sentence = '';
for (let attempt = 0; attempt < 20; attempt++) {
  await page.waitForTimeout(3000);
  await page.goto(`${BASE}/projects/${projectId}/runs`, { waitUntil: 'networkidle' });

  if (await page.locator('.bell-dot').count()) {
    await page.locator('.bell-trigger').click();
    sentence = (await page.locator('.bell-item-text').first().innerText()).trim();
    break;
  }
}

if (!sentence) throw new Error('The bell never filled.');
console.log('3  The bell says:', sentence);

// 4 — and the receiver holds a signed payload, readable by anyone who asks it.
let delivered: { body: string; signature: string } | null = null;
for (let attempt = 0; attempt < 20 && !delivered; attempt++) {
  const response = await page.request.get(`${BASE}/fake/hook/last`);
  if (response.ok()) {
    const last = await response.json();
    if (/run\.(failed|errored)/.test(last.body)) delivered = last;
  }
  if (!delivered) await page.waitForTimeout(3000);
}

if (!delivered) throw new Error('The webhook receiver never saw the failure.');
if (!delivered.signature.startsWith('sha256=')) {
  throw new Error(`The delivery arrived unsigned: ${delivered.signature}`);
}
console.log('4  The receiver holds it, signed:', delivered.signature.slice(0, 24) + '…');

// 5 — seen means seen.
await page.locator('.bell-menu form button').click();
await page.waitForLoadState('networkidle');

if (await page.locator('.bell-dot').count()) throw new Error('Marking seen did not clear the dot.');
console.log('5  Marked seen; the dot is gone until the next failure.');

await browser.close();
console.log('\nA failure now tells the team, three ways, without anybody watching a page.');
