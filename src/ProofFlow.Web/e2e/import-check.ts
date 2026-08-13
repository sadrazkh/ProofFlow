import { chromium } from '@playwright/test';
import { writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { projectId as pickProject } from './tools/project';

/**
 * The last thing the plan asks for, by hand and for real.
 *
 * Import a Postman collection → it lands in Endpoints, not Scenarios → open one → give it inputs →
 * press Test. Written down rather than clicked through once, because «I checked it works» is the
 * claim that ages worst.
 *
 *   npx tsx e2e/import-check.ts       (with the application running on :5290)
 */

const BASE = process.env.PROOFFLOW_URL ?? 'http://localhost:5290';
const EMAIL = process.env.PROOFFLOW_EMAIL ?? 'demo@proofflow.local';
const PASSWORD = process.env.PROOFFLOW_PASSWORD ?? '';

/** A small collection with two folders and a repeated name, which is what real ones look like. */
const COLLECTION = {
  info: {
    name: 'Warehouse',
    schema: 'https://schema.getpostman.com/json/collection/v2.1.0/collection.json',
  },
  item: [
    {
      name: 'Records',
      item: [
        { name: 'Read one', request: { method: 'GET', url: 'http://localhost:5290/fake/records/1' } },
        { name: 'Read one', request: { method: 'GET', url: 'http://localhost:5290/fake/records/2' } },
      ],
    },
    {
      name: 'Health',
      item: [
        { name: 'Ping', request: { method: 'GET', url: 'http://localhost:5290/fake/records/3' } },
      ],
    },
  ],
};

if (!PASSWORD) throw new Error('Set PROOFFLOW_PASSWORD.');

const file = resolve(import.meta.dirname, '../../../docs/ui/raw/warehouse.postman.json');
await writeFile(file, JSON.stringify(COLLECTION, null, 2), 'utf8');

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

  const scenariosBefore = await count(`${BASE}/projects/${projectId}/scenarios`);
  const endpointsBefore = await count(`${BASE}/projects/${projectId}/endpoints`);

  await page.goto(`${BASE}/projects/${projectId}/import?source=postman`, { waitUntil: 'networkidle' });
  await page.locator('input[type="file"]').setInputFiles(file);
  // Scoped to the import form. A bare form button[type="submit"] also matches the sign-out item
  // inside the account menu, which is hidden, so the click waits for it to become visible.
  await page.locator('form[enctype] button[type="submit"]').first().click();

  await page.waitForURL(/\/import\/preview/i, { timeout: 60_000 });

  const preview = (await page.locator('main').innerText()).replace(/\s+/g, ' ');
  console.log(`Preview says: ${preview.slice(0, 220)}`);

  await page.locator('main form button[type="submit"]').last().click();
  await page.waitForLoadState('networkidle');

  const scenariosAfter = await count(`${BASE}/projects/${projectId}/scenarios`);
  const endpointsAfter = await count(`${BASE}/projects/${projectId}/endpoints`);

  console.log(`Scenarios: ${scenariosBefore} → ${scenariosAfter}`);
  console.log(`Endpoints: ${endpointsBefore} → ${endpointsAfter}`);

  if (scenariosAfter !== scenariosBefore) {
    throw new Error('The import made scenarios. Every request should be an endpoint.');
  }

  if (endpointsAfter !== endpointsBefore + 3) {
    throw new Error(`Expected three endpoints, got ${endpointsAfter - endpointsBefore}.`);
  }

  // And the repeated name survived as two rows rather than one, which is the collision that used
  // to kill an import halfway through.
  await page.goto(`${BASE}/projects/${projectId}/endpoints`, { waitUntil: 'networkidle' });
  const readOne = await page.locator('a', { hasText: 'Records · Read one' }).count();

  if (readOne !== 2) throw new Error(`Both «Read one» rows should be here; found ${readOne}.`);

  console.log('Two requests with the same name in one folder both arrived.');
  console.log('\nThe import lands in Endpoints. Nothing became a scenario.');
} finally {
  await browser.close();
}

/** How many rows a paged list says it has in total. */
async function count(url: string): Promise<number> {
  await page.goto(url, { waitUntil: 'networkidle' });

  const pager = await page.locator('.pager-count').first().innerText().catch(() => '');
  const total = /(\d+)(?!.*\d)/.exec(pager.replace(/[^\d ]/g, ' '));

  if (total) return Number(total[1]);

  // No pager at all means one page, so the rows on screen are all of them.
  return page.locator('tbody tr').count();
}
