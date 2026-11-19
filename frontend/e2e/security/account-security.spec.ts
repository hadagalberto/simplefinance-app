// Account security on the only user, end to end in a real browser: the
// password-strength meter, change password (wrong current password refused,
// old password stops working, new one signs in), two-factor activation with a
// TOTP computed here (secret read from the manual-entry field, code pasted into
// the OTP boxes, 8 recovery codes), and sign-in through /2fa/verify (a wrong
// code refused, a fresh code reaching the dashboard).
//
// These steps change the account, so they run in their own project after the
// app specs, serially, on one browser context and one page. That keeps the
// login count at exactly 4 (first sign-in, old-password failure, new-password
// sign-in, 2FA sign-in) under the API's 10 per minute limit.
//
// Never log or annotate a password, the TOTP secret, a code or a recovery code.
import { randomBytes } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { test, expect, type BrowserContext, type Page, type TestInfo } from '@playwright/test'

import { guardConsole, type ConsoleGuard } from '../support/fixtures'
import { msUntilNextStep, stepOf, totp } from '../support/totp'

test.describe.configure({ mode: 'serial' })

type Strings = Record<string, unknown>

function pt(namespace: string, key: string): string {
  const file = JSON.parse(
    readFileSync(new URL(`../../src/i18n/locales/pt-BR/${namespace}.json`, import.meta.url), 'utf8'),
  ) as Strings
  const value = key.split('.').reduce<unknown>((node, part) => (node as Strings | undefined)?.[part], file)
  if (typeof value !== 'string' || value === '') throw new Error(`missing pt-BR string ${namespace}:${key}`)
  return value
}

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

/** A request this spec makes fail on purpose; the browser logs it as a console error. */
type Provoked = { url: RegExp; status: number }

const LOGIN_401: Provoked = { url: /\/api\/auth\/login$/, status: 401 }
const CHANGE_PASSWORD_400: Provoked = { url: /\/api\/auth\/change-password$/, status: 400 }
const VERIFY_401: Provoked = { url: /\/api\/auth\/2fa\/verify$/, status: 401 }

let context: BrowserContext
let page: Page
let guard: ConsoleGuard
/** How many guard entries earlier tests already judged. */
let judged = 0

const email = () => {
  const value = process.env.E2E_EMAIL
  if (!value) throw new Error('E2E_EMAIL is not set; global setup did not run')
  return value
}

/** The password that currently signs in. Starts as the one global setup generated. */
let currentPassword = process.env.E2E_PASSWORD ?? ''
/** Base32 TOTP secret, set once two-factor is activated. */
let totpSecret = ''
/** The 30 s step in which the activation code was accepted. */
let activationStep = -1

/**
 * Fails when the page logged anything since the previous call other than the
 * failures this test provoked on purpose (matched on request URL and status).
 */
function expectOnlyProvoked(testInfo: TestInfo, provoked: Provoked[]) {
  const all = guard.unexpected()
  const fresh = all.slice(judged)
  judged = all.length
  const stray = fresh.filter((entry) => {
    const at = entry.lastIndexOf(' @ ')
    if (!entry.startsWith('console: ') || at === -1) return true
    const url = entry.slice(at + 3)
    return !provoked.some((rule) => rule.url.test(url) && new RegExp(`\\b${rule.status}\\b`).test(entry.slice(0, at)))
  })
  if (fresh.length > stray.length) {
    testInfo.annotations.push({ type: 'provoked-errors', description: `${fresh.length - stray.length} expected failure(s) logged` })
  }
  expect(stray, `unexpected console/page errors:\n${stray.join('\n')}`).toEqual([])
}

/** A fresh strong password: upper, lower, digit and symbol, 32+ characters. */
function newStrongPassword(): string {
  return `Sf-${randomBytes(18).toString('base64url')}a9Z!`
}

async function submitLogin(password: string) {
  await page.goto('/login')
  await page.locator('input[type="email"]').fill(email())
  await page.locator('input[type="password"]').fill(password)
  await page.getByRole('button', { name: pt('auth', 'login.submit'), exact: true }).click()
}

