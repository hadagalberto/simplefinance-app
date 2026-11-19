// The topbar fullscreen button enters and leaves browser fullscreen.
import { test, expect } from '../support/fixtures'

test('fullscreen button toggles document.fullscreenElement', async ({ appPage: page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Painel', exact: true })).toBeVisible()

  // The button only renders where the browser allows fullscreen at all.
  expect(await page.evaluate(() => document.fullscreenEnabled), 'document.fullscreenEnabled').toBe(true)

  await page.getByRole('button', { name: 'Tela cheia', exact: true }).click()
  await expect.poll(() => page.evaluate(() => document.fullscreenElement !== null)).toBe(true)
  await expect(page.getByRole('button', { name: 'Sair da tela cheia' })).toHaveAttribute('aria-pressed', 'true')

  await page.getByRole('button', { name: 'Sair da tela cheia' }).click()
  await expect.poll(() => page.evaluate(() => document.fullscreenElement === null)).toBe(true)
  await expect(page.getByRole('button', { name: 'Tela cheia', exact: true })).toHaveAttribute('aria-pressed', 'false')
})
