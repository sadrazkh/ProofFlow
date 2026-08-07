import { defineConfig, devices } from '@playwright/test';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';

const STATE_FILE = resolve(import.meta.dirname, 'e2e/.auth/state.json');

/**
 * Browser tests run against an application that is already listening.
 *
 * `webServer` deliberately unset: the .NET host needs a built frontend bundle and a migrated
 * database before it can serve anything, and having Playwright start it would hide a failure in
 * either of those as a browser timeout. CI starts it explicitly and waits on /healthz, so a
 * startup problem reports itself as a startup problem.
 */
export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.{spec,setup}.ts',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['list']] : [['list']],
  use: {
    baseURL: process.env.PROOFFLOW_URL ?? 'http://localhost:5290',
    trace: 'on-first-retry',
  },
  projects: [
    // One sign-in for the whole run, because the application rate limits that endpoint and a
    // suite that trips it silently audits the sign-in page under every other page's name.
    { name: 'setup', testMatch: /auth\.setup\.ts/ },
    {
      name: 'chromium',
      dependencies: ['setup'],
      testMatch: /.*\.spec\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        // Absent when no password was supplied — the authenticated tests skip themselves in that
        // case, and pointing at a missing file would fail the run instead.
        ...(existsSync(STATE_FILE) ? { storageState: STATE_FILE } : {}),
      },
    },
  ],
});
