// Workflows that change data on the seeded stack: the transactions list (pager,
// search filter, detail sheet), the budget limit dialog (pt-BR validation
// message, save, success toast, two toasts stacked) and a savings goal created
// and then edited.
//
// Every visible string is read from the pt-BR locale files, so a renamed key or
// a changed message fails here instead of silently matching nothing.
import { readFileSync } from 'node:fs'
import type { Locator, Page } from '@playwright/test'

import { test, expect } from '../support/fixtures'

type Strings = Record<string, unknown>

/** A pt-BR string by namespace and dotted key, with {{name}} placeholders filled in. */
function pt(namespace: string, key: string, vars: Record<string, string | number> = {}): string {
  const file = JSON.parse(
    readFileSync(new URL(`../../src/i18n/locales/pt-BR/${namespace}.json`, import.meta.url), 'utf8'),
  ) as Strings
  const value = key.split('.').reduce<unknown>((node, part) => (node as Strings | undefined)?.[part], file)
  if (typeof value !== 'string' || value === '') throw new Error(`missing pt-BR string ${namespace}:${key}`)
  return value.replace(/\{\{(\w+)\}\}/g, (_, name: string) => String(vars[name] ?? `{{${name}}}`))
}

/** BRL as Intl formats it in pt-BR; the space after R$ is a no-break space, so match any blank. */
function brl(text: string): RegExp {
  return new RegExp(`R\\$\\s${text.replace(/\./g, '\\.')}`)
}

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

/** The text of the description cell (second column) of the first body row. */
async function firstRowDescription(page: Page): Promise<string> {
  return (await page.locator('tbody tr').first().locator('td').nth(1).innerText()).trim()
}

test.describe('transactions', () => {
  test('pager, search filter and detail sheet', async ({ appPage: page }) => {
    await page.goto('/transactions')
    const rows = page.locator('tbody tr')
    const summary = page.locator('.sf-pager > span')
    await expect(rows.filter({ hasText: /E2E / }).first()).toBeVisible()

    // First page: 50 rows and more than one page.
    const firstSummary = (await summary.innerText()).trim()
    const total = Number(/ de (\d+) /.exec(firstSummary)?.[1])
    expect(total, 'seeded transaction count').toBeGreaterThan(50)
    await expect(summary).toHaveText(pt('transactions', 'pagination.summary', { start: 1, end: 50, total, items: pt('transactions', 'pagination.itemsName') }))
    await expect(rows).toHaveCount(50)
    const pager = page.getByRole('navigation', { name: pt('transactions', 'pagination.label') })
    await expect(pager.getByRole('button', { name: '2', exact: true })).toBeVisible()

    // Next page: the range text and the first row change.
    const firstRowPage1 = await firstRowDescription(page)
    await pager.getByRole('button', { name: pt('transactions', 'pagination.next') }).click()
    await expect(summary).toHaveText(pt('transactions', 'pagination.summary', { start: 51, end: 100, total, items: pt('transactions', 'pagination.itemsName') }))
    await expect(pager.getByRole('button', { name: '2', exact: true })).toHaveAttribute('aria-current', 'page')
    await expect.poll(() => firstRowDescription(page)).not.toBe(firstRowPage1)

    // Search narrows the list to the six monthly salaries, and every row matches.
    const search = page.getByPlaceholder(pt('transactions', 'filters.searchPlaceholder'))
    await search.fill('E2E Salário')
    await expect(rows).toHaveCount(6)
    for (const description of await rows.locator('td:nth-child(2)').allInnerTexts()) {
      expect(description).toContain('E2E Salário')
    }
    await expect(summary).toHaveText(pt('transactions', 'pagination.summary', { start: 1, end: 6, total: 6, items: pt('transactions', 'pagination.itemsName') }))

    // Clearing restores the full list and empties the search box.
    await page.getByRole('button', { name: new RegExp(`^${escape(pt('transactions', 'filters.clearFilters'))}`) }).click()
    await expect(search).toHaveValue('')
    await expect(summary).toHaveText(pt('transactions', 'pagination.summary', { start: 1, end: 50, total, items: pt('transactions', 'pagination.itemsName') }))
    await expect(rows).toHaveCount(50)

    // A row click opens the detail sheet for that transaction; its close button closes it.
    const description = await firstRowDescription(page)
    await rows.first().click()
    const sheet = page.locator('.offcanvas.show')
    await expect(sheet).toBeVisible()
    await expect(sheet.getByText(pt('transactions', 'detail.title'), { exact: true })).toBeVisible()
    await expect(sheet.getByText(description, { exact: true })).toBeVisible()
    await sheet.locator('.offcanvas-header .btn-close').click()
    await expect(page.locator('.offcanvas')).toHaveCount(0)
  })
})

/** The add-a-budget picker below the table. */
function addBudgetSelect(page: Page): Locator {
  return page.locator('select').filter({ has: page.locator('option', { hasText: pt('budget', 'addCategory.placeholder') }) })
}

async function openSetLimitDialog(page: Page, category: string): Promise<Locator> {
  await addBudgetSelect(page).selectOption({ label: category })
  await page.getByRole('button', { name: pt('budget', 'setLimit'), exact: true }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByText(pt('budget', 'dialog.setTitle'), { exact: true })).toBeVisible()
  await expect(dialog.getByText(category, { exact: true })).toBeVisible()
  return dialog
}

