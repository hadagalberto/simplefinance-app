import { useQuery } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Request/response contracts mirroring 06-03's exact GET /api/investments and
 * GET /api/investments/summary response shapes (see 06-05-PLAN.md's
 * <interfaces> block) -- no `any`, so a backend contract change surfaces here
 * as a type error. Both hooks follow the established accessToken-passed-
 * explicitly, TanStack Query v5, `enabled: !!accessToken` pattern.
 */

export interface InvestmentPositionItem {
  id: string
  name: string
  type: string
  subtype: string | null
  institution: string | null
  issuerName: string | null
  value: number | null
  amountInvested: number | null
  gain: number | null
  gainPercent: number | null
  annualRate: number | null
  lastMonthRate: number | null
  date: string | null
  dueDate: string | null
  currencyCode: string | null
}

export interface InvestmentPositionsResult {
  items: InvestmentPositionItem[]
}

export interface InvestmentDistributionItem {
  label: string
  value: number
  percent: number
}

export interface InvestmentsSummaryResult {
  totalInvested: number
  totalCurrentValue: number
  totalGain: number
  totalGainPercent: number | null
  byType: InvestmentDistributionItem[]
  byIssuer: InvestmentDistributionItem[]
}

export function useInvestmentPositionsQuery(accessToken: string) {
  return useQuery({
    queryKey: ['investment-positions'],
    queryFn: () => apiFetch<InvestmentPositionsResult>('/api/investments', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useInvestmentSummaryQuery(accessToken: string) {
  return useQuery({
    queryKey: ['investment-summary'],
    queryFn: () => apiFetch<InvestmentsSummaryResult>('/api/investments/summary', { accessToken }),
    enabled: !!accessToken,
  })
}