async function expectDashboard() {
  await page.waitForURL((url) => url.pathname === '/')
  await expect(page.getByRole('heading', { name: 'Painel', exact: true })).toBeVisible()
}

/** Writes `code` into the OTP boxes the way a clipboard paste does, starting at the first box. */
async function pasteCode(code: string) {
  const first = otpBoxes().first()
  await first.focus()
  await first.evaluate((element, text) => {
    const data = new DataTransfer()
    data.setData('text/plain', text)
    element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }))
  }, code)
}

function otpBoxes() {
  return page.locator('.sf-otp input')
}

/** True when the six boxes hold exactly `code`. Compared here so a failure never prints the code. */
async function boxesHold(code: string): Promise<boolean> {
  const values = await otpBoxes().evaluateAll((inputs) => inputs.map((input) => (input as HTMLInputElement).value))
  return values.length === 6 && values.join('') === code
}

/** Waits out the rest of the current step when fewer than `marginMs` remain, so a code is not about to expire. */
async function awayFromStepEdge(marginMs = 3_000) {
  const left = msUntilNextStep()
  if (left < marginMs) await page.waitForTimeout(left + 250)
}

test.beforeAll(async ({ browser }, testInfo) => {
  if (!currentPassword) throw new Error('E2E_PASSWORD is not set; global setup did not run')
  const { baseURL, locale, viewport } = testInfo.project.use
  context = await browser.newContext({ baseURL, locale, viewport })
  page = await context.newPage()
  guard = await guardConsole(page)
})

test.afterAll(async () => {
  await page?.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {})
  await context?.close()
})

test('sign in with the generated password', async () => {
  const testInfo = test.info()
  await submitLogin(currentPassword)
  await expectDashboard()
  expectOnlyProvoked(testInfo, [])
})

test('change password: meter, wrong current refused, old fails, new signs in', async () => {
  const testInfo = test.info()
  await page.goto('/settings/change-password')
  await expect(page.locator('h1.sf-auth__title')).toHaveText(pt('auth', 'changePassword.pageTitle'))

  const current = page.locator('input[autocomplete="current-password"]')
  const fresh = page.locator('input[name="newPassword"]')
  const confirm = page
    .locator('.mb-3')
    .filter({ has: page.locator('label', { hasText: new RegExp(`^${escape(pt('auth', 'changePassword.confirmNewPasswordLabel'))}`) }) })
    .locator('input')
  const meter = page.getByRole('progressbar', { name: pt('auth', 'passwordStrength.label') })
  const submit = page.getByRole('button', { name: pt('auth', 'changePassword.submit'), exact: true })

  // The meter follows what is typed: weak, then strong.
  await fresh.fill('abcdef')
  await expect(meter).toHaveAttribute('aria-valuetext', pt('auth', 'passwordStrength.levels.weak'))
  const weakScore = Number(await meter.getAttribute('aria-valuenow'))
  await expect(meter.locator('.sf-strength__bar.is-on')).toHaveCount(weakScore)

  const nextPassword = newStrongPassword()
  await fresh.fill(nextPassword)
  await expect(meter).toHaveAttribute('aria-valuetext', pt('auth', 'passwordStrength.levels.strong'))
  await expect(meter).toHaveAttribute('aria-valuenow', '4')
  await expect(meter.locator('.sf-strength__bar.is-on')).toHaveCount(4)
  expect(weakScore).toBeLessThan(4)
  await confirm.fill(nextPassword)

  // A wrong current password is refused with the pt-BR message, and nothing changes.
  await current.fill(`wrong-${randomBytes(6).toString('hex')}`)
  await submit.click()
  await expect(page.getByText(pt('auth', 'changePassword.invalidCurrentPassword'), { exact: true })).toBeVisible()
  await expect(page).toHaveURL(/\/settings\/change-password$/)

  // The right one changes it; the page signs out and goes to /login.
  await current.fill(currentPassword)
  await submit.click()
  await page.waitForURL((url) => url.pathname === '/login')
  const oldPassword = currentPassword
  currentPassword = nextPassword

  // The old password is refused...
  await submitLogin(oldPassword)
  await expect(page.getByText(pt('auth', 'login.invalidCredentials'), { exact: true })).toBeVisible()
  await expect(page).toHaveURL(/\/login$/)

  // ...and the new one signs in.
  await submitLogin(currentPassword)
  await expectDashboard()
  expectOnlyProvoked(testInfo, [CHANGE_PASSWORD_400, LOGIN_401])
})

