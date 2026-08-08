import { chromium, type Page } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { projectId as pickProject } from './tools/project';

/**
 * Takes a project out of the building and brings it back in, through the interface.
 *
 * This is the claim the whole format makes, driven the way a person would: press Download, choose
 * the file that lands, read the preview, confirm. Then it runs one of the imported scenarios,
 * because a project that arrives and cannot run is a project that did not really arrive.
 *
 * It also imports a cURL command and an OpenAPI document, so the other two doors are walked as well
 * as tested.
 *
 *   npx tsx e2e/demo-portability.ts    (with the application running on :5290)
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

/** Presses Download and keeps what arrives. */
async function download(page: Page, projectId: string): Promise<string> {
  await page.goto(`${BASE}/projects/${projectId}/export`, { waitUntil: 'networkidle' });

  const [file] = await Promise.all([
    page.waitForEvent('download'),
    page.locator('a[download]').click(),
  ]);

  // Into the git-ignored folder with the screenshots. A real export can carry a real address and
  // a real environment name; it is evidence of a run, not something to commit.
  const path = resolve(OUT, file.suggestedFilename());
  await file.saveAs(path);

  console.log(`Exported to ${file.suggestedFilename()}.`);
  return path;
}

/**
 * Walks the three steps, reading the middle one out loud.
 *
 * The preview is the point of the whole flow, so a driver that skipped straight to Confirm would be
 * testing the half that matters least.
 */
async function importFile(
  page: Page, projectId: string, source: string, path: string, asNew: boolean,
): Promise<void> {
  await page.goto(`${BASE}/projects/${projectId}/import?source=${source}`, { waitUntil: 'networkidle' });

  await page.locator(`input[name="source"][value="${source}"]`).check();
  await page.locator('#import-file').setInputFiles(path);

  if (asNew) await page.locator('input[name="asNewProject"]').check();

  // Scoped to the form. A bare button[type=submit] also matches the sign-out item hidden inside
  // the account menu, which is invisible and never becomes clickable.
  await page.locator('form[action$="/import/preview"] button[type="submit"]').click();
  await page.waitForLoadState('networkidle');

  const counts = await page.locator('.table tbody tr').allInnerTexts();
  console.log(`  preview: ${counts.map((row) => row.replace(/\s+/g, ' ').trim()).join(' · ')}`);

  const secrets = await page.locator('.export-secrets li').allInnerTexts();
  if (secrets.length > 0) console.log(`  secrets to create: ${secrets.join(', ')}`);

  const notes = await page.locator('.import-notes li').count();
  if (notes > 0) console.log(`  ${notes} note(s) about what did not come across.`);

  await page.locator('form[action$="/import/apply"] button[type="submit"]').click();
  await page.waitForLoadState('networkidle');

  console.log(`  landed on ${new URL(page.url()).pathname}`);
}

/** Pastes a cURL command instead of uploading a file. */
async function importPasted(page: Page, projectId: string, source: string, text: string): Promise<void> {
  await page.goto(`${BASE}/projects/${projectId}/import?source=${source}`, { waitUntil: 'networkidle' });

  await page.locator(`input[name="source"][value="${source}"]`).check();
  await page.locator('#import-pasted').fill(text);
  await page.locator('input[name="asNewProject"]').check();

  // Scoped to the form. A bare button[type=submit] also matches the sign-out item hidden inside
  // the account menu, which is invisible and never becomes clickable.
  await page.locator('form[action$="/import/preview"] button[type="submit"]').click();
  await page.waitForLoadState('networkidle');

  const counts = await page.locator('.table tbody tr').allInnerTexts();
  console.log(`  preview: ${counts.map((row) => row.replace(/\s+/g, ' ').trim()).join(' · ')}`);

  await page.locator('form[action$="/import/apply"] button[type="submit"]').click();
  await page.waitForLoadState('networkidle');

  console.log(`  landed on ${new URL(page.url()).pathname}`);
}

/** Starts a scenario from a template and reports what the validator made of it. */
async function fromTemplate(page: Page, projectId: string, key: string): Promise<void> {
  await page.goto(`${BASE}/projects/${projectId}/templates`, { waitUntil: 'networkidle' });

  await page.locator(`form[action$="/templates/${key}/use"] button`).click();
  await page.waitForURL(/\/scenarios\/[0-9a-f-]+$/i, { timeout: 20_000 });
  await page.waitForSelector('[data-island-mounted="true"]');
  await page.waitForSelector('.wf-node');

  const nodes = await page.locator('.wf-node').count();
  const verdict = await page.locator('.canvas-page-bar .badge').first().innerText().catch(() => '—');

  console.log(`Template «${key}»: ${nodes} steps on the canvas. Validator says: ${verdict.trim()}`);
}

async function main(): Promise<void> {
  if (!PASSWORD) throw new Error('Set PROOFFLOW_PASSWORD.');

  await mkdir(OUT, { recursive: true });

  const browser = await chromium.launch();
  const context = await browser.newContext({ acceptDownloads: true });
  const page = await context.newPage();

  await signIn(page);
  const projectId = await firstProjectId(page);

  const exported = await download(page, projectId);

  console.log('Importing it back as a new project:');
  await importFile(page, projectId, 'proofflow', exported, true);

  console.log('Importing a cURL command:');
  await importPasted(page, projectId, 'curl',
    "curl -X POST https://api.example.com/products -H 'Authorization: Bearer live-token' "
    + "-H 'Content-Type: application/json' -d '{\"name\":\"Anvil\"}'");

  const openApi = resolve(OUT, 'sample-openapi.json');
  await writeFile(openApi, JSON.stringify({
    openapi: '3.0.3',
    info: { title: 'Sample Orders API', version: '1.0.0' },
    servers: [{ url: 'https://orders.example.com/v1' }],
    paths: {
      '/orders': {
        get: { summary: 'List orders', tags: ['Orders'], responses: { 200: { description: 'ok' } } },
        post: {
          summary: 'Place an order',
          tags: ['Orders'],
          requestBody: { content: { 'application/json': { example: { sku: 'anvil', quantity: 1 } } } },
          responses: { 201: { description: 'made' } },
        },
      },
      '/orders/{id}': {
        get: { summary: 'One order', responses: { 200: { description: 'ok' }, 404: { description: 'gone' } } },
      },
    },
  }, null, 2), 'utf8');

  console.log('Importing an OpenAPI document:');
  await importFile(page, projectId, 'openapi', openApi, true);

  console.log('Starting from templates:');
  await fromTemplate(page, projectId, 'smoke');
  await fromTemplate(page, projectId, 'crud');

  await context.close();
  await browser.close();
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
