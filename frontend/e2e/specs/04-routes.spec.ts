// Route sweep: every signed-in screen renders its translated page header in
// pt-BR and in English, the key screens show their seeded content, nothing
// scrolls sideways on a 375 px phone (signed in, and /login signed out), the
// sidebar icons render from the bundle, and an unknown route shows the
// translated 404 with a working link back to the dashboard.
//
// Every expected string is read from the locale files, so a renamed key or a
// missing translation fails here. The fixtures abort and fail on any request to
// an Iconify API host, so each page in this file also proves the icons are offline.
import { readFileSync } from 'node:fs'
import type { Page } from '@playwright/test'

import { assertCleanConsole, guardConsole, settle, test, expect } from '../support/fixtures'

type Lang = 'pt-BR' | 'en'
type Strings = Record<string, unknown>

function localeFile(lang: Lang, namespace: string): Strings {
  return JSON.parse(readFileSync(new URL(`../../src/i18n/locales/${lang}/${namespace}.json`, import.meta.url), 'utf8')) as Strings
}

/** Looks up a dotted key ("items.accounts") in one namespace of one language. */
function text(lang: Lang, namespace: string, key: string): string {
  const value = key.split('.').reduce<unknown>((node, part) => (node as Strings | undefined)?.[part], localeFile(lang, namespace))
  if (typeof value !== 'string' || value === '') throw new Error(`missing ${lang} string ${namespace}:${key}`)
  return value
}

type RouteRow = {
  path: string
  /** Where the header lives: the app page header, or the title of the account-screen card. */
  header: 'page' | 'auth'
  namespace: string
  key: string
  /** Extra check on the seeded data or the main card (pt-BR pass only). */
  content?: (page: Page) => Promise<void>
}

const PAGE_TITLE = 'h1.sf-page-header__title'
const AUTH_TITLE = 'h1.sf-auth__title'

/** At least `min` table body rows that mention `textInRow`. */
async function expectRows(page: Page, textInRow: string | RegExp, min: number) {
  const rows = page.locator('tbody tr').filter({ hasText: textInRow })
  await expect.poll(() => rows.count(), { message: `rows with ${String(textInRow)}` }).toBeGreaterThanOrEqual(min)
}

/** The page's first card is visible below the header. */
async function expectMainCard(page: Page) {
  await expect(page.locator('.sf-page-header ~ * .card, .sf-page-header ~ .card').first()).toBeVisible()
}

const ROUTES: RouteRow[] = [
  { path: '/', header: 'page', namespace: 'menu', key: 'items.dashboard' },
  {
    path: '/transactions',
    header: 'page',
    namespace: 'menu',
    key: 'items.transactions',
    content: (page) => expectRows(page, /E2E /, 1),
  },
  {
    path: '/accounts',
    header: 'page',
    namespace: 'menu',
    key: 'items.accounts',
    content: async (page) => {
      // Both seeded accounts: the checking account and the credit card, by their masked numbers.
      await expect(page.getByText('0001-9', { exact: true }).first()).toBeVisible()
      await expect(page.getByText('**** 4242', { exact: true }).first()).toBeVisible()
    },
  },
  {
    path: '/budget',
    header: 'page',
    namespace: 'menu',
    key: 'items.budget',
    content: async (page) => {
      for (const category of ['Mercado', 'Restaurante e Bar', 'Combustível']) await expectRows(page, category, 1)
    },
  },
  {
    path: '/investments',
    header: 'page',
    namespace: 'menu',
    key: 'items.investments',
    content: (page) => expectRows(page, /E2E /, 3),
  },
  {
    path: '/net-worth',
    header: 'page',
    namespace: 'menu',
    key: 'items.netWorth',
    content: async (page) => {
      const drawn = page.locator('svg.apexcharts-svg .apexcharts-series path').filter({ visible: true })
      await expect
        .poll(() => drawn.evaluateAll((paths) => paths.filter((p) => (p.getAttribute('d') ?? '').length > 10).length), {
          message: 'net-worth chart: drawn series',
        })
        .toBeGreaterThanOrEqual(1)
    },
  },
  { path: '/recurring', header: 'page', namespace: 'menu', key: 'items.recurring' },
  { path: '/goals', header: 'page', namespace: 'menu', key: 'items.goals' },
  { path: '/reports', header: 'page', namespace: 'menu', key: 'items.reports' },
  {
    path: '/assistente',
    header: 'page',
    namespace: 'menu',
    key: 'items.assistant',
    content: async (page) => {
      // AI is off on the throwaway stack; the composer still renders (the server answers when a message is sent).
      await expect(page.getByPlaceholder(text('pt-BR', 'ai', 'assistant.placeholder'))).toBeVisible()
    },
  },
  { path: '/notifications', header: 'page', namespace: 'menu', key: 'items.notifications' },
  { path: '/settings/connections', header: 'page', namespace: 'menu', key: 'items.connections', content: expectMainCard },
  { path: '/settings/categories', header: 'page', namespace: 'menu', key: 'items.categories', content: expectMainCard },
  { path: '/settings/rules', header: 'page', namespace: 'menu', key: 'items.rules', content: expectMainCard },
  { path: '/settings/alerts', header: 'page', namespace: 'menu', key: 'items.alerts', content: expectMainCard },
  { path: '/settings/preferences', header: 'page', namespace: 'menu', key: 'items.preferences', content: expectMainCard },
  { path: '/settings/ai', header: 'page', namespace: 'menu', key: 'items.ai', content: expectMainCard },
  { path: '/settings/backup', header: 'page', namespace: 'menu', key: 'items.backup', content: expectMainCard },
  { path: '/settings/change-password', header: 'auth', namespace: 'auth', key: 'changePassword.pageTitle' },
  { path: '/settings/2fa/enable', header: 'auth', namespace: 'auth', key: 'enable2fa.pageTitle' },
]

