import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Ported verbatim from frontend/src/features/budget/api.ts (v1) --
 * request/response contracts mirroring 05-05's exact GET/POST/DELETE
 * /api/budgets response shapes, no `any`, so a backend contract change
 * surfaces here as a type error. Every hook follows useAccountsQuery's
 * exact pattern (accessToken passed explicitly per call, TanStack Query
 * v5). Only the `apiFetch` import path stays '@/lib/apiClient' (already
 * identical) -- no other changes.
 */

export type BudgetState = 'Ok' | 'Attention' | 'Exceeded'

export interface BudgetRow {
  id: string
  categoryId: string | null
  categoryName: string | null
  categoryIcon: string | null
  categoryColor: string | null
  limitAmount: number
  spentAmount: number
  percentOfCap: number
  remaining: number
  projectedSpend: number
  projectedPercent: number
  state: BudgetState
  isAtRisk: boolean
}

export interface BudgetsResponse {
  year: number
  month: number
  copiedFromPreviousMonth: boolean
  items: BudgetRow[]
}

export interface UpsertBudgetRequest {
  accessToken: string
  categoryId: string | null
  year: number
  month: number
  limitAmount: number
}

export interface DeleteBudgetRequest {
  accessToken: string
  id: string
}

export function useBudgetsQuery(accessToken: string, year: number, month: number) {
  return useQuery({
    queryKey: ['budgets', year, month],
    queryFn: () => apiFetch<BudgetsResponse>(`/api/budgets?year=${year}&month=${month}`, { accessToken }),
    enabled: !!accessToken,
  })
}

/**
 * Broad `['budgets']` invalidation on success (not a narrower
 * `['budgets', year, month]` key) -- matches useToggleAccountHideMutation's
 * precedent so any budgets query for any month refetches after an upsert.
 */
export function useUpsertBudgetMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, categoryId, year, month, limitAmount }: UpsertBudgetRequest) =>
      apiFetch<BudgetRow>('/api/budgets', {
        method: 'POST',
        body: { categoryId, year, month, limitAmount },
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['budgets'] })
    },
  })
}

export function useDeleteBudgetMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: DeleteBudgetRequest) =>
      apiFetch<void>(`/api/budgets/${id}`, {
        method: 'DELETE',
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['budgets'] })
    },
  })
}
