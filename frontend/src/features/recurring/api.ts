import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Request/response contracts mirroring 07-03's exact GET/PATCH /api/recurring
 * response shapes (see 07-07-PLAN.md's <interfaces> block) -- no `any`, so a
 * backend contract change surfaces here as a type error. `useRecurringQuery`
 * follows `useInvestmentPositionsQuery`'s exact pattern; both mutations
 * follow `useToggleAccountHideMutation`'s exact mutation+invalidate pattern.
 *
 * Ported verbatim from frontend/src/features/recurring/api.ts -- Plan 13-07's
 * "recurring" report tab imports RecurringListResponse/RecurringItem from
 * this exact file, so every exported name and shape must stay unchanged.
 */

export type RecurringFrequency = 'Weekly' | 'Monthly' | 'Yearly'

export interface RecurringItem {
  id: string
  displayName: string
  categoryId: string | null
  categoryName: string | null
  categoryIcon: string | null
  categoryColor: string | null
  averageAmount: number
  frequency: RecurringFrequency
  lastSeenDate: string
  nextExpectedDate: string
  occurrenceCount: number
  yearlyTotal: number
  isSubscription: boolean
  isActive: boolean
  isNewlyDetected: boolean
  amountIncreaseDetected: boolean
  previousAverageAmount: number | null
}

export interface RecurringListResponse {
  monthlyCommittedCost: number
  items: RecurringItem[]
}

export interface ToggleRecurringSubscriptionRequest {
  accessToken: string
  id: string
  isSubscription: boolean
}

export interface ToggleRecurringActiveRequest {
  accessToken: string
  id: string
  isActive: boolean
}

export function useRecurringQuery(accessToken: string) {
  return useQuery({
    queryKey: ['recurring'],
    queryFn: () => apiFetch<RecurringListResponse>('/api/recurring', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useToggleSubscriptionMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, isSubscription }: ToggleRecurringSubscriptionRequest) =>
      apiFetch<RecurringItem>(`/api/recurring/${id}/subscription`, {
        method: 'PATCH',
        body: { isSubscription },
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['recurring'] })
    },
  })
}

export function useToggleActiveMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, isActive }: ToggleRecurringActiveRequest) =>
      apiFetch<RecurringItem>(`/api/recurring/${id}/active`, {
        method: 'PATCH',
        body: { isActive },
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['recurring'] })
    },
  })
}
