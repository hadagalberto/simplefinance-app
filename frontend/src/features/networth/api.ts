import { useQuery } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Request/response contracts mirroring 06-04's exact GET /api/networth/summary
 * and GET /api/networth/evolution response shapes (NetWorthSummaryResult /
 * NetWorthEvolutionResult) -- no `any`, so a backend contract change surfaces
 * here as a type error. Both hooks follow useDashboardSummaryQuery's exact
 * pattern (accessToken passed explicitly, TanStack Query v5, `enabled:
 * !!accessToken`). Query keys ('networth-summary'/'networth-evolution') are
 * distinct from the Dashboard's own 'dashboard-net-worth-evolution' key, so
 * TanStack Query never collides this screen's cache entries with the
 * Dashboard's mini evolution widget.
 */

export interface NetWorthSummary {
  accountsBalance: number
  investmentsBalance: number
  creditCardDebt: number
  netWorth: number
}

export interface NetWorthEvolutionPoint {
  date: string
  netWorth: number
}

export interface NetWorthEvolutionResult {
  points: NetWorthEvolutionPoint[]
}

export function useNetWorthSummaryQuery(accessToken: string) {
  return useQuery({
    queryKey: ['networth-summary'],
    queryFn: () => apiFetch<NetWorthSummary>('/api/networth/summary', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useNetWorthEvolutionQuery(accessToken: string, days: 30 | 90 | 365) {
  return useQuery({
    queryKey: ['networth-evolution', days],
    queryFn: () =>
      apiFetch<NetWorthEvolutionResult>(`/api/networth/evolution?days=${days}`, { accessToken }),
    enabled: !!accessToken,
  })
}
