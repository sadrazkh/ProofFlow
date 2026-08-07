import { expect, test as setup } from '@playwright/test';
import { resolve } from 'node:path';

/**
 * Signs in once, for the whole run.
 *
 * The suite used to sign in per test — twenty times, against an endpoint the application rate
 * limits to twelve attempts a minute by address. Eight were refused, and because a refused sign-in
 * leaves the browser on the sign-in page, those tests audited *that* page and reported it under
 * the name of the page they never reached. Every one of them passed.
 *
 * So: one sign-in, saved, reused. It is faster, and it stops this suite from quietly measuring the
 * rate limiter.
 */

export const STATE_FILE = resolve(import.meta.dirname, '.auth/state.json');

const BASE = process.env.PROOFFLOW_URL ?? 'http://localhost:5290';
const EMAIL = process.env.PROOFFLOW_EMAIL ?? 'demo@proofflow.local';
const PASSWORD = process.env.PROOFFLOW_PASSWORD ?? '';

setup('sign in', async ({ page, context }) => {
  setup.skip(!PASSWORD, 'PROOFFLOW_PASSWORD is not set.');

  await page.goto(`${BASE}/account/sign-in`, { waitUntil: 'networkidle' });
  await page.fill('#Email', EMAIL);
  await page.fill('#Password', PASSWORD);
  await page.click('button[type="submit"]');
  await page.waitForLoadState('networkidle');

  expect(page.url(), 'sign-in did not succeed').not.toContain('sign-in');

  await context.storageState({ path: STATE_FILE });
});
