import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Request/response contracts mirroring v1's
 * frontend/src/features/settings/alerts/api.ts (Plan 08-05's AlertRule CRUD
 * -- GET/POST/PUT/PATCH-active/DELETE /api/alert-rules -- and channel-status/
 * test-notification endpoints -- GET /api/notification-channels, POST
 * /{type}/test) exactly. Follows features/rules/api.ts's accessToken-per-call
 * TanStack Query v5 pattern: one hook per endpoint, alert-rule mutations
 * invalidate ['alert-rules'] on success.
 *
 * AlertRuleType/NotificationChannel are declared as local literal-union
 * types in THIS file (not imported from @/features/notifications/api)
 * -- a deliberate parallelism decision (see 13-06-PLAN.md's <objective>):
 * both plans wire the exact same backend enum values, but keeping the type
 * declaration local means this plan has zero file/module dependency on
 * Plan 13-05, so both stay fully parallel in Wave 1.
 */

export type AlertRuleType =
  | 'BudgetThreshold'
  | 'LowBalance'
  | 'InvoiceDue'
  | 'LargeTransaction'
  | 'NewRecurring'
  | 'SyncError'
  | 'GoalReached'

export type NotificationChannel = 'Telegram' | 'Email' | 'Both'

export interface AlertRuleResponse {
  id: string
  type: AlertRuleType
  threshold: number | null
  categoryId: string | null
  categoryName: string | null
  accountId: string | null
  accountName: string | null
  daysBefore: number | null
  channel: NotificationChannel
  isActive: boolean
}

export interface AlertRuleWriteFields {
  type: AlertRuleType
  threshold: number | null
  categoryId: string | null
  accountId: string | null
  daysBefore: number | null
  channel: NotificationChannel
  isActive: boolean
}

export interface CreateAlertRuleRequest extends AlertRuleWriteFields {
  accessToken: string
}

export interface UpdateAlertRuleRequest extends AlertRuleWriteFields {
  accessToken: string
  id: string
}

export interface ToggleAlertRuleActiveRequest {
  accessToken: string
  id: string
  isActive: boolean
}

export interface DeleteAlertRuleRequest {
  accessToken: string
  id: string
}

export function useAlertRulesQuery(accessToken: string) {
  return useQuery({
    queryKey: ['alert-rules'],
    queryFn: () => apiFetch<AlertRuleResponse[]>('/api/alert-rules', { accessToken }),
    enabled: !!accessToken,
  })
}

function invalidateAlertRules(queryClient: ReturnType<typeof useQueryClient>) {
  void queryClient.invalidateQueries({ queryKey: ['alert-rules'] })
}

export function useCreateAlertRuleMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, ...fields }: CreateAlertRuleRequest) =>
      apiFetch<AlertRuleResponse>('/api/alert-rules', {
        method: 'POST',
        body: fields,
        accessToken,
      }),
    onSuccess: () => invalidateAlertRules(queryClient),
  })
}

export function useUpdateAlertRuleMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, ...fields }: UpdateAlertRuleRequest) =>
      apiFetch<AlertRuleResponse>(`/api/alert-rules/${id}`, {
        method: 'PUT',
        body: fields,
        accessToken,
      }),
    onSuccess: () => invalidateAlertRules(queryClient),
  })
}

export function useToggleAlertRuleActiveMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, isActive }: ToggleAlertRuleActiveRequest) =>
      apiFetch<AlertRuleResponse>(`/api/alert-rules/${id}/active`, {
        method: 'PATCH',
        body: { isActive },
        accessToken,
      }),
    onSuccess: () => invalidateAlertRules(queryClient),
  })
}

export function useDeleteAlertRuleMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: DeleteAlertRuleRequest) =>
      apiFetch<void>(`/api/alert-rules/${id}`, {
        method: 'DELETE',
        accessToken,
      }),
    onSuccess: () => invalidateAlertRules(queryClient),
  })
}

export interface NotificationChannelStatus {
  type: NotificationChannel
  isConfigured: boolean
}

export interface TestNotificationChannelRequest {
  accessToken: string
  type: NotificationChannel
}

export interface TestNotificationChannelResponse {
  sent: boolean
}

/**
 * Channel status/test-notification hooks target a distinct query key
 * (['notification-channels']) with no cross-invalidation against
 * ['alert-rules'] -- configuring/testing a channel never changes rule data.
 */
export function useNotificationChannelsQuery(accessToken: string) {
  return useQuery({
    queryKey: ['notification-channels'],
    queryFn: () => apiFetch<NotificationChannelStatus[]>('/api/notification-channels', { accessToken }),
    enabled: !!accessToken,
  })
}

/**
 * Fire-and-report action, not a state mutation -- no query invalidation.
 * The mutation's own isPending/isSuccess/isError/data drive the UI directly.
 */
export function useTestNotificationChannelMutation() {
  return useMutation({
    mutationFn: ({ accessToken, type }: TestNotificationChannelRequest) =>
      apiFetch<TestNotificationChannelResponse>(`/api/notification-channels/${type}/test`, {
        method: 'POST',
        accessToken,
      }),
  })
}
