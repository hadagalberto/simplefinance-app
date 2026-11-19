import { createContext } from 'react'

import type { ResolvedTheme, ThemePreference } from './themeLogic'

export type ThemeContextValue = {
  /** The stored preference: light, dark or system. */
  theme: ThemePreference
  /** The mode currently applied to the page. */
  resolvedTheme: ResolvedTheme
  /** Changes the preference, applies it and stores it. */
  setTheme: (next: ThemePreference) => void
}

export const ThemeContext = createContext<ThemeContextValue | null>(null)
