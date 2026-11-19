import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { ApiError, apiFetch } from '@/lib/apiClient'

/**
 * A GET that 404s is an expected "no data yet" state for the forecast + monthly
 * summary reads (the daily job hasn't run / AI is off), NOT a hard error — the
 * widgets render an empty state rather than a red error banner. Callers detect it
 * off the query error so the retry logic below skips 404 (no point retrying a
 * deterministic "not found").
 */
export function isNotFoundError(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404
}

/**
 * AI-FND-07/08: request/response contract mirroring the backend's GET/PUT
 * /api/ai/settings and GET /api/ai/usage exactly -- no `any`, so a backend
 * contract change surfaces here as a type error. Follows the preferences
 * feature's accessToken-per-call TanStack Query v5 pattern: one query hook per
 * GET, one update mutation invalidating ['ai','settings'] on success.
 *
 * The settings response carries a key-present boolean per provider and NEVER a
 * key value (T-15-14) -- deepSeekKeyPresent/openAiKeyPresent only.
 */

export interface AiSettingsResponse {
  aiEnabled: boolean
  aiCategorizationEnabled: boolean
  aiAssistantEnabled: boolean
  aiInsightsEnabled: boolean
  aiAnomalyEnabled: boolean
  aiForecastEnabled: boolean
  aiMonthlySummaryEnabled: boolean
  aiAutoApplyThreshold: number
  aiMonthlyBudgetUsd: number
  aiChatModel: string
  aiSummaryModel: string
  aiLanguage: string
  deepSeekKeyPresent: boolean
  openAiKeyPresent: boolean
}

/** Editable AI settings sent to PUT /api/ai/settings. */
export interface AiSettingsInput {
  aiEnabled: boolean
  aiCategorizationEnabled: boolean
  aiAssistantEnabled: boolean
  aiInsightsEnabled: boolean
  aiAnomalyEnabled: boolean
  aiForecastEnabled: boolean
  aiMonthlySummaryEnabled: boolean
  aiAutoApplyThreshold: number
  aiMonthlyBudgetUsd: number
  aiChatModel: string
  aiSummaryModel: string
  aiLanguage: string
}

/**
 * Write-only provider keys. Tri-state sent to PUT /api/ai/settings (mirrors the backend
 * UpdateAiSettingsCommand): the field is OMITTED to leave the stored key unchanged, sent as a
 * non-empty string to set it (encrypted at rest), or sent as an explicit "" to clear it (revert to
 * the env fallback). The key value is NEVER returned by the API — only the present flags on
 * AiSettingsResponse.
 */
export interface UpdateAiSettingsRequest extends AiSettingsInput {
  accessToken: string
  deepSeekApiKey?: string
  openAiApiKey?: string
}

export interface AiUsageFeature {
  feature: string
  promptTokens: number
  completionTokens: number
  estimatedCostUsd: number
}

export interface AiUsageResponse {
  month: string
  totalCostUsd: number
  monthlyBudgetUsd: number
  features: AiUsageFeature[]
}

export function useAiSettings(accessToken: string) {
  return useQuery({
    queryKey: ['ai', 'settings'],
    queryFn: () => apiFetch<AiSettingsResponse>('/api/ai/settings', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useUpdateAiSettings() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, ...fields }: UpdateAiSettingsRequest) =>
      apiFetch<AiSettingsResponse>('/api/ai/settings', {
        method: 'PUT',
        body: fields,
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['ai', 'settings'] })
    },
  })
}

export function useAiUsage(accessToken: string, month: string) {
  return useQuery({
    queryKey: ['ai', 'usage', month],
    queryFn: () =>
      apiFetch<AiUsageResponse>(`/api/ai/usage?month=${encodeURIComponent(month)}`, { accessToken }),
    enabled: !!accessToken,
  })
}