test('enable two-factor with a computed TOTP and see 8 recovery codes', async () => {
  const testInfo = test.info()
  await page.goto('/settings/2fa/enable')
  await expect(page.locator('h1.sf-auth__title')).toHaveText(pt('auth', 'enable2fa.pageTitle'))

  const secretField = page.locator('#totp-secret')
  await expect(secretField).toHaveValue(/^[A-Z2-7]{16,}=*$/)
  totpSecret = await secretField.inputValue()
  await page.getByRole('button', { name: pt('auth', 'enable2fa.qr.continue'), exact: true }).click()
  await expect(otpBoxes()).toHaveCount(6)

  // Paste the code into the first box: all six boxes fill.
  await awayFromStepEdge()
  const code = totp(totpSecret)
  await pasteCode(code)
  await expect.poll(() => boxesHold(code), { message: 'pasted digits fill all six boxes' }).toBe(true)

  activationStep = stepOf()
  await page.getByRole('button', { name: pt('auth', 'enable2fa.verify.submit'), exact: true }).click()

  // Eight recovery codes, shown once; confirming them goes to the dashboard.
  await expect(page.getByText(pt('auth', 'enable2fa.recoveryCodes.label'), { exact: true })).toBeVisible()
  await expect(page.locator('.font-monospace > .col')).toHaveCount(8)
  const finish = page.getByRole('button', { name: pt('auth', 'enable2fa.recoveryCodes.continue'), exact: true })
  await expect(finish).toBeDisabled()
  await page.getByLabel(pt('auth', 'enable2fa.recoveryCodes.savedCheckbox')).check()
  await finish.click()
  await expectDashboard()
  expectOnlyProvoked(testInfo, [])
})

test('sign-in now goes through /2fa/verify: wrong code refused, fresh code reaches the dashboard', async () => {
  const testInfo = test.info()
  test.setTimeout(120_000)
  expect(totpSecret, 'two-factor was activated by the previous test').not.toBe('')

  // Sign out through the user menu.
  await page.getByRole('button', { name: pt('shell', 'userDropdown.account') }).click()
  await page.getByRole('button', { name: pt('shell', 'userDropdown.logOut') }).click()
  await page.waitForURL((url) => url.pathname === '/login')

  // The password alone no longer reaches the app.
  await submitLogin(currentPassword)
  await page.waitForURL((url) => url.pathname === '/2fa/verify')
  await expect(page.locator('h1.sf-auth__title')).toHaveText(pt('auth', 'twofa.verify.title'))
  await expect(otpBoxes()).toHaveCount(6)

  const verify = page.getByRole('button', { name: pt('auth', 'twofa.verify.submit'), exact: true })

  // A wrong code is refused with the pt-BR message.
  await otpBoxes().first().click()
  await page.keyboard.type('000000')
  await expect.poll(() => boxesHold('000000')).toBe(true)
  await verify.click()
  await expect(page.getByText(pt('auth', 'twofa.verify.invalidCode'), { exact: true })).toBeVisible()
  await expect(page).toHaveURL(/\/2fa\/verify$/)

  // A fresh code, from a later step than the activation code, signs in.
  if (stepOf() === activationStep) await page.waitForTimeout(msUntilNextStep() + 250)
  await awayFromStepEdge()
  const code = totp(totpSecret)
  await pasteCode(code)
  await expect.poll(() => boxesHold(code), { message: 'fresh code fills all six boxes' }).toBe(true)
  await verify.click()
  await expectDashboard()
  expectOnlyProvoked(testInfo, [VERIFY_401])
})
