// Project `setup`: first-run account creation through the real /setup page,
// with the identity global-setup generated for this run, then the synthetic
// data load (e2e/seed.sql) so the app specs have charts with series.
import { test as setup, expect } from '@playwright/test'

import { stackRun, stackSql } from './support/db'
import { assertCleanConsole, guardConsole } from './support/fixtures'

setup('create the first user through /setup', async ({ page }, testInfo) => {
  const name = process.env.E2E_NAME
  const email = process.env.E2E_EMAIL
  const password = process.env.E2E_PASSWORD
  if (!name || !email || !password) throw new Error('E2E identity is not set; global setup did not run')

  const guard = await guardConsole(page)
  await page.goto('/setup')
  await expect(page.getByRole('heading', { name: 'Criar sua conta' })).toBeVisible()
  await page.locator('input[autocomplete="name"]').fill(name)
  await page.locator('input[type="email"]').fill(email)
  await page.locator('input[type="password"]').fill(password)
  await page.getByRole('button', { name: 'Criar conta' }).click()
  await page.waitForURL((url) => url.pathname === '/login')
  await expect(page.getByRole('heading', { name: 'Entrar' })).toBeVisible()
  assertCleanConsole(guard, testInfo)

  // The seed needs the user (and the categories the API creates with it). This runs
  // `node e2e/stack.mjs seed`, which pipes e2e/seed.sql into psql in the throwaway postgres container.
  stackRun('seed')
  expect(Number(stackSql('select count(*) from "Transactions"'))).toBeGreaterThanOrEqual(120)
  expect(Number(stackSql('select count(*) from "NetWorthSnapshots"'))).toBeGreaterThanOrEqual(6)
})
