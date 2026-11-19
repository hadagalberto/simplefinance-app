import { useCallback, useLayoutEffect, useMemo, useState, useSyncExternalStore, type ReactNode } from 'react'

import { ThemeContext, type ThemeContextValue } from './ThemeContext'
import { parseThemePreference, resolveTheme, THEME_STORAGE_KEY, type ThemePreference } from './themeLogic'

const DARK_QUERY = '(prefers-color-scheme: dark)'

function subscribeToOsTheme(onChange: () => void): () => void {
  const query = window.matchMedia(DARK_QUERY)
  query.addEventListener('change', onChange)
  return () => query.removeEventListener('change', onChange)
}

function getOsPrefersDark(): boolean {
  return window.matchMedia(DARK_QUERY).matches
}

function getServerOsPrefersDark(): boolean {
  return true
}

function readStoredTheme(): ThemePreference {
  try {
    return parseThemePreference(window.localStorage.getItem(THEME_STORAGE_KEY))
  } catch {
    return parseThemePreference(null)
  }
}

/**
 * Owns the color mode of the whole app. The preference (light, dark or
 * system) is kept in localStorage, and the resolved mode is written as
 * data-bs-theme on <html>. With system, the page follows the OS setting
 * live. Chart colors read that same attribute.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<ThemePreference>(readStoredTheme)
  const prefersDark = useSyncExternalStore(subscribeToOsTheme, getOsPrefersDark, getServerOsPrefersDark)
  const resolvedTheme = resolveTheme(theme, prefersDark)

  useLayoutEffect(() => {
    document.documentElement.setAttribute('data-bs-theme', resolvedTheme)
  }, [resolvedTheme])

  const setTheme = useCallback((next: ThemePreference) => {
    setThemeState(next)
    try {
      window.localStorage.setItem(THEME_STORAGE_KEY, next)
    } catch {
      // Storage can be unavailable (private mode, quota). The choice still applies for this session.
    }
  }, [])

  const value = useMemo<ThemeContextValue>(() => ({ theme, resolvedTheme, setTheme }), [theme, resolvedTheme, setTheme])

  return <ThemeContext value={value}>{children}</ThemeContext>
}
