// Browser tab title format for every screen.
//
// This module has no imports, so the node:test suite in
// scripts/shell-logic.test.mjs loads it directly.

/** Product name shown in the tab title and the page header breadcrumb. */
export const APP_NAME = 'SimpleFinance'

/**
 * Builds the tab title: "<page> · SimpleFinance".
 * A missing or blank page name gives the product name alone.
 */
export function formatPageTitle(title?: string): string {
  const page = (title ?? '').trim()
  return page ? `${page} · ${APP_NAME}` : APP_NAME
}
