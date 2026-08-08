import { chromium, type Browser, type Page } from '@playwright/test';
import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { mkdir, rm } from 'node:fs/promises';
import { resolve } from 'node:path';

/**
 * The twenty steps, on a project that did not exist a minute ago.
 *
 * This is the measure the plan says is not negotiable: somebody who cannot write code creates a
 * project, points it at an API, records what correct looks like, notices what moved, draws a test,
 * runs it here and on a machine inside their own network, compares two environments, and reads a
 * failure well enough to fix it. Every step below is the interface — a form filled, a button
 * pressed, a connection dragged. Nothing is written into the database and nothing calls an endpoint
 * the page would not.
 *
 * It stops at the first step that fails, on purpose. A run that skips ahead to polish step twelve
 * while step seven is broken produces a green list and a product nobody can use.
 *
 * It also drives the real agent, as a separate process, over HTTP: enrol it, point an environment
 * at it, and wait for the run to come back. That path cannot be proved any other way — an agent
 * that works in a test harness and not as a program is an agent nobody can run.
 *
 *   npx tsx e2e/acceptance.ts        (with the application running on :5290)
 */

const BASE = process.env.PROOFFLOW_URL ?? 'http://localhost:5290';
const EMAIL = process.env.PROOFFLOW_EMAIL ?? 'demo@proofflow.local';
const PASSWORD = process.env.PROOFFLOW_PASSWORD ?? '';

const OUT = resolve(import.meta.dirname, '../../../docs/ui/raw');
const AGENT = resolve(import.meta.dirname, '../../ProofFlow.Agent/bin/Debug/net10.0');

/** Unique per run, so a second pass is a genuine cold start rather than a resumed one. */
const PROJECT = `Acceptance ${new Date().toISOString().slice(11, 19).replace(/:/g, '')}`;

const LOCAL_ENVIRONMENT = 'Local fake API';
const REMOTE_ENVIRONMENT = 'Behind the firewall';
const BASELINE = 'record';
const SECRET = 'apiToken';
/** Unique too. Runners are not name-unique, and a stale one from a previous pass reads as this one. */
const RUNNER = `Agent ${PROJECT.slice(-6)}`;

let agent: ChildProcess | null = null;

// ---- the harness ------------------------------------------------------------------------------

let step = 0;
const results: { number: number; title: string; note: string }[] = [];

/**
 * Runs one step and prints it.
 *
 * Throwing is how a step fails, and the throw propagates: the caller does not catch, so the run
 * ends where the product does.
 */
async function stage(title: string, body: () => Promise<string>): Promise<void> {
  step++;

  const label = String(step).padStart(2, '0');

  try {
    const note = await body();
    results.push({ number: step, title, note });
    console.log(`  ${label}  ${title.padEnd(46)} ${note}`);
  } catch (error) {
    console.log(`  ${label}  ${title.padEnd(46)} FAILED`);
    console.log('');
    console.log(`Step ${label} — ${title} — did not work.`);
    console.log(String(error instanceof Error ? error.message : error).split('\n')[0]);
    throw error;
  }
}

async function signIn(page: Page): Promise<void> {
  await page.goto(`${BASE}/account/sign-in`, { waitUntil: 'networkidle' });
  await page.fill('#Email', EMAIL);
  await page.fill('#Password', PASSWORD);
  await page.click('button[type="submit"]');
  await page.waitForLoadState('networkidle');

  if (page.url().includes('sign-in')) throw new Error('Sign-in failed. Check PROOFFLOW_PASSWORD.');
}

async function shot(page: Page, name: string): Promise<void> {
  await page.screenshot({ path: resolve(OUT, `acceptance-${name}.png`), fullPage: true });
}

// ---- the steps --------------------------------------------------------------------------------

/** 1 — a project, from the form a new reader meets first. */
async function createProject(page: Page): Promise<string> {
  await page.goto(`${BASE}/projects/new`, { waitUntil: 'networkidle' });
  await page.fill('#Name', PROJECT);

  // Scoped to this form. The shell has its own submit buttons — the workspace switcher is one —
  // and an unscoped selector finds a hidden menu item and waits for it forever.
  await page.locator('form[action="/projects/new"] button[type="submit"]').click();
  await page.waitForURL(/\/projects\/[0-9a-f-]+/i, { timeout: 15_000 });

  const id = page.url().match(/\/projects\/([0-9a-f-]+)/i)?.[1];
  if (!id) throw new Error(`Creating the project did not land on one: ${page.url()}`);

  return id;
}

