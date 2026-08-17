import { chromium } from '@playwright/test';
import { projectId as pickProject } from './tools/project';

/**
 * The four steps, walked the way somebody walks them, against an API that needs a token.
 *
 * The complaint this answers was «our APIs have auth and it doesn't work». So this types nothing
 * that looks like a token anywhere — a username, a password and two paths — and finishes on an
 * endpoint whose Test button comes back green. If a token had to be pasted in to get there, this
 * script could not have been written.
 *
 *   npx tsx e2e/connect-check.ts       (with the application running on :5290)
 */

const BASE = process.env.PROOFFLOW_URL ?? 'http://localhost:5290';
const EMAIL = process.env.PROOFFLOW_EMAIL ?? 'demo@proofflow.local';
const PASSWORD = process.env.PROOFFLOW_PASSWORD ?? '';

if (!PASSWORD) throw new Error('Set PROOFFLOW_PASSWORD.');

const browser = await chromium.launch();
const context = await browser.newContext();

await context.addCookies([{ name: '.AspNetCore.Culture', value: 'c=en|uic=en', url: BASE }]);

const page = await context.newPage();

try {
  await page.goto(`${BASE}/account/sign-in`, { waitUntil: 'networkidle' });
  await page.fill('#Email', EMAIL);
  await page.fill('#Password', PASSWORD);
  await page.click('button[type="submit"]');
  await page.waitForLoadState('networkidle');

  const projectId = await pickProject(page, BASE);
  if (!projectId) throw new Error('No project found.');

  await page.goto(`${BASE}/projects/${projectId}/connect`, { waitUntil: 'networkidle' });
  await page.waitForSelector('[data-island="connect-api"][data-island-mounted]');

  // ---- 1: where is it ---------------------------------------------------------------------------

  await page.fill('#c-base', `${BASE}/fake`);

  // Loopback, which the guard refuses until somebody says otherwise. The checkbox only appears
  // once the address looks private, which is the whole point of asking here rather than later.
  const allow = page.locator('.check-row input[type="checkbox"]');
  await allow.waitFor({ state: 'visible', timeout: 5_000 });
  await allow.check();

  await next();

  // ---- 2: how do you sign in --------------------------------------------------------------------

  // Sign-in is the default, so this asserts the default rather than choosing it.
  const chosen = await page.locator('.connect-kind.is-chosen .check-row-title').innerText();
  if (!/sign in/i.test(chosen)) throw new Error(`Sign-in should be the default; it is «${chosen}».`);

  await page.fill('#c-token-url', '/auth/login');
  await page.fill('#c-user', 'demo');
  await page.fill('#c-pass', 'demo-password');

  await next();

  // ---- 3: prove it ------------------------------------------------------------------------------

  await page.fill('#c-path', '/categories');
  await page.locator('.card-body button.btn-primary').click();

  await page.waitForSelector('.connect-line', { timeout: 30_000 });

  const lines = page.locator('.connect-line');
  const count = await lines.count();

  if (count !== 2) throw new Error(`Both halves should be reported; found ${count} line(s).`);

  for (let at = 0; at < count; at++) {
    const line = lines.nth(at);
    const text = (await line.innerText()).replace(/\s+/g, ' ').trim();

    if (!(await line.evaluate((node) => node.classList.contains('is-ok')))) {
      throw new Error(`A step is red: ${text}`);
    }

    console.log(`  ${text}`);
  }

  await next();

  // ---- 4: keep it -------------------------------------------------------------------------------

  const name = `Connected ${Date.now()}`;
  await page.fill('#c-name', name);

  await page.locator('.card-footer button.btn-primary').click();

  // It ends on the endpoint it made, not on a list. That is the difference between «saved» and
  // «you can now find out whether it works».
  await page.waitForURL(/\/endpoints\/[0-9a-f-]{36}/i, { timeout: 30_000 });

  const endpointUrl = page.url();
  console.log(`\nIt ended on ${endpointUrl.replace(BASE, '')}`);

  // ---- and the thing that matters ---------------------------------------------------------------

  const request = (await page.locator('main').innerText()).replace(/\s+/g, ' ');

  if (/authorization/i.test(request)) {
    throw new Error('The endpoint it made carries an Authorization header. It should carry none.');
  }

  // The workbench, not the sweep below it: an endpoint with no inputs is sent once, and «send it
  // once» is what its button does. The sweep is correctly disabled until there are rows to sweep.
  const workbench = page.locator('[data-island="baseline-workbench"]');
  await workbench.locator('button.btn-primary').first().click();

  const status = workbench.locator('.status, .diff-status, .workbench-panel').first();
  await status.waitFor({ timeout: 60_000 });

  // The response arrives through the same executor a scheduled run uses, so this is the real
  // question: did the environment's own sign-in get us past the door.
  const outcome = await page
    .waitForFunction(
      () => {
        const text = document.querySelector('main')?.innerText ?? '';
        return /\b(200|401|403)\b/.test(text) ? text.replace(/\s+/g, ' ') : null;
      },
      undefined,
      { timeout: 60_000 })
    .then((handle) => handle.jsonValue() as Promise<string>);

  if (/\b40[13]\b/.test(outcome)) {
    throw new Error(`Sending it came back refused: ${outcome.slice(0, 300)}`);
  }

  console.log('Sending it reached a path that refuses anybody without a token, and got 200.');
  console.log('\nNobody typed a token. The environment signed itself in.');
} finally {
  await browser.close();
}

/** The step button in the footer, and the wait for the panel behind it to change. */
async function next(): Promise<void> {
  const before = await page.locator('.wizard-rail .is-current .wizard-name').innerText();

  await page.locator('.card-footer button.btn-primary').click();

  await page
    .locator('.wizard-rail .is-current .wizard-name')
    .filter({ hasNotText: before })
    .waitFor({ timeout: 5_000 });
}