/**
 * AI-CAT-07: the pending AI-suggestion queue (16-05 GET /api/ai/suggestions).
 * Mirrors the backend `SuggestionResponse` shape exactly (camelCase, no `any`)
 * so a contract change surfaces here as a type error. `confidence` is 0-1
 * (rendered as a percentage in the tab). `source` is Embedding|Llm|Statistical,
 * `status` is Pending (the only listed view this phase).
 */
export interface SuggestionResponse {
  id: string
  transactionId: string | null
  transactionDescription: string
  merchantName: string | null
  suggestedCategoryId: string | null
  suggestedCategoryName: string | null
  confidence: number
  rationale: string | null
  source: string
  status: string
  createdAt: string
}

export interface SuggestionMutationRequest {
  accessToken: string
  id: string
}

export interface BulkAcceptSuggestionsRequest {
  accessToken: string
  ids: string[]
}

export interface BulkAcceptSuggestionsResponse {
  accepted: number
}

export interface RunCategorizationResponse {
  jobId: string
  enqueued: boolean
}

export function useSuggestions(accessToken: string) {
  return useQuery({
    queryKey: ['ai', 'suggestions'],
    queryFn: () =>
      apiFetch<SuggestionResponse[]>('/api/ai/suggestions?status=pending', { accessToken }),
    enabled: !!accessToken,
  })
}

/**
 * Accept/reject mutations invalidate BOTH the suggestions queue and the
 * transactions feed on success: accepting applies CategorySource=AiSuggested to
 * the transaction (16-05), so the feed must refetch to show the new category.
 * A 404 means the row was already decided elsewhere -- the caller refetches the
 * queue on error to reconcile with server truth (T-16-16).
 */
function invalidateSuggestionAndTransactionQueries(
  queryClient: ReturnType<typeof useQueryClient>
) {
  void queryClient.invalidateQueries({ queryKey: ['ai', 'suggestions'] })
  void queryClient.invalidateQueries({ queryKey: ['transactions'] })
}

export function useAcceptSuggestion() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: SuggestionMutationRequest) =>
      apiFetch<void>(`/api/ai/suggestions/${id}/accept`, { method: 'POST', accessToken }),
    onSuccess: () => invalidateSuggestionAndTransactionQueries(queryClient),
  })
}

export function useRejectSuggestion() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: SuggestionMutationRequest) =>
      apiFetch<void>(`/api/ai/suggestions/${id}/reject`, { method: 'POST', accessToken }),
    onSuccess: () => invalidateSuggestionAndTransactionQueries(queryClient),
  })
}

export function useBulkAcceptSuggestions() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, ids }: BulkAcceptSuggestionsRequest) =>
      apiFetch<BulkAcceptSuggestionsResponse>('/api/ai/suggestions/bulk-accept', {
        method: 'POST',
        body: { ids },
        accessToken,
      }),
    onSuccess: () => invalidateSuggestionAndTransactionQueries(queryClient),
  })
}

export function useRunCategorization() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken }: { accessToken: string }) =>
      apiFetch<RunCategorizationResponse>('/api/ai/categorize/run', {
        method: 'POST',
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['ai', 'suggestions'] })
    },
  })
}

/**
 * AI-INS-04: the proactive insight/anomaly card strip (18-06 GET /api/ai/insights).
 * Mirrors the backend `AiInsightResponse` record exactly (camelCase, no `any`) so a
 * contract change surfaces here as a type error. `kind` is
 * SpendingChange|Trend|SubscriptionCreep|BudgetRisk|Anomaly (Anomaly routes to
 * AnomalyCard, else InsightCard); `severity` is Info|Warning|Critical (drives the
 * badge color); `dataJson` is the authoritative jsonb figures blob (parsed on the
 * client, never the prose); `body` is untrusted display prose (React escapes it).
 */
export interface AiInsightResponse {
  id: string
  kind: string
  severity: string
  title: string
  body: string
  periodStart: string
  periodEnd: string
  dataJson: string
  status: string
  createdAt: string
}

