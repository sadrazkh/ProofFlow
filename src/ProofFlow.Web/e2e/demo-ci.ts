import { chromium, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

/**
 * The pipeline's path, walked the way a pipeline walks it.
 *
 * A key is issued through the interface, then everything after that is plain HTTP with a header —
 * no cookie, no browser, no session. That is the whole claim of step nineteen, and a test that
 * called the controller from inside the process would prove none of it.
 *
 * It also asserts that the JUnit document is the shape a build system reads, because "we emit
 * JUnit" is only true if a reader agrees.
 *
 *   npx tsx e2e/demo-ci.ts        (with the application running on :5290)
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
  await page.goto(`${BASE}/projects`, { waitUntil: 'networkidle' });
  const href = await page.locator('a.project-card').first().getAttribute('href');
  const id = href?.split('/').pop();

  if (!id) throw new Error('No project found. Is Demo:Seed on?');
  return id;
}

/** Issues a key through the interface and reads it off the page — the one time it is shown. */
async function issueKey(page: Page, projectId: string): Promise<string> {
  await page.goto(`${BASE}/projects/${projectId}/settings`, { waitUntil: 'networkidle' });

  await page.fill('#key-name', 'Demo pipeline');
  await page.locator('form[action$="/settings/keys"] button[type="submit"]').click();
  await page.waitForLoadState('networkidle');

  const secret = (await page.locator('.key-value code').first().innerText()).trim();
  if (!secret.startsWith('pf_')) throw new Error(`That does not look like a key: ${secret}`);

  console.log(`Issued a key: ${secret.slice(0, 11)}…`);
  return secret;
}

/** Everything from here is what a build agent does: HTTP and a header, nothing else. */
async function pipeline(projectId: string, key: string): Promise<void> {
  const headers = { Authorization: `Bearer ${key}`, 'Content-Type': 'application/json' };

  const refused = await fetch(`${BASE}/api/v1/projects/${projectId}/runs`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: '{}',
  });

  if (refused.status !== 401) {
    throw new Error(`Without a key the API answered ${refused.status}, not 401.`);
  }

  console.log('Without a key: 401, as it should be.');

  const started = await fetch(`${BASE}/api/v1/projects/${projectId}/runs`, {
    method: 'POST',
    headers,
    body: JSON.stringify({ environments: ['Local'], name: 'from the pipeline' }),
  });

  if (started.status !== 202) {
    throw new Error(`Starting answered ${started.status}: ${await started.text()}`);
  }

  const accepted = await started.json() as { runId?: string; batchId?: string; report: string };
  const id = accepted.runId ?? accepted.batchId!;
  const kind = accepted.runId ? 'runs' : 'batches';

  console.log(`Accepted: 202, ${kind.slice(0, -1)} ${id}`);

  // Poll the way a pipeline does: on `finished`, not on a list of statuses held in two places.
  let state: { finished: boolean; passed: boolean; status?: string; state?: string } | null = null;

  for (let waited = 0; waited < 90_000; waited += 1000) {
    const response = await fetch(`${BASE}/api/v1/${kind}/${id}`, { headers });
    state = await response.json();

    if (state!.finished) break;
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }

  if (!state?.finished) throw new Error('The run never finished.');
  console.log(`Finished: ${state.status ?? state.state}, passed=${state.passed}`);

  const report = await fetch(`${BASE}${accepted.report}`, { headers });
  const xml = await report.text();

  if (report.headers.get('content-type')?.includes('xml') !== true) {
    throw new Error(`The report came back as ${report.headers.get('content-type')}.`);
  }

  // The shape a build system reads. Checked rather than assumed, because "we emit JUnit" is only
  // true if a reader agrees.
  for (const needle of ['<testsuites', '<testsuite ', '<testcase ', 'time=', 'timestamp=']) {
    if (!xml.includes(needle)) throw new Error(`The report has no ${needle} in it.`);
  }

  const cases = (xml.match(/<testcase /g) ?? []).length;
  const failures = (xml.match(/<failure /g) ?? []).length;

  console.log(`JUnit: ${xml.length} bytes, ${cases} case(s), ${failures} failure(s).`);
  console.log(xml.split('\n').slice(0, 4).join('\n'));
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
  await page.waitForTimeout(1000);

  await page.screenshot({ path: resolve(OUT, `${name}--${language}-${theme}-desktop.png`) });
  await context.close();
}

/** Adds a schedule through the interface, so the page has something on it worth looking at. */
async function addSchedule(page: Page, projectId: string): Promise<void> {
  await page.goto(`${BASE}/projects/${projectId}/schedules`, { waitUntil: 'networkidle' });

  if (await page.locator('#schedule-name').count() === 0) {
    console.log('No schedule form — needs a scenario and an environment.');
    return;
  }

  await page.fill('#schedule-name', 'Nightly regression');

  // Through the preset button, which is the control most people will use.
  await page.locator('button[data-cron="0 6 * * 1-5"]').click();

  await page.locator('input[name="scenarioIds"]').first().check();
  await page.locator('input[name="environmentIds"]').first().check();

  await page.locator('form[action$="/schedules/save"] button[type="submit"]').click();
  await page.waitForLoadState('networkidle');

  const rows = await page.locator('table tbody tr').count();
  const when = await page.locator('.schedule-when').first().innerText().catch(() => '—');

  console.log(`Schedules: ${rows} row(s). First reads: ${when.replace(/\s+/g, ' ').trim()}`);
}

async function main(): Promise<void> {
  if (!PASSWORD) throw new Error('Set PROOFFLOW_PASSWORD.');
  await mkdir(OUT, { recursive: true });

  const browser = await chromium.launch();

  const context = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
  await context.addCookies([{ name: '.AspNetCore.Culture', value: 'c=en|uic=en', url: BASE }]);

  const page = await context.newPage();

  try {
    await signIn(page);
    const projectId = await firstProjectId(page);

    await addSchedule(page, projectId);

    const key = await issueKey(page, projectId);
    await pipeline(projectId, key);

    const session = await page.context().storageState();
    await page.close();

    for (const language of ['en', 'fa'] as const) {
      for (const theme of ['light', 'dark'] as const) {
        await shoot(browser, session, `/projects/${projectId}/schedules`,
          'schedules', language, theme);
        await shoot(browser, session, `/projects/${projectId}/settings`,
          'project-settings', language, theme);
      }
    }

    console.log('Screenshots written to docs/ui/raw.');
  } finally {
    await browser.close();
  }
}

await main();
