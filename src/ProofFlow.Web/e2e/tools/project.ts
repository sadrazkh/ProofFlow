import type { Page } from '@playwright/test';

/**
 * Which project the drivers and the screenshot matrix work on.
 *
 * Named rather than "the first card", and that stopped being a nicety the moment importing existed:
 * the project list is ordered by when it last changed, so importing three files puts three new
 * projects above the demo one and every script that took the first card silently switched to a
 * project with no runs, no baselines and nothing to photograph. Nothing would have failed — the
 * pages all render — and the evidence would have looked complete.
 *
 * The demo seed's first project by default, overridable for anybody pointing these at their own
 * instance.
 */
export const PROJECT_NAME = process.env.PROOFFLOW_PROJECT ?? 'Catalog API';

/**
 * The id of that project, or the first one if it is not there.
 *
 * The fallback is deliberate and narrow: somebody running this against an instance that has never
 * seen the demo seed should get a screenshot matrix rather than an error, and they will notice the
 * project is theirs.
 */
export async function projectId(page: Page, base: string): Promise<string | null> {
  await page.goto(`${base}/projects`, { waitUntil: 'networkidle' });

  // Matched exactly, not as a substring. Importing a project beside its original produces
  // "Catalog API (2)", which contains "Catalog API" and — being newer — sorts above it. A
  // substring match therefore quietly switches every driver to the copy, which has no runs and
  // nothing to photograph, and every page still renders so nothing looks wrong.
  const cards = page.locator('a.project-card');
  const count = await cards.count();

  for (let at = 0; at < count; at++) {
    const card = cards.nth(at);
    const name = (await card.locator('.semibold').first().innerText().catch(() => '')).trim();

    if (name === PROJECT_NAME) {
      return (await card.getAttribute('href'))?.split('/').pop() ?? null;
    }
  }

  const first = await cards.first().getAttribute('href').catch(() => null);

  return first?.split('/').pop() ?? null;
}