/** Replaces the value of a number input by typing, the way a user would. */
async function typeInto(input: Locator, value: string) {
  await input.click()
  await input.press('ControlOrMeta+a')
  await input.press('Backspace')
  await input.pressSequentially(value)
}

function budgetRow(page: Page, category: string): Locator {
  return page.locator('tbody tr').filter({ has: page.getByText(category, { exact: true }) })
}

test.describe('budget', () => {
  test('limit dialog: pt-BR validation on 0, 1234,56 saved as R$ 1.234,56 with a toast, two toasts stacked', async ({ appPage: page }) => {
    await page.goto('/budget')
    await expect(budgetRow(page, 'Mercado')).toBeVisible()
    const toasts = page.locator('.toast')
    const savedText = pt('budget', 'saveSuccess')

    // 0 is refused on the client with the pt-BR message, and the dialog stays open.
    let dialog = await openSetLimitDialog(page, 'Farmácia')
    const amount = dialog.locator('input[inputmode="decimal"]')
    await typeInto(amount, '0')
    await dialog.getByRole('button', { name: pt('budget', 'dialog.save') }).click()
    await expect(dialog.getByText(pt('budget', 'dialog.amountPositiveError'), { exact: true })).toBeVisible()
    await expect(dialog).toBeVisible()
    await expect(toasts).toHaveCount(0)

    // Typed with the pt-BR decimal comma, as a user would, in a browser whose own
    // UI language is English (playwright.config.ts): the text is kept as typed
    // and the app parses it, so it saves 1234.56, not 123456.
    await typeInto(amount, '1234,56')
    expect(await amount.inputValue()).toBe('1234,56')
    await dialog.getByRole('button', { name: pt('budget', 'dialog.save') }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)
    await expect(toasts.filter({ hasText: savedText })).toHaveCount(1)
    await expect(budgetRow(page, 'Farmácia')).toContainText(brl('1.234,56'))

    // A second save while the first toast is still up: both toasts show at once.
    dialog = await openSetLimitDialog(page, 'Streaming')
    await typeInto(dialog.locator('input[inputmode="decimal"]'), '80')
    await dialog.getByRole('button', { name: pt('budget', 'dialog.save') }).click()
    await expect(toasts.filter({ hasText: savedText })).toHaveCount(2)
    await expect(budgetRow(page, 'Streaming')).toContainText(brl('80,00'))

    // Each toast closes by itself after its delay.
    await expect(toasts).toHaveCount(0, { timeout: 10_000 })
  })
})

/** The input under the label with this exact text inside the open dialog. */
function dialogField(dialog: Locator, label: string): Locator {
  return dialog.locator('.mb-3').filter({ has: dialog.page().locator('label', { hasText: new RegExp(`^${escape(label)}$`) }) }).locator('input').first()
}

function goalCard(page: Page, name: string): Locator {
  // The innermost card holding the name is the goal's own card.
  return page.locator('.card').filter({ has: page.getByText(name, { exact: true }) }).last()
}

test.describe('goals', () => {
  test('create a goal, then edit its target', async ({ appPage: page }) => {
    const name = 'E2E Reserva'
    await page.goto('/goals')
    await expect(page.getByRole('heading', { level: 4, name: pt('goals', 'pageTitle'), exact: true })).toBeVisible()

    await page.getByRole('button', { name: pt('goals', 'newGoal'), exact: true }).click()
    let dialog = page.getByRole('dialog')
    await expect(dialog.getByText(pt('goals', 'dialog.createDescription'), { exact: true })).toBeVisible()
    await dialogField(dialog, pt('goals', 'dialog.nameLabel')).fill(name)
    await dialogField(dialog, pt('goals', 'dialog.targetAmountLabel')).fill('5000')
    await dialog.getByRole('button', { name: pt('goals', 'dialog.createGoal'), exact: true }).click()

    await expect(page.getByRole('dialog')).toHaveCount(0)
    await expect(page.locator('.toast').filter({ hasText: pt('goals', 'toasts.goalCreated') })).toBeVisible()
    const card = goalCard(page, name)
    await expect(card).toContainText(brl('5.000,00'))

    await card.getByRole('button', { name: pt('goals', 'card.actionsAriaLabel') }).click()
    await page.getByRole('button', { name: pt('goals', 'card.edit'), exact: true }).click()
    dialog = page.getByRole('dialog')
    await expect(dialog.getByText(pt('goals', 'dialog.editDescription'), { exact: true })).toBeVisible()
    const target = dialogField(dialog, pt('goals', 'dialog.targetAmountLabel'))
    await expect(target).toHaveValue('5000')
    // Grouping dot plus decimal comma, as pt-BR writes it.
    await target.fill('7.500,50')
    await dialog.getByRole('button', { name: pt('goals', 'dialog.saveChanges'), exact: true }).click()

    await expect(page.getByRole('dialog')).toHaveCount(0)
    await expect(page.locator('.toast').filter({ hasText: pt('goals', 'toasts.goalUpdated') })).toBeVisible()
    await expect(goalCard(page, name)).toContainText(brl('7.500,50'))
    await expect(goalCard(page, name)).not.toContainText(brl('5.000,00'))
  })
})
