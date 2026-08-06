import { chromium, devices, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

/**
 * Screenshots of the running application, for review.
 *
 * Every page, in both languages, at three widths, in both themes — because the failures this is
 * meant to catch are exactly the ones that only appear in one combination: a heading that fits in
 * English and overflows in Persian, a border that vanishes in dark mode, a toolbar that stacks
 * wrongly at 390px.
 *
 * Output goes to docs/ui/raw, which is git-ignored: a screenshot of a running instance can hold a
 * real token or a real address. Only reviewed and redacted copies belong in the repository.
 *
 *   npx tsx e2e/shoot.ts            (with the application running on :5290)
 */

const BASE = process.env.PROOFFLOW_URL ?? 'http://localhost:5290';
const EMAIL = process.env.PROOFFLOW_EMAIL ?? 'demo@proofflow.local';
const PASSWORD = process.env.PROOFFLOW_PASSWORD ?? '';

const OUT = resolve(import.meta.dirname, '../../../docs/ui/raw');

type Viewport = { name: string; width: number; height: number };

const VIEWPORTS: Viewport[] = [
  { name: 'desktop', width: 1440, height: 960 },
  { name: 'tablet', width: 834, height: 1112 },
  { name: 'mobile', width: 390, height: 844 },
];

const PAGES: { name: string; path: string; auth: boolean }[] = [
  { name: 'sign-in', path: '/account/sign-in', auth: false },
  { name: 'sign-up', path: '/account/sign-up', auth: false },
  { name: 'dashboard', path: '/', auth: true },
  { name: 'projects', path: '/projects', auth: true },
  { name: 'project-new', path: '/projects/new', auth: true },
  { name: 'activity', path: '/activity', auth: true },
  { name: 'not-found', path: '/no-such-page', auth: false },
];

async function signIn(page: Page): Promise<boolean> {
  if (!PASSWORD) return false;

  await page.goto(`${BASE}/account/sign-in`, { waitUntil: 'networkidle' });
  await page.fill('#Email', EMAIL);
  await page.fill('#Password', PASSWORD);
  await page.click('button[type="submit"]');
  await page.waitForLoadState('networkidle');

  return !page.url().includes('sign-in');
}

async function main(): Promise<void> {
  await mkdir(OUT, { recursive: true });

  const browser = await chromium.launch();
  let shots = 0;

  for (const language of ['fa', 'en'] as const) {
    for (const theme of ['light', 'dark'] as const) {
      for (const viewport of VIEWPORTS) {
        const context = await browser.newContext({
          ...(viewport.name === 'mobile' ? devices['Pixel 7'] : {}),
          viewport: { width: viewport.width, height: viewport.height },
          locale: language === 'fa' ? 'fa-IR' : 'en-GB',
          colorScheme: theme,
          deviceScaleFactor: 2,
        });

        // Both are read before first paint by the inline script in the layout, so they have to be
        // in place before the first navigation rather than set and reloaded.
        await context.addInitScript(`localStorage.setItem('proofflow-theme', '${theme}')`);
        await context.addCookies([{
          name: '.AspNetCore.Culture',
          value: `c=${language}|uic=${language}`,
          url: BASE,
        }]);

        const page = await context.newPage();
        const authenticated = await signIn(page);

        for (const target of PAGES) {
          if (target.auth && !authenticated) continue;

          await page.goto(`${BASE}${target.path}`, { waitUntil: 'networkidle' });
          // Icons are drawn by a module that runs after load; without this the shots catch empty
          // <i> placeholders and every review comment is about missing icons.
          await page.waitForTimeout(350);

          const file = `${target.name}--${language}-${theme}-${viewport.name}.png`;
          await page.screenshot({
            path: resolve(OUT, file),
            fullPage: viewport.name !== 'mobile',
          });
          shots++;
        }

        await context.close();
      }
    }
  }

  await browser.close();
  console.log(`${shots} screenshots written to ${OUT}`);

  if (!PASSWORD) {
    console.warn('PROOFFLOW_PASSWORD was empty, so only the anonymous pages were captured.');
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
