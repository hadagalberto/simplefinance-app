import { use } from 'react'

import { ThemeContext, type ThemeContextValue } from './ThemeContext'

/** The app's theme preference, the applied mode, and the setter. */
export function useTheme(): ThemeContextValue {
  const value = use(ThemeContext)
  if (value === null) {
    throw new Error('useTheme must be used inside ThemeProvider')
  }
  return value
}
