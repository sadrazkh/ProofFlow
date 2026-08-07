import { chromium, devices, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

type StorageState = Awaited<ReturnType<BrowserContext['storageState']>>;

/**
 * Screenshots of the running application, for review.
 *
 * Every page, in both languages, at three widths, in both themes — because the failures this is
 * meant to catch only appear in one combination: a heading that fits in English and overflows in
 * Persian, a border that vanishes in dark mode, a toolbar that stacks wrongly at 390px.
 *
 * Signed-out and signed-in pages are captured from separate browser contexts. Sharing one meant
 * the sign-in page redirected to the dashboard and quietly produced twelve screenshots of the
 * wrong thing under the right filename — the failure mode where the evidence looks complete.
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

type Target = { name: string; path: string; auth: boolean };

const PAGES: Target[] = [
  { name: 'sign-in', path: '/account/sign-in', auth: false },
  { name: 'sign-up', path: '/account/sign-up', auth: false },
  { name: 'not-found', path: '/no-such-page', auth: false },
  { name: 'dashboard', path: '/', auth: true },
  { name: 'projects', path: '/projects', auth: true },
  { name: 'project-new', path: '/projects/new', auth: true },
  { name: 'activity', path: '/activity', auth: true },
  // Development only, and the cheapest visual regression test there is: one page covering every
  // component in both themes.
  { name: 'design', path: '/design', auth: true },
];

/**
 * Pages that live inside a project, so their address is only known once a project exists.
 *
 * Discovered rather than hard-coded: the demo seed makes new ids on every fresh database, and a
 * matrix that silently captured a 404 under the right filename would be worse than one that
 * skipped them.
 */
const PROJECT_PAGES: { name: string; path: (projectId: string) => string }[] = [
  { name: 'environments', path: (id) => `/projects/${id}/environments` },
  { name: 'request', path: (id) => `/projects/${id}/request` },
  { name: 'baselines', path: (id) => `/projects/${id}/baselines` },
  { name: 'datasets', path: (id) => `/projects/${id}/datasets` },
  { name: 'dataset-new', path: (id) => `/projects/${id}/datasets/new` },
  { name: 'captures', path: (id) => `/projects/${id}/captures` },
  { name: 'wizard', path: (id) => `/projects/${id}/wizard` },
  { name: 'scenarios', path: (id) => `/projects/${id}/scenarios` },
  { name: 'runs', path: (id) => `/projects/${id}/runs` },
  { name: 'matrix', path: (id) => `/projects/${id}/matrix` },
];

type Combination = { language: 'fa' | 'en'; theme: 'light' | 'dark'; viewport: Viewport };

async function newContext(
  browser: Browser,
  { language, theme, viewport }: Combination,
  session?: StorageState,
): Promise<BrowserContext> {
  const context = await browser.newContext({
    ...(viewport.name === 'mobile' ? devices['Pixel 7'] : {}),
    viewport: { width: viewport.width, height: viewport.height },
    locale: language === 'fa' ? 'fa-IR' : 'en-GB',
    colorScheme: theme,
    deviceScaleFactor: 2,
    ...(session ? { storageState: session } : {}),
  });

  // Read before first paint by the inline script in the layout, so it has to be in place before
  // the first navigation rather than set and reloaded.
  await context.addInitScript(`localStorage.setItem('proofflow-theme', '${theme}')`);

  await context.addCookies([
    { name: '.AspNetCore.Culture', value: `c=${language}|uic=${language}`, url: BASE },
    // Pinned rather than left to the machine's own zone, so a timestamp in a screenshot means the
    // same thing on a laptop in Tehran and on a CI runner in UTC.
    { name: 'proofflow.tz', value: 'Asia%2FTehran', url: BASE },
  ]);

  return context;
}

/**
 * Signs in once and keeps the cookie.
 *
 * Twelve combinations meant twelve sign-ins, which is more than the application's own rate limit
 * on that endpoint allows in a minute — so the later combinations were bounced and their pages
 * silently skipped. Reusing one session is both faster and the thing that stops this script from
 * testing the rate limiter instead of the interface.
 */
async function establishSession(browser: Browser): Promise<
  { state: StorageState; projectId: string | null; baselinePath: string | null } | undefined
> {
  if (!PASSWORD) return undefined;

  const context = await browser.newContext();
  const page = await context.newPage();

  await page.goto(`${BASE}/account/sign-in`, { waitUntil: 'networkidle' });
  await page.fill('#Email', EMAIL);
  await page.fill('#Password', PASSWORD);
  await page.click('button[type="submit"]');
  await page.waitForLoadState('networkidle');

  if (page.url().includes('sign-in')) {
    await context.close();
    return undefined;
  }

  const state = await context.storageState();

  await page.goto(`${BASE}/projects`, { waitUntil: 'networkidle' });
  const href = await page.locator('a.project-card').first().getAttribute('href').catch(() => null);
  const projectId = href?.split('/').pop() ?? null;

  // The baseline detail page is worth shooting and its address is only known once one exists.
  // e2e/demo.ts creates it; without that run this stays null and the skip is reported.
  let baselinePath: string | null = null;

  if (projectId) {
    await page.goto(`${BASE}/projects/${projectId}/baselines`, { waitUntil: 'networkidle' });
    baselinePath = await page.locator('td a[href*="/baselines/"]').first()
      .getAttribute('href').catch(() => null);
  }

  await context.close();
  return { state, projectId, baselinePath };
}

async function capture(page: Page, target: Target, combination: Combination): Promise<void> {
  await page.goto(`${BASE}${target.path}`, { waitUntil: 'networkidle' });

  // Icons are drawn by a module that runs after load; without this the shots catch empty <i>
  // placeholders and every review comment is about missing icons.
  await page.waitForTimeout(350);

  const { language, theme, viewport } = combination;
  await page.screenshot({
    path: resolve(OUT, `${target.name}--${language}-${theme}-${viewport.name}.png`),
    fullPage: viewport.name !== 'mobile',
  });
}

async function main(): Promise<void> {
  await mkdir(OUT, { recursive: true });

  const browser = await chromium.launch();
  const session = await establishSession(browser);
  let shots = 0;
  let skipped = 0;

  for (const language of ['fa', 'en'] as const) {
    for (const theme of ['light', 'dark'] as const) {
      for (const viewport of VIEWPORTS) {
        const combination: Combination = { language, theme, viewport };

        // Anonymous pages, in a context that has never signed in.
        const anonymous = await newContext(browser, combination);
        const anonymousPage = await anonymous.newPage();
        for (const target of PAGES.filter((p) => !p.auth)) {
          await capture(anonymousPage, target, combination);
          shots++;
        }
        await anonymous.close();

        // A second, separate context for everything behind a session, seeded with the one
        // sign-in rather than repeating it.
        if (session) {
          const authed = await newContext(browser, combination, session.state);
          const authedPage = await authed.newPage();

          for (const target of PAGES.filter((p) => p.auth)) {
            await capture(authedPage, target, combination);
            shots++;
          }

          if (session.projectId) {
            for (const target of PROJECT_PAGES) {
              await capture(
                authedPage,
                { name: target.name, path: target.path(session.projectId), auth: true },
                combination);
              shots++;
            }
          } else {
            skipped += PROJECT_PAGES.length;
          }

          if (session.baselinePath) {
            await capture(
              authedPage,
              { name: 'baseline-detail', path: session.baselinePath, auth: true },
              combination);
            shots++;
          } else {
            skipped++;
          }

          await authed.close();
        } else {
          skipped += PAGES.filter((p) => p.auth).length + PROJECT_PAGES.length;
        }
      }
    }
  }

  await browser.close();
  console.log(`${shots} screenshots written to ${OUT}`);

  // Said out loud rather than left to be inferred from a smaller-than-expected count.
  if (skipped > 0) {
    console.warn(
      `${skipped} signed-in screenshots were skipped: ` +
      (PASSWORD ? 'sign-in failed.' : 'PROOFFLOW_PASSWORD was not set.'),
    );
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
