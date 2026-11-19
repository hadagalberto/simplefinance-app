// Theme preference rules for the app shell.
//
// This module has no imports, so the node:test suite in
// scripts/shell-logic.test.mjs loads it directly.

/** What the user picked: a fixed mode, or follow the operating system. */
export type ThemePreference = 'light' | 'dark' | 'system'

/** The mode actually applied to the page. */
export type ResolvedTheme = 'light' | 'dark'

/** localStorage key that holds the user's theme preference. */
export const THEME_STORAGE_KEY = 'sf.theme'

/** Preference used when nothing valid is stored. */
export const DEFAULT_THEME: ThemePreference = 'dark'

/** Reads a stored value. Only the exact strings light, dark and system are accepted. */
export function parseThemePreference(raw: string | null): ThemePreference {
  if (raw === 'light' || raw === 'dark' || raw === 'system') {
    return raw
  }
  return DEFAULT_THEME
}

/** Turns a preference into the mode to apply, given whether the OS prefers dark. */
export function resolveTheme(preference: ThemePreference, prefersDark: boolean): ResolvedTheme {
  if (preference === 'system') {
    return prefersDark ? 'dark' : 'light'
  }
  return preference
}
