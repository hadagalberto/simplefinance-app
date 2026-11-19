// Theme and language switches from the topbar: live effect, persistence across
// a reload, and a clean restore to dark + pt-BR so no later spec inherits them.
import type { Page } from '@playwright/test'

import { test, expect } from '../support/fixtures'

const DASHBOARD_PT = 'Painel'
const DASHBOARD_EN = 'Dashboard'

/** Restores the defaults the other specs expect. localStorage is shared by every page of the context. */
async function restoreDefaults(page: Page) {
  await page.evaluate(() => {
    localStorage.setItem('sf.theme', 'dark')
    localStorage.setItem('lang', 'pt-BR')
  })
}

/** Fill color of the first axis label of the income-vs-expenses chart, as the browser computes it. */
async function axisLabelFill(page: Page): Promise<string> {
  const label = page
    .locator('.card')
    .filter({ has: page.getByRole('heading', { name: 'Receitas x despesas' }) })
    .locator('.apexcharts-xaxis-label')
    .first()
  await expect(label).toBeAttached()
  return label.evaluate((element) => getComputedStyle(element).fill)
}

test('switching to light recolors the charts without a reload and persists', async ({ appPage: page }) => {
  try {
    await page.goto('/')
    await expect(page.getByRole('heading', { name: DASHBOARD_PT, exact: true })).toBeVisible()
    await expect(page.locator('html')).toHaveAttribute('data-bs-theme', 'dark')

    const darkFill = await axisLabelFill(page)
    await page.evaluate(() => {
      ;(window as unknown as { __e2eNoReload?: boolean }).__e2eNoReload = true
    })

    await page.getByRole('button', { name: 'Tema' }).click()
    await page.getByRole('button', { name: 'Claro' }).click()

    await expect(page.locator('html')).toHaveAttribute('data-bs-theme', 'light')
    await expect.poll(() => axisLabelFill(page)).not.toBe(darkFill)
    expect(await page.evaluate(() => (window as unknown as { __e2eNoReload?: boolean }).__e2eNoReload)).toBe(true)

    await page.reload()
    await expect(page.getByRole('heading', { name: DASHBOARD_PT, exact: true })).toBeVisible()
    await expect(page.locator('html')).toHaveAttribute('data-bs-theme', 'light')
    expect(await page.evaluate(() => localStorage.getItem('sf.theme'))).toBe('light')

    await page.getByRole('button', { name: 'Tema' }).click()
    await page.getByRole('button', { name: 'Escuro' }).click()
    await expect(page.locator('html')).toHaveAttribute('data-bs-theme', 'dark')
    await expect.poll(() => axisLabelFill(page)).toBe(darkFill)
  } finally {
    await restoreDefaults(page)
  }
})

test('switching to English translates the chrome and persists', async ({ appPage: page }) => {
  try {
    await page.goto('/')
    await expect(page.getByRole('heading', { name: DASHBOARD_PT, exact: true })).toBeVisible()
    const nav = page.getByRole('navigation', { name: 'Navegação principal' })
    await expect(nav.getByRole('link', { name: 'Transações' })).toBeVisible()

    await page.getByRole('button', { name: 'Idioma' }).click()
    await page.getByRole('button', { name: 'English' }).click()

    const navEn = page.getByRole('navigation', { name: 'Main navigation' })
    await expect(page.getByRole('heading', { level: 4, name: DASHBOARD_EN, exact: true })).toBeVisible()
    await expect(navEn.getByRole('link', { name: 'Transactions' })).toBeVisible()
    await expect(navEn.getByRole('link', { name: 'Accounts & Cards' })).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Income vs. expenses' })).toBeVisible()

    await page.reload()
    await expect(page.getByRole('heading', { level: 4, name: DASHBOARD_EN, exact: true })).toBeVisible()
    await expect(navEn.getByRole('link', { name: 'Transactions' })).toBeVisible()
    expect(await page.evaluate(() => localStorage.getItem('lang'))).toBe('en')

    await page.getByRole('button', { name: 'Language' }).click()
    await page.getByRole('button', { name: 'Português' }).click()
    await expect(page.getByRole('heading', { name: DASHBOARD_PT, exact: true })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Transações' })).toBeVisible()
  } finally {
    await restoreDefaults(page)
  }
})
