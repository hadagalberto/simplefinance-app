import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Request/response contract mirroring v1.0 Phase 8's (08-06) GET/PUT
 * /api/preferences exactly (see 13-04-PLAN.md's <interfaces> block) -- no
 * `any`, so a backend contract change surfaces here as a type error.
 * Follows every other feature's accessToken-per-call TanStack Query v5
 * pattern: one query hook, one update mutation invalidating ['preferences']
 * on success.
 */

export interface PreferencesResponse {
  timezone: string
  baseCurrency: string
  syncCronExpression: string
  defaultTheme: 'light' | 'dark'
}

export interface UpdatePreferencesRequest extends PreferencesResponse {
  accessToken: string
}

export function usePreferencesQuery(accessToken: string) {
  return useQuery({
    queryKey: ['preferences'],
    queryFn: () => apiFetch<PreferencesResponse>('/api/preferences', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useUpdatePreferencesMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, ...fields }: UpdatePreferencesRequest) =>
      apiFetch<PreferencesResponse>('/api/preferences', {
        method: 'PUT',
        body: fields,
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['preferences'] })
    },
  })
}
