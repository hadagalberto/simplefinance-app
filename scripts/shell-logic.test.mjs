// Tests for the app shell's pure logic — run with: node --test scripts/shell-logic.test.mjs
//
// frontend/src/shell/navMatch.ts, frontend/src/shell/theme/themeLogic.ts and
// frontend/src/shell/pageTitle.ts have no imports, so Node loads them directly and
// strips their TypeScript types. frontend/package.json declares "type": "module",
// so Node treats them as ES modules without a MODULE_TYPELESS_PACKAGE_JSON
// warning.

import test from 'node:test'
import assert from 'node:assert/strict'

import { isAnyPathActive, isPathActive, parseCollapsedFlag, SIDEBAR_COLLAPSED_KEY } from '../frontend/src/shell/navMatch.ts'
import { DEFAULT_THEME, parseThemePreference, resolveTheme, THEME_STORAGE_KEY } from '../frontend/src/shell/theme/themeLogic.ts'
import { APP_NAME, formatPageTitle } from '../frontend/src/shell/pageTitle.ts'

test('isPathActive: root matches only root', () => {
  assert.equal(isPathActive('/', '/'), true)
  assert.equal(isPathActive('/transactions', '/'), false)
  assert.equal(isPathActive('/settings/rules', '/'), false)
})

test('isPathActive: a target matches itself and its sub-paths, not a longer sibling', () => {
  assert.equal(isPathActive('/transactions', '/transactions'), true)
  assert.equal(isPathActive('/transactions/abc', '/transactions'), true)
  assert.equal(isPathActive('/transactions-x', '/transactions'), false)
})

test('isPathActive: ignores one trailing slash and respects segment boundaries', () => {
  assert.equal(isPathActive('/budget/', '/budget'), true)
  assert.equal(isPathActive('/settings/rules-x', '/settings/rules'), false)
})

test('isAnyPathActive: true when any target is active', () => {
  assert.equal(isAnyPathActive('/settings/ai', ['/settings/connections', '/settings/ai']), true)
  assert.equal(isAnyPathActive('/goals', ['/settings/ai']), false)
})

test('parseCollapsedFlag: only the exact stored value "1" means collapsed', () => {
  assert.equal(parseCollapsedFlag('1'), true)
  assert.equal(parseCollapsedFlag('0'), false)
  assert.equal(parseCollapsedFlag(null), false)
  assert.equal(parseCollapsedFlag('yes'), false)
  assert.equal(parseCollapsedFlag(''), false)
})

test('SIDEBAR_COLLAPSED_KEY: is the shared storage key', () => {
  assert.equal(SIDEBAR_COLLAPSED_KEY, 'sf.sidebar.collapsed')
})

// Theme preference logic (frontend/src/shell/theme/themeLogic.ts, also import-free).

test('parseThemePreference: accepts the three exact preference values', () => {
  assert.equal(parseThemePreference('light'), 'light')
  assert.equal(parseThemePreference('dark'), 'dark')
  assert.equal(parseThemePreference('system'), 'system')
})

test('parseThemePreference: unknown values fall back to dark', () => {
  assert.equal(parseThemePreference(null), 'dark')
  assert.equal(parseThemePreference(''), 'dark')
  assert.equal(parseThemePreference('blue'), 'dark')
  assert.equal(parseThemePreference('LIGHT'), 'dark')
})

test('resolveTheme: an explicit preference ignores the OS setting', () => {
  assert.equal(resolveTheme('light', true), 'light')
  assert.equal(resolveTheme('dark', false), 'dark')
})

test('resolveTheme: system follows the OS setting', () => {
  assert.equal(resolveTheme('system', true), 'dark')
  assert.equal(resolveTheme('system', false), 'light')
})

test('THEME_STORAGE_KEY and DEFAULT_THEME: storage key is sf.theme and the default is dark', () => {
  assert.equal(THEME_STORAGE_KEY, 'sf.theme')
  assert.equal(DEFAULT_THEME, 'dark')
})

// Browser tab title format (frontend/src/shell/pageTitle.ts, also import-free).

test('formatPageTitle: a page name comes before the app name, joined by a spaced middle dot', () => {
  assert.equal(formatPageTitle('Transações'), 'Transações · SimpleFinance')
})

test('formatPageTitle: a missing, empty or blank title shows the app name alone', () => {
  assert.equal(formatPageTitle(undefined), 'SimpleFinance')
  assert.equal(formatPageTitle(), 'SimpleFinance')
  assert.equal(formatPageTitle(''), 'SimpleFinance')
  assert.equal(formatPageTitle('   '), 'SimpleFinance')
})

test('formatPageTitle: surrounding whitespace is trimmed from the page name', () => {
  assert.equal(formatPageTitle('  Metas  '), 'Metas · SimpleFinance')
})

test('APP_NAME: is SimpleFinance', () => {
  assert.equal(APP_NAME, 'SimpleFinance')
})
