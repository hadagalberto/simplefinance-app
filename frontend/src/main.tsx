/**
 * SimpleFinance browser entry.
 *
 * Provider order, outermost first:
 * - BrowserRouter: the providers below and every page use navigation.
 * - QueryClientProvider: sits above auth because the auth provider runs queries.
 * - AppProviders: theme, toasts and auth.
 * - AppRoutes: the page for the current URL.
 */
import '@/i18n'
import '@/styles/app.scss'

import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { QueryClientProvider } from '@tanstack/react-query'

import { queryClient } from '@/lib/queryClient'
import { AppRoutes } from '@/routes/AppRoutes'
import { AppProviders } from '@/shell/AppProviders'

const mountElement = document.getElementById('root')
if (!mountElement) {
  throw new Error('SimpleFinance cannot start: the #root mount element is missing from index.html.')
}

createRoot(mountElement).render(
  <StrictMode>
    <BrowserRouter basename={import.meta.env.BASE_URL}>
      <QueryClientProvider client={queryClient}>
        <AppProviders>
          <AppRoutes />
        </AppProviders>
      </QueryClientProvider>
    </BrowserRouter>
  </StrictMode>,
)
