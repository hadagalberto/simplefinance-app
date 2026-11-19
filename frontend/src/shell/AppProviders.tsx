import type { ReactNode } from 'react'

import { AuthProvider } from '@/features/auth/AuthContext'

import { ThemeProvider } from './theme/ThemeProvider'
import { ToastProvider } from './toast/ToastProvider'

/** App-wide providers mounted above the router's routes: theme, toasts, then auth. */
export function AppProviders({ children }: { children: ReactNode }) {
  return (
    <ThemeProvider>
      <ToastProvider>
        <AuthProvider>{children}</AuthProvider>
      </ToastProvider>
    </ThemeProvider>
  )
}