export function useInsights(accessToken: string) {
  return useQuery({
    queryKey: ['ai', 'insights'],
    queryFn: () =>
      apiFetch<AiInsightResponse[]>('/api/ai/insights?status=new', { accessToken }),
    enabled: !!accessToken,
  })
}

/**
 * Dismiss retires one insight (Status→Dismissed) and invalidates ['ai','insights']
 * so the card disappears; the same (UserId, DedupKey) existence gate keeps a
 * dismissed insight from ever being re-created by the detection jobs. A 404 means
 * the row was already gone — the caller refetches the list to reconcile.
 */
export function useDismissInsight() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: SuggestionMutationRequest) =>
      apiFetch<void>(`/api/ai/insights/${id}/dismiss`, { method: 'POST', accessToken }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['ai', 'insights'] })
    },
  })
}

/**
 * AI-CHAT-06: conversation CRUD + write-action confirm/cancel over the 17-05/17-06
 * endpoints. Mirrors the backend records exactly (camelCase, no `any`) so a
 * contract drift surfaces here as a type error. All CRUD mutations invalidate
 * ['ai','conversations']; confirm invalidates the affected FEATURE query by
 * actionType (a confirmed create_goal must refetch ['goals'], etc.).
 */

export interface ConversationSummary {
  id: string
  title: string
  createdAt: string
  updatedAt: string
}

/** A single persisted turn. `role` is User|Assistant|Tool; `toolName` set on Tool turns. */
export interface ChatMessageDto {
  id: string
  role: string
  content: string | null
  toolName: string | null
  createdAt: string
}

export interface ConversationDetail {
  id: string
  title: string
  createdAt: string
  updatedAt: string
  /** Oldest-first. */
  messages: ChatMessageDto[]
}

/** POST .../confirm|cancel → 200 { status: "Confirmed" | "Cancelled" }. */
export interface ActionStatusResponse {
  status: string
}

export function useConversationsQuery(accessToken: string) {
  return useQuery({
    queryKey: ['ai', 'conversations'],
    queryFn: () => apiFetch<ConversationSummary[]>('/api/ai/conversations', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useConversationQuery(accessToken: string, conversationId: string | null) {
  return useQuery({
    queryKey: ['ai', 'conversations', conversationId],
    queryFn: () =>
      apiFetch<ConversationDetail>(`/api/ai/conversations/${conversationId}`, { accessToken }),
    enabled: !!accessToken && !!conversationId,
  })
}

export interface RenameConversationRequest {
  accessToken: string
  id: string
  title: string
}

export function useRenameConversationMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, title }: RenameConversationRequest) =>
      apiFetch<void>(`/api/ai/conversations/${id}`, {
        method: 'PATCH',
        body: { title },
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['ai', 'conversations'] })
    },
  })
}

export interface DeleteConversationRequest {
  accessToken: string
  id: string
}

export function useDeleteConversationMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: DeleteConversationRequest) =>
      apiFetch<void>(`/api/ai/conversations/${id}`, { method: 'DELETE', accessToken }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['ai', 'conversations'] })
    },
  })
}

/**
 * A confirmed write-action mutates a feature's data server-side (via the existing
 * domain services, 17-06), so the affected feature query must refetch. Keyed by
 * the action's `actionType` (the write-tool name).
 */
const ACTION_INVALIDATION_KEYS: Record<string, string[]> = {
  create_goal: ['goals'],
  set_budget: ['budgets'],
  create_category_rule: ['category-rules'],
  recategorize_transactions: ['transactions'],
}

/**
 * The feature query key a confirmed/decided action of `actionType` affects, or undefined for an
 * unknown type. Exported so the bulk "Confirmar todas" path can refetch the same query the
 * single-confirm mutation invalidates — including for an action decided elsewhere (409/404), which
 * must still reconcile the affected feature with server truth.
 */