/** Adds an environment through the short form, then opens it. */
async function addEnvironment(page: Page, projectId: string, name: string): Promise<void> {
  await page.goto(`${BASE}/projects/${projectId}/environments`, { waitUntil: 'networkidle' });

  await page.fill('#new-env-name', name);
  await page.fill('#new-env-url', `${BASE}/fake`);
  await page.click('#new-environment button[type="submit"]');
  await page.waitForLoadState('networkidle');

  await page.locator('.master-item').filter({ hasText: name }).first().click();
  await page.waitForSelector('#env-name');
}

/** Saves the detail form, whatever is currently in it. */
async function saveEnvironment(page: Page): Promise<void> {
  await page.locator('form[data-guard-unsaved] button[type="submit"]').first().click();
  await page.waitForLoadState('networkidle');
}

async function openEnvironment(page: Page, projectId: string, name: string): Promise<void> {
  await page.goto(`${BASE}/projects/${projectId}/environments`, { waitUntil: 'networkidle' });
  await page.locator('.master-item').filter({ hasText: name }).first().click();
  await page.waitForSelector('#env-name');
}

/** 7 — a request in the lab, resolving a variable and a secret on the way out. */
async function sendInTheLab(page: Page, projectId: string, path: string): Promise<string> {
  await page.goto(`${BASE}/projects/${projectId}/request`, { waitUntil: 'networkidle' });
  await page.waitForSelector('[data-island-mounted="true"]');

  await page.locator('input.input-mono').first().fill(`{{environment.baseUrl}}${path}`);
  await page.getByRole('button', { name: /send|ارسال/i }).click();
  await page.waitForSelector('.response-head', { timeout: 25_000 });

  return (await page.locator('.response-head').first().innerText()).replace(/\s+/g, ' ').trim();
}

/** Presses Compare on the open baseline and waits for a verdict. */
async function compare(page: Page): Promise<string> {
  await page.locator('.workbench-bar .btn-primary').first().click();
  await page.waitForSelector('.diff-summary', { timeout: 25_000 });
  await page.waitForTimeout(400);

  return (await page.locator('.diff-summary').first().innerText()).replace(/\s+/g, ' ').trim();
}

/** Adds a node from the palette by its visible name. */
async function addNode(page: Page, search: string, name: string): Promise<void> {
  await page.fill('.node-palette-search input', search);
  await page.waitForTimeout(150);
  await page.locator('.node-palette-item', { hasText: name }).first().click();
  await page.waitForTimeout(250);
}

async function setProperty(page: Page, label: string, value: string): Promise<void> {
  const field = page.locator('.inspector .field', { hasText: label }).first();
  await field.locator('input, textarea').first().fill(value);
  await field.locator('input, textarea').first().blur();
  await page.waitForTimeout(200);
}

/** Drags between two sockets, found by the label in their title rather than by position. */
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

/** Presses Run and waits for the console to reach a verdict rather than for a timer. */
async function runIt(page: Page, timeout = 90_000): Promise<string> {
  await page.locator('.canvas-page-bar button[type="submit"]', { hasText: /Run|اجرا/ }).first().click();
  await page.waitForURL(/\/runs\/[0-9a-f-]+$/i, { timeout: 20_000 });
  await page.waitForSelector('[data-island-mounted="true"]');

  const badge = page.locator('.run-head .badge').first();

  for (let waited = 0; waited < timeout; waited += 500) {
    const text = (await badge.innerText()).trim();
    if (!/Queued|Running|در صف|در حال اجرا/.test(text)) return text;

    await page.waitForTimeout(500);
  }

  throw new Error(`The run never left Queued or Running after ${timeout / 1000}s.`);
}

/** Points the open scenario at one environment, so the next Run uses it. */
async function chooseEnvironment(page: Page, name: string): Promise<void> {
  const picker = page.locator('.canvas-page-bar select').first();
  await picker.selectOption({ label: name });
  await page.waitForTimeout(400);
}

// ---- the agent, as a program ---------------------------------------------------------------

/** Issues a code on the runners page and hands it back. Shown once, so it is read here. */
async function issueCode(page: Page): Promise<string> {
  await page.goto(`${BASE}/runners`, { waitUntil: 'networkidle' });

  await page.fill('#runner-name', RUNNER);
  await page.locator('form[action="/runners"] button[type="submit"]').first().click();
  await page.waitForLoadState('networkidle');

  const code = (await page.locator('.runner-code-value').first().innerText()).trim();
  if (!/^[A-Z0-9]{4}(-[A-Z0-9]{4}){3}$/.test(code)) throw new Error(`No code on the page: «${code}»`);

  return code;
}

