// Playwright end-to-end suite for the web client.
//
// The suite runs against the throwaway Docker stack started by `e2e/stack.mjs`
// (front on :5173, api on :5080). `npm run e2e` resets that stack first; see
// e2e/stack.mjs for the safety rules around it.
//
// One worker, no retries, no parallelism: the API rotates the refresh cookie on
// every refresh and revokes the whole token family when an old one comes back,
// and it rate-limits logins to 10 per minute. The suite therefore signs in once
// per worker and shares that browser context (see e2e/support/fixtures.ts).
//
// Reports and traces go to `.e2e-out/` at the repo root (gitignored), never
// under frontend/: traces can hold typed values such as the test password.
import path from 'node:path'
import { fileURLToPath } from 'node:url'

import { defineConfig } from '@playwright/test'

const here = path.dirname(fileURLToPath(import.meta.url))
const outRoot = path.resolve(here, '..', '.e2e-out')

export default defineConfig({
  testDir: 'e2e',
  globalSetup: './e2e/global-setup.ts',
  outputDir: path.join(outRoot, 'test-results'),
  workers: 1,
  fullyParallel: false,
  retries: 0,
  forbidOnly: true,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  reporter: [['list'], ['html', { outputFolder: path.join(outRoot, 'report'), open: 'never' }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:5173',
    locale: 'pt-BR',
    // The page runs in pt-BR (locale above) inside an English Chromium UI on
    // purpose: amount fields are text inputs parsed by the app, so a typed
    // "1234,56" must survive a browser whose own language uses a decimal point.
    // (Native number inputs dropped the comma here and saved 123456.)
    launchOptions: { args: ['--lang=en-US'] },
    viewport: { width: 1366, height: 900 },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'setup', testDir: './e2e', testMatch: 'auth.setup.ts' },
    { name: 'app', testDir: './e2e/specs', dependencies: ['setup'] },
    // Changes the password and turns on 2FA for the only user, so it runs last,
    // after every app spec, on its own browser context (e2e/security/).
    { name: 'security', testDir: './e2e/security', dependencies: ['app'] },
  ],
})