export function actionInvalidationKey(actionType: string): string[] | undefined {
  return ACTION_INVALIDATION_KEYS[actionType]
}

export interface ActionDecisionRequest {
  accessToken: string
  id: string
  actionType: string
}

export function useConfirmActionMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: ActionDecisionRequest) =>
      apiFetch<ActionStatusResponse>(`/api/ai/actions/${id}/confirm`, {
        method: 'POST',
        accessToken,
      }),
    onSuccess: (_data, variables) => {
      const key = ACTION_INVALIDATION_KEYS[variables.actionType]
      if (key) void queryClient.invalidateQueries({ queryKey: key })
    },
  })
}

export function useCancelActionMutation() {
  return useMutation({
    mutationFn: ({ accessToken, id }: ActionDecisionRequest) =>
      apiFetch<ActionStatusResponse>(`/api/ai/actions/${id}/cancel`, {
        method: 'POST',
        accessToken,
      }),
  })
}

/**
 * AI-FCT-02: the caller's deterministic month forecast (19-02 GET /api/ai/forecast).
 * Mirrors the backend `AiForecastResponse` record exactly (camelCase, no `any`) so a
 * contract change surfaces here as a type error. The projected numbers + range are
 * authoritative; `byCategoryJson` is the per-category driver blob the client parses
 * (never the prose); `narrative` is untrusted display prose (React escapes it; null
 * when the budget cap was hit at generation time). A 404 = no forecast yet (empty state).
 */
export interface AiForecastResponse {
  month: string
  projectedIncome: number
  projectedExpense: number
  projectedBalance: number
  balanceLow: number
  balanceHigh: number
  byCategoryJson: string
  method: string
  narrative: string | null
  createdAt: string
}

/**
 * AI-FCT-04: the caller's persisted monthly summary (19-04 GET /api/ai/summary).
 * Mirrors the backend `AiSummaryResponse` record exactly (camelCase, no `any`).
 * `dataJson` is the authoritative fact package (parsed for figures); `title`/`body`
 * are untrusted display prose (escaped as React text children — never
 * dangerouslySetInnerHTML). A 404 = no summary yet (empty state).
 */
export interface AiSummaryResponse {
  id: string
  title: string
  body: string
  dataJson: string
  periodStart: string
  periodEnd: string
  createdAt: string
}

export function useForecast(accessToken: string, month: string) {
  return useQuery({
    queryKey: ['ai', 'forecast', month],
    queryFn: () =>
      apiFetch<AiForecastResponse>(`/api/ai/forecast?month=${encodeURIComponent(month)}`, { accessToken }),
    enabled: !!accessToken,
    // A 404 is an expected "no forecast yet" state — do not burn retries on it.
    retry: (failureCount, error) => !isNotFoundError(error) && failureCount < 3,
  })
}

export function useMonthlySummary(accessToken: string, month: string) {
  return useQuery({
    queryKey: ['ai', 'summary', month],
    queryFn: () =>
      apiFetch<AiSummaryResponse>(`/api/ai/summary?month=${encodeURIComponent(month)}`, { accessToken }),
    enabled: !!accessToken,
    // A 404 is an expected "no summary yet" state — do not burn retries on it.
    retry: (failureCount, error) => !isNotFoundError(error) && failureCount < 3,
  })
}

export interface ResendSummaryRequest {
  accessToken: string
  id: string
}

/**
 * POST /api/ai/summary/{id}/resend → 204. Re-pushes the STORED summary via
 * Telegram/e-mail — no body, no regeneration, no new insight (19-04's T-19-13
 * contract). No cache invalidation: resend does not change the read model.
 */
export function useResendSummary() {
  return useMutation({
    mutationFn: ({ accessToken, id }: ResendSummaryRequest) =>
      apiFetch<void>(`/api/ai/summary/${id}/resend`, { method: 'POST', accessToken }),
  })
}
