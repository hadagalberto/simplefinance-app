// Active-route matching for the SimpleFinance sidebar.
//
// This module deliberately has no imports: scripts/shell-logic.test.mjs loads
// it straight into Node's test runner (which strips the TypeScript types), so
// it must not pull in React, the router or any path alias.

function stripTrailingSlash(value: string): string {
  return value.length > 1 && value.endsWith('/') ? value.slice(0, -1) : value
}

/**
 * True when the menu target `to` should be highlighted for `pathname`.
 * The root target matches only the root itself; every other target matches
 * its own path and any sub-path below it (segment boundary, not a raw prefix).
 */
export function isPathActive(pathname: string, to: string): boolean {
  const path = stripTrailingSlash(pathname)
  const target = stripTrailingSlash(to)
  if (target === '/') return path === '/'
  return path === target || path.startsWith(`${target}/`)
}

/** True when any of `targets` is active for `pathname`. */
export function isAnyPathActive(pathname: string, targets: readonly string[]): boolean {
  return targets.some((to) => isPathActive(pathname, to))
}

/** localStorage key holding the desktop sidebar's collapsed flag. */
export const SIDEBAR_COLLAPSED_KEY = 'sf.sidebar.collapsed'

/**
 * Reads the stored collapsed flag. Only the exact value the app writes ('1')
 * means collapsed; anything else (missing, edited, garbage) means expanded.
 */
export function parseCollapsedFlag(raw: string | null): boolean {
  return raw === '1'
}
