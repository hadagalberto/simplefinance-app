import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Request/response contracts mirroring 07-04's exact GET/POST/PATCH
 * /api/goals response shapes (see 13-03-PLAN.md's <interfaces> block) --
 * no `any`, so a backend contract change surfaces here as a type error.
 * Every hook follows useUpsertBudgetMutation's exact pattern (accessToken
 * passed explicitly per call, TanStack Query v5, broad `['goals']`
 * invalidation on every mutation).
 */

export type GoalStatus = 'Active' | 'Completed' | 'Archived'

export interface Goal {
  id: string
  name: string
  targetAmount: number
  currentAmount: number
  targetDate: string | null
  linkedAccountId: string | null
  linkedInvestmentId: string | null
  icon: string | null
  color: string | null
  status: GoalStatus
  percentComplete: number
  remaining: number
  requiredMonthlyPace: number | null
}

export interface CreateGoalRequest {
  accessToken: string
  name: string
  targetAmount: number
  targetDate: string | null
  linkedAccountId: string | null
  linkedInvestmentId: string | null
  icon: string | null
  color: string | null
}

export interface UpdateGoalRequest extends CreateGoalRequest {
  id: string
}

export interface UpdateGoalProgressRequest {
  accessToken: string
  id: string
  currentAmount: number
}

export interface ArchiveGoalRequest {
  accessToken: string
  id: string
}

export function useGoalsQuery(accessToken: string) {
  return useQuery({
    queryKey: ['goals'],
    queryFn: () => apiFetch<Goal[]>('/api/goals', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useCreateGoalMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, ...body }: CreateGoalRequest) =>
      apiFetch<Goal>('/api/goals', {
        method: 'POST',
        body,
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['goals'] })
    },
  })
}

export function useUpdateGoalMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, ...body }: UpdateGoalRequest) =>
      apiFetch<Goal>(`/api/goals/${id}`, {
        method: 'PATCH',
        body,
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['goals'] })
    },
  })
}

export function useUpdateGoalProgressMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, currentAmount }: UpdateGoalProgressRequest) =>
      apiFetch<Goal>(`/api/goals/${id}/progress`, {
        method: 'PATCH',
        body: { currentAmount },
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['goals'] })
    },
  })
}

export function useArchiveGoalMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: ArchiveGoalRequest) =>
      apiFetch<Goal>(`/api/goals/${id}/archive`, {
        method: 'PATCH',
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['goals'] })
    },
  })
}