/**
 * Enrols and starts the shipped agent binary.
 *
 * A separate process on purpose. The point of a runner is that it is a program somebody copies onto
 * another machine, and everything about it that could go wrong — the credentials file, the console,
 * the polling loop — only goes wrong when it is actually run.
 */
function startAgent(code: string): void {
  const enrolled = spawnSync(resolve(AGENT, 'proofflow-agent.exe'),
    ['enroll', '--url', BASE, '--code', code], { encoding: 'utf8' });

  if (enrolled.status !== 0) {
    throw new Error(`The agent would not enrol: ${enrolled.stderr || enrolled.stdout}`);
  }

  agent = spawn(resolve(AGENT, 'proofflow-agent.exe'), ['run', '--url', BASE],
    { stdio: 'ignore', detached: false });
}

/** Waits for the runners page to say the agent is answering. */
async function waitForAgent(page: Page): Promise<string> {
  for (let waited = 0; waited < 60_000; waited += 1000) {
    await page.goto(`${BASE}/runners`, { waitUntil: 'networkidle' });

    // The row for this runner, not the first row. Older agents from earlier passes are still
    // listed, and one of them sorts above this one.
    const row = page.locator('.runner-table tbody tr', { hasText: RUNNER }).first();
    const state = await row.locator('.status').first().innerText().catch(() => '');

    if (/Ready|آماده/.test(state)) return state.replace(/\s+/g, ' ').trim();

    await page.waitForTimeout(1000);
  }

  throw new Error('The agent never reached Ready. Is proofflow-agent built?');
}

// ---- the run ----------------------------------------------------------------------------------

