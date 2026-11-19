// Shared fixtures for the app specs.
//
// authedContext (worker scope): one browser context per worker, signed in once
// through the real /login page. The API rotates the refresh cookie on every
// refresh and revokes the whole token family when a rotated token is presented
// again, so a saved storageState reused by several contexts would end every
// session after the first reload. Sharing one live context keeps exactly one
// cookie jar that always holds the newest token.
//
// appPage (test scope): a new page in that context with a console guard.
// Never keep two pages of the context loading at the same time; each page boot
// calls /api/auth/refresh.
import { test as base, expect, type BrowserContext, type ConsoleMessage, type Page, type TestInfo } from '@playwright/test'

/** Console errors the app is expected to log; matched on the URL the browser reports for the message. */
const ALLOWED_CONSOLE_ERRORS: { url: RegExp; status: number }[] = [
  // Signed out, the boot-time refresh answers 401.
  { url: /\/api\/auth\/refresh(\?|$)/, status: 401 },
  // AI features are off on the throwaway stack; these empty-state endpoints answer 404.
  { url: /\/api\/ai\/forecast(\?|$)/, status: 404 },
  { url: /\/api\/ai\/summary(\/|\?|$)/, status: 404 },
]

const FIRST_PARTY_HOSTS = new Set(['localhost:5173', 'localhost:5080'])

/**
 * Public Iconify API hosts. Icons ship in the bundle, so any request to one of
 * these is a regression: it is aborted and fails the test. Other third-party
 * hosts (the web-font CDN in index.html) stay informational.
 */
const ICON_API_HOSTS = ['api.iconify.design', 'api.simplesvg.com', 'api.unisvg.com']
const isIconApiRequest = (url: URL): boolean => ICON_API_HOSTS.includes(url.hostname)

export interface ConsoleEntry {
  text: string
  url: string
}

function isAllowed(entry: ConsoleEntry): boolean {
  return ALLOWED_CONSOLE_ERRORS.some(
    (rule) => rule.url.test(entry.url) && new RegExp(`\\b${rule.status}\\b`).test(entry.text),
  )
}

export interface ConsoleGuard {
  /** Console errors outside the allowlist, uncaught page errors and any Iconify API request. */
  unexpected(): string[]
  /** Hosts other than the app and its API that the page requested. */
  thirdPartyHosts(): string[]
}

/**
 * Starts collecting console errors, page errors and third-party requests for one
 * page, and aborts every request to an Iconify API host. Await it before the
 * first navigation so the route is in place.
 */
export async function guardConsole(page: Page): Promise<ConsoleGuard> {
  const consoleErrors: ConsoleEntry[] = []
  const pageErrors: string[] = []
  const thirdParty = new Set<string>()
  const iconApiRequests: string[] = []

  await page.route(isIconApiRequest, (route) => {
    iconApiRequests.push(route.request().url())
    return route.abort()
  })

  page.on('console', (message: ConsoleMessage) => {
    if (message.type() === 'error') consoleErrors.push({ text: message.text(), url: message.location().url })
  })
  page.on('pageerror', (error) => pageErrors.push(error.message))
  page.on('request', (request) => {
    const host = new URL(request.url()).host
    if (host && !FIRST_PARTY_HOSTS.has(host)) thirdParty.add(host)
  })

  return {
    unexpected: () => [
      ...consoleErrors.filter((entry) => !isAllowed(entry)).map((entry) => `console: ${entry.text} @ ${entry.url || '(no url)'}`),
      ...pageErrors.map((message) => `pageerror: ${message}`),
      ...iconApiRequests.map((url) => `icon API request (icons must come from the bundle): ${url}`),
    ],
    thirdPartyHosts: () => [...thirdParty].sort(),
  }
}

/** Fails the test when the guard collected anything unexpected; records third-party hosts as an annotation. */
export function assertCleanConsole(guard: ConsoleGuard, testInfo: TestInfo): void {
  const hosts = guard.thirdPartyHosts()
  if (hosts.length) testInfo.annotations.push({ type: 'third-party-hosts', description: hosts.join(', ') })
  const unexpected = guard.unexpected()
  expect(unexpected, `unexpected console/page errors:\n${unexpected.join('\n')}`).toEqual([])
}

function credentials() {
  const email = process.env.E2E_EMAIL
  const password = process.env.E2E_PASSWORD
  if (!email || !password) throw new Error('E2E_EMAIL/E2E_PASSWORD are not set; global setup did not run')
  return { email, password }
}

/** Signs in through the /login page and waits until the dashboard has loaded. */
export async function signInThroughUi(page: Page): Promise<void> {
  const { email, password } = credentials()
  await page.goto('/login')
  await page.locator('input[type="email"]').fill(email)
  await page.locator('input[type="password"]').fill(password)
  await page.getByRole('button', { name: 'Entrar' }).click()
  await page.waitForURL((url) => url.pathname === '/')
  await expect(page.getByRole('heading', { name: 'Painel', exact: true })).toBeVisible()
}

/** Waits until the page has no request in flight, so a refresh rotation is never cut off by closing it. */
export async function settle(page: Page): Promise<void> {
  await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {})
}

type WorkerFixtures = { authedContext: BrowserContext }
type TestFixtures = { appPage: Page }

export const test = base.extend<TestFixtures, WorkerFixtures>({
  authedContext: [
    async ({ browser }, use, workerInfo) => {
      // Worker fixtures cannot use the test-scoped baseURL/locale/viewport fixtures, so read the project's settings.
      const { baseURL, locale, viewport } = workerInfo.project.use
      const context = await browser.newContext({ baseURL, locale, viewport })
      const loginPage = await context.newPage()
      const guard = await guardConsole(loginPage)
      await signInThroughUi(loginPage)
      await settle(loginPage)
      await loginPage.close()
      const unexpected = guard.unexpected()
      if (unexpected.length) throw new Error(`unexpected console/page errors during sign-in:\n${unexpected.join('\n')}`)
      await use(context)
      await context.close()
    },
    { scope: 'worker' },
  ],

  appPage: async ({ authedContext }, use, testInfo) => {
    const page = await authedContext.newPage()
    const guard = await guardConsole(page)
    await use(page)
    await settle(page)
    await page.close()
    assertCleanConsole(guard, testInfo)
  },
})

export { expect }