const NOT_FOUND_PATH = '/rota-que-nao-existe'

/**
 * Opens a route and waits for its translated header. The header only shows once
 * the boot-time refresh has answered, so the next navigation never cuts a
 * refresh rotation short.
 */
async function openRoute(page: Page, route: RouteRow, lang: Lang) {
  await page.goto(route.path)
  const title = page.locator(route.header === 'page' ? PAGE_TITLE : AUTH_TITLE)
  await expect(title, `${route.path} header (${lang})`).toHaveText(text(lang, route.namespace, route.key))
}

/** Page-level horizontal overflow: the document is wider than the viewport. */
async function horizontalOverflow(page: Page): Promise<{ scrollWidth: number; innerWidth: number }> {
  return page.evaluate(() => ({ scrollWidth: document.documentElement.scrollWidth, innerWidth: window.innerWidth }))
}

async function expectNoHorizontalScroll(page: Page, label: string) {
  await expect
    .poll(
      async () => {
        const { scrollWidth, innerWidth } = await horizontalOverflow(page)
        return scrollWidth <= innerWidth ? 'fits' : `scrollWidth ${scrollWidth} > innerWidth ${innerWidth}`
      },
      { message: `${label}: page-level horizontal scroll at 375px` },
    )
    .toBe('fits')
}

async function restorePortuguese(page: Page) {
  await page.evaluate(() => localStorage.setItem('lang', 'pt-BR'))
}

test('every route renders its pt-BR header, dark, with seeded content on the key screens', async ({ appPage: page }) => {
  test.setTimeout(180_000)
  for (const route of ROUTES) {
    await openRoute(page, route, 'pt-BR')
    await expect(page.locator('html')).toHaveAttribute('data-bs-theme', 'dark')
    if (route.content) await route.content(page)
    await settle(page)
  }
})

test('the sidebar icons render from the bundled set', async ({ appPage: page }) => {
  await openRoute(page, ROUTES[0], 'pt-BR')
  const nav = page.getByRole('navigation', { name: text('pt-BR', 'shell', 'sidebar.navLabel') })
  const icons = nav.locator('.sf-nav__icon')
  const total = await icons.count()
  expect(total, 'sidebar entries with an icon').toBeGreaterThanOrEqual(8)
  // Every icon slot holds an svg with drawn shapes; lucide draws mostly with <path d>.
  await expect
    .poll(() => icons.evaluateAll((slots) => slots.filter((slot) => (slot.querySelector('svg')?.childElementCount ?? 0) > 0).length))
    .toBe(total)
  const withPath = await icons.evaluateAll(
    (slots) => slots.filter((slot) => [...slot.querySelectorAll('svg path')].some((p) => (p.getAttribute('d') ?? '') !== '')).length,
  )
  expect(withPath, 'sidebar icons with a non-empty path').toBeGreaterThanOrEqual(8)
  await settle(page)
})

test('every route renders its English header after switching the language', async ({ appPage: page }) => {
  test.setTimeout(180_000)
  try {
    await openRoute(page, ROUTES[0], 'pt-BR')
    await page.getByRole('button', { name: text('pt-BR', 'shell', 'language.label') }).click()
    await page.getByRole('button', { name: 'English' }).click()
    await expect(page.getByRole('navigation', { name: text('en', 'shell', 'sidebar.navLabel') })).toBeVisible()
    await settle(page)

    for (const route of ROUTES) {
      await openRoute(page, route, 'en')
      await settle(page)
    }

    await page.goto(NOT_FOUND_PATH)
    await expect(page.locator(AUTH_TITLE)).toHaveText(text('en', 'shell', 'notFound.title'))
    await settle(page)
  } finally {
    await restorePortuguese(page)
  }
})

test('no route scrolls sideways at 375x812', async ({ appPage: page }) => {
  test.setTimeout(180_000)
  const original = page.viewportSize()
  await page.setViewportSize({ width: 375, height: 812 })
  try {
    for (const route of ROUTES) {
      await openRoute(page, route, 'pt-BR')
      await settle(page)
      await expectNoHorizontalScroll(page, route.path)
    }
  } finally {
    if (original) await page.setViewportSize(original)
  }
})

test('signed out, /login fits 375px and is dark', async ({ browser }, testInfo) => {
  // A fresh context with no cookie: it never presents the shared context's refresh token.
  const { baseURL, locale } = testInfo.project.use
  const context = await browser.newContext({ baseURL, locale, viewport: { width: 375, height: 812 } })
  try {
    const page = await context.newPage()
    const guard = await guardConsole(page)
    await page.goto('/login')
    await expect(page.locator(AUTH_TITLE)).toHaveText(text('pt-BR', 'auth', 'login.title'))
    await expect(page.locator('html')).toHaveAttribute('data-bs-theme', 'dark')
    await settle(page)
    await expectNoHorizontalScroll(page, '/login (signed out)')
    await page.close()
    assertCleanConsole(guard, testInfo)
  } finally {
    await context.close()
  }
})

test('an unknown route shows the translated 404 and links back to the dashboard', async ({ appPage: page }) => {
  await page.goto(NOT_FOUND_PATH)
  await expect(page.locator(AUTH_TITLE)).toHaveText(text('pt-BR', 'shell', 'notFound.title'))
  await expect(page.getByText(text('pt-BR', 'shell', 'notFound.description'))).toBeVisible()
  await page.getByRole('link', { name: text('pt-BR', 'shell', 'notFound.back') }).click()
  await page.waitForURL((url) => url.pathname === '/')
  await expect(page.locator(PAGE_TITLE)).toHaveText(text('pt-BR', 'menu', 'items.dashboard'))
  await settle(page)
})
