import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Request/response contracts mirroring Plan 08-05's GET /api/notifications +
 * POST /{id}/read exactly (see 08-07-PLAN.md's <interfaces> block). Follows
 * settings/rules/api.ts's accessToken-per-call TanStack Query v5 pattern: one
 * hook per endpoint, the mark-read mutation invalidates ['notifications'] on
 * success so the bell's unread badge and dropdown both refresh immediately.
 *
 * NotificationType/NotificationChannel use PascalCase values (matching
 * Transaction.Type/Transaction.Status's verified-correct casing per 08-05's
 * own documented decision), not RulesPage's uppercase-with-underscore
 * convention.
 */

export type NotificationType =
  | 'BudgetThreshold'
  | 'LowBalance'
  | 'InvoiceDue'
  | 'LargeTransaction'
  | 'NewRecurring'
  | 'SyncError'
  | 'GoalReached'

export type NotificationChannel = 'Telegram' | 'Email' | 'Both'

export interface NotificationItem {
  id: string
  type: NotificationType
  title: string
  message: string
  channel: NotificationChannel
  isRead: boolean
  sentAt: string | null
  createdAt: string
}

export interface NotificationsResponse {
  unreadCount: number
  items: NotificationItem[]
}

export interface MarkNotificationReadRequest {
  accessToken: string
  id: string
}

/**
 * 60s poll interval keeps the unread badge reasonably fresh without needing
 * WebSockets/SSE -- a single-user app checking hourly-cadence alerts doesn't
 * need sub-minute push latency.
 */
export function useNotificationsQuery(accessToken: string) {
  return useQuery({
    queryKey: ['notifications'],
    queryFn: () => apiFetch<NotificationsResponse>('/api/notifications', { accessToken }),
    enabled: !!accessToken,
    refetchInterval: 60000,
  })
}

export function useMarkNotificationReadMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: MarkNotificationReadRequest) =>
      apiFetch<void>(`/api/notifications/${id}/read`, {
        method: 'POST',
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['notifications'] })
    },
  })
}