async function main(): Promise<void> {
  if (!PASSWORD) throw new Error('Set PROOFFLOW_PASSWORD.');
  await mkdir(OUT, { recursive: true });

  // A stale credentials file would silently enrol nothing and poll as an older runner.
  await rm(resolve(AGENT, 'runner.json'), { force: true });

  const browser: Browser = await chromium.launch();

  // Pinned to English: the steps below find buttons and states by the words on them.
  const context = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
  await context.addCookies([{ name: '.AspNetCore.Culture', value: 'c=en|uic=en', url: BASE }]);

  const page = await context.newPage();

  console.log(`\nProofFlow — the twenty steps, on «${PROJECT}»\n`);

  try {
    await signIn(page);

    let projectId = '';
    let scenario = '';

    await stage('Create a project', async () => {
      projectId = await createProject(page);
      return PROJECT;
    });

    await stage('Point an environment at the API', async () => {
      await addEnvironment(page, projectId, LOCAL_ENVIRONMENT);

      // The fake API is on this host, which the guard treats as loopback and refuses by default.
      // Saying otherwise is a decision about this environment, and the switch is where it is made.
      await page.locator('input[name="AllowPrivateNetwork"]').check();
      await saveEnvironment(page);

      await openEnvironment(page, projectId, LOCAL_ENVIRONMENT);
      if (!await page.locator('input[name="AllowPrivateNetwork"]').isChecked()) {
        throw new Error('The private-network switch did not stick.');
      }

      return `${LOCAL_ENVIRONMENT}, loopback allowed`;
    });

    await stage('Add a second environment', async () => {
      await addEnvironment(page, projectId, REMOTE_ENVIRONMENT);
      await page.locator('input[name="AllowPrivateNetwork"]').check();
      await saveEnvironment(page);
      return REMOTE_ENVIRONMENT;
    });

    await stage('Add a variable', async () => {
      await openEnvironment(page, projectId, LOCAL_ENVIRONMENT);
      await page.fill('#var-name', 'recordId');
      await page.fill('#var-value', '1');

      // Across the project rather than to this one environment. The default is the narrower
      // choice, which is right for a base URL or a page size; this value is the same wherever the
      // test runs, and a test that runs in two places needs it in both.
      await page.locator('#var-scope').selectOption('');

      await page.locator('#var-name').press('Enter');
      await page.waitForLoadState('networkidle');

      const rows = await page.locator('.variable-row, tr', { hasText: 'recordId' }).count();
      if (rows === 0) throw new Error('The variable is not on the page after saving.');

      return 'recordId = 1';
    });

    await stage('Add a secret', async () => {
      await page.fill('#secret-name', SECRET);
      await page.fill('#secret-value', 'a-real-token-value');
      await page.locator('#secret-value').press('Enter');
      await page.waitForLoadState('networkidle');

      const html = await page.content();
      if (!html.includes(SECRET)) throw new Error('The secret is not listed.');
      if (html.includes('a-real-token-value')) throw new Error('The value came back to the page.');

      return `${SECRET}, value never returned`;
    });

    await stage('Send a request', async () => {
      const head = await sendInTheLab(page, projectId, '/volatile');
      if (!/200/.test(head)) throw new Error(`The response was not 200: ${head}`);
      return head.slice(0, 40);
    });

    await stage('Record what correct looks like', async () => {
      await page.getByRole('button', { name: /save as baseline|ذخیره به‌عنوان/i }).click();
      await page.waitForSelector('[role="dialog"]');
      await page.locator('[role="dialog"] input').first().fill(BASELINE);
      await page.locator('[role="dialog"] .btn-primary').click();
      await page.waitForURL(/\/baselines\/[0-9a-f-]+$/i, { timeout: 25_000 });

      return BASELINE;
    });

    await stage('Compare, and see what moved', async () => {
      const summary = await compare(page);
      if (!/\d/.test(summary)) throw new Error(`Nothing was reported: ${summary}`);
      await shot(page, 'compared');

      return summary.slice(0, 46);
    });

    await stage('Turn the moving fields into rules', async () => {
      if (await page.locator('.suggestions').count() === 0) {
        throw new Error('Nothing was suggested to ignore.');
      }

      // Select them all, then save. Two presses, because accepting a suggestion is a decision and
      // the panel makes you make it rather than making it for you.
      await page.locator('.suggestion-foot .btn-ghost').first().click();
      await page.locator('.suggestion-foot .btn-primary').click();
      await page.waitForSelector('.suggestions', { state: 'detached', timeout: 25_000 });

      return 'the dynamic fields, ignored';
    });

    await stage('Compare again — nothing moves', async () => {
      const summary = await compare(page);

      // The verdict is a badge rather than a word to match: "identical" is one translation of it.
      if (await page.locator('.diff-summary .badge-pass').count() === 0) {
        throw new Error(`Still different under the new rules: ${summary}`);
      }

      await shot(page, 'identical');
      return 'identical';
    });

    await stage('Draw a test on the canvas', async () => {
      await page.goto(`${BASE}/projects/${projectId}/scenarios/new`, { waitUntil: 'networkidle' });
      await page.waitForSelector('[data-island-mounted="true"]');
      await page.waitForSelector('.wf-node');

      await addNode(page, 'HTTP', 'HTTP request');
      await page.locator('.wf-node').nth(1).click();
      await setProperty(page, 'Address', '{{environment.baseUrl}}/records/{{vars.recordId}}');

      await addNode(page, 'status', 'Check the status code');

      await connect(page, 0, 'Then', 1, 'In');
      await connect(page, 1, 'Then', 2, 'In');
      await connect(page, 1, 'Response', 2, 'Response');

      await page.locator('.canvas-bar .btn-primary').click();
      await page.waitForTimeout(1200);

      const verdict = (await page.locator('.canvas-bar .badge').first().innerText()).trim();
      if (/draft|پیش‌نویس/i.test(verdict)) throw new Error(`The validator is not happy: ${verdict}`);

      scenario = page.url();
      await shot(page, 'canvas');

      return `3 steps · ${verdict}`;
    });

    await stage('Run it here', async () => {
      await chooseEnvironment(page, LOCAL_ENVIRONMENT);
      const verdict = await runIt(page);

      if (!/Passed|قبول/i.test(verdict)) throw new Error(`The local run said: ${verdict}`);
      await shot(page, 'run-local');

      return verdict;
    });

    let code = '';

    await stage('Enrol a machine of your own', async () => {
      code = await issueCode(page);
      await shot(page, 'runner-code');
      return `code issued, shown once`;
    });

    await stage('Start the agent, and watch it arrive', async () => {
      startAgent(code);
      return await waitForAgent(page);
    });

    await stage('Send the second environment through it', async () => {
      await openEnvironment(page, projectId, REMOTE_ENVIRONMENT);
      await page.locator('#env-runner').selectOption({ label: new RegExp(RUNNER) as never })
        .catch(async () => {
          const option = page.locator('#env-runner option', { hasText: RUNNER }).first();
          await page.locator('#env-runner').selectOption(await option.getAttribute('value') ?? '');
        });
      await saveEnvironment(page);

      await openEnvironment(page, projectId, REMOTE_ENVIRONMENT);
      const chosen = await page.locator('#env-runner').inputValue();
      if (!chosen) throw new Error('The runner did not stick to the environment.');

      return `${REMOTE_ENVIRONMENT} → ${RUNNER}`;
    });

    await stage('Run it there', async () => {
      await page.goto(scenario, { waitUntil: 'networkidle' });
      await page.waitForSelector('[data-island-mounted="true"]');

      await chooseEnvironment(page, REMOTE_ENVIRONMENT);
      const verdict = await runIt(page, 180_000);

      if (!/Passed|قبول/i.test(verdict)) throw new Error(`The remote run said: ${verdict}`);
      await shot(page, 'run-remote');

      return `${verdict}, from the agent`;
    });

    await stage('Run both places at once', async () => {
      // The chooser is a plain form; the grid island only exists once a matrix does.
      await page.goto(`${BASE}/projects/${projectId}/matrix`, { waitUntil: 'networkidle' });

      const scenarios = page.locator('input[name="scenarioIds"]');
      const environments = page.locator('input[name="environmentIds"]');

      for (let at = 0; at < await scenarios.count(); at++) await scenarios.nth(at).check();
      for (let at = 0; at < await environments.count(); at++) await environments.nth(at).check();

      await page.fill('#matrix-name', 'Both places');
      await page.locator('form[action$="/matrix/start"] button[type="submit"]').click();
      await page.waitForURL(/\/matrix\/[0-9a-f-]+$/i, { timeout: 20_000 });
      await page.waitForSelector('[data-island-mounted="true"]');

      const badge = page.locator('.matrix-head .badge').first();

      // One of the two columns runs on the agent, which polls, so this waits minutes rather than
      // seconds. That is the product being honest about what a remote run costs.
      for (let waited = 0; waited < 240_000; waited += 1000) {
        const text = (await badge.innerText().catch(() => '')).trim();

        if (text && !/Queued|Running|در صف|در حال اجرا/.test(text)) {
          await shot(page, 'matrix');

          const cells = await page.locator('.matrix-cell').allInnerTexts();
          return `${cells.length} cells · ${text}`;
        }

        await page.waitForTimeout(1000);
      }

      throw new Error('The matrix never settled.');
    });

    await stage('Read the history', async () => {
      await page.goto(`${BASE}/projects/${projectId}/runs`, { waitUntil: 'networkidle' });

      const rows = await page.locator('tbody tr').count();
      if (rows < 2) throw new Error(`Only ${rows} run(s) in the history.`);

      await shot(page, 'history');
      return `${rows} runs`;
    });

    await stage('Break it, and understand why', async () => {
      await page.goto(scenario, { waitUntil: 'networkidle' });
      await page.waitForSelector('[data-island-mounted="true"]');

      await page.locator('.wf-node').nth(1).click();
      await setProperty(page, 'Address', '{{environment.baseUrl}}/status/500');
      await page.locator('.canvas-bar .btn-primary').click();
      await page.waitForTimeout(1200);

      await chooseEnvironment(page, LOCAL_ENVIRONMENT);
      const verdict = await runIt(page);

      if (!/Failed|رد/i.test(verdict)) throw new Error(`Breaking it did not fail it: ${verdict}`);

      const outcome = (await page.locator('.run-outcome').first().innerText()).trim();
      if (outcome.length < 8) throw new Error('The failure says nothing.');

      await shot(page, 'run-failed');
      return outcome.replace(/\s+/g, ' ').slice(0, 46);
    });

    await stage('Put it back, and run it again', async () => {
      await page.goto(scenario, { waitUntil: 'networkidle' });
      await page.waitForSelector('[data-island-mounted="true"]');

      await page.locator('.wf-node').nth(1).click();
      await setProperty(page, 'Address', '{{environment.baseUrl}}/records/{{vars.recordId}}');
      await page.locator('.canvas-bar .btn-primary').click();
      await page.waitForTimeout(1200);

      await chooseEnvironment(page, LOCAL_ENVIRONMENT);
      const verdict = await runIt(page);

      if (!/Passed|قبول/i.test(verdict)) throw new Error(`The re-run said: ${verdict}`);
      return verdict;
    });

    console.log(`\n  ${results.length} steps, all of them. The product does what it says.\n`);
  } finally {
    agent?.kill();
    await browser.close();
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
