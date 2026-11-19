import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'
import { buildTransactionsQueryString, type TransactionFilters } from '@/features/transactions/filters'

/**
 * Request/response contracts mirroring 03-03's exact GET /api/transactions and
 * GET /api/categories response shape (see 03-07-PLAN.md's <interfaces> block)
 * -- no `any`, so a backend contract change surfaces here as a type error.
 *
 * `type`/`status` here are the RAW enum values the API returns per row
 * ("Debit"|"Credit", "Pending"|"Posted") -- distinct from the derived
 * request-side filter vocabulary ("income"|"expense"|"transfer",
 * "posted"|"pending") defined in filters.ts. Never conflate the two.
 */

/**
 * Wire values of Transaction.CategorySource (SimpleFinance.Domain.Enums.CategorySource) --
 * never renamed to match CONTEXT.md's "Pluggy/Rule/Manual" prose, only display-labeled via
 * CATEGORY_SOURCE_LABELS below.
 */
export type CategorySource = 'SystemMapping' | 'Rule' | 'UserOverride'

/** Cosmetic display names only (04-07-PLAN.md's <interfaces> block) -- the wire value stays the raw enum string. */
export const CATEGORY_SOURCE_LABELS: Record<CategorySource, string> = {
  SystemMapping: 'Pluggy',
  Rule: 'Rule',
  UserOverride: 'Manual',
}

export interface TransactionListItem {
  id: string
  date: string
  description: string
  merchantName: string | null
  accountId: string
  accountName: string
  categoryId: string | null
  categoryName: string | null
  amount: number
  type: 'Debit' | 'Credit'
  status: 'Pending' | 'Posted'
  isTransfer: boolean
  isHidden: boolean
  categorySource: CategorySource
}

export interface TransactionsPageResponse {
  items: TransactionListItem[]
  page: number
  pageSize: number
  totalCount: number
}

/**
 * `Category`/`useCategoriesQuery` below are an intentional second, independent
 * implementation of the same GET /api/categories endpoint already exposed by
 * `frontend2/src/features/categories/api.ts` (`CategoryResponse`/`useCategoriesQuery`
 * with queryKey `['categories', accessToken]`). This file's queryKey is the plain
 * `['categories']` (no accessToken) -- mirroring v1's own per-feature duplication
 * (frontend/src/features/transactions/api.ts keeps its own lightweight Category
 * shape rather than importing settings/categories' richer one). Do NOT consolidate
 * these two into a single shared implementation in this plan.
 */
export interface Category {
  id: string
  name: string
  parentCategoryId: string | null
  type: string
  icon: string | null
  color: string | null
  isHidden: boolean
}

export function useTransactionsQuery(accessToken: string, filters: TransactionFilters, page: number) {
  return useQuery({
    queryKey: ['transactions', filters, page],
    queryFn: () =>
      apiFetch<TransactionsPageResponse>(
        `/api/transactions?${buildTransactionsQueryString(filters, page)}`,
        { accessToken }
      ),
    enabled: !!accessToken,
  })
}

export function useCategoriesQuery(accessToken: string) {
  return useQuery({
    queryKey: ['categories'],
    queryFn: () => apiFetch<Category[]>('/api/categories', { accessToken }),
    enabled: !!accessToken,
  })
}

/**
 * Detail + mutation contracts mirroring 03-05's exact PATCH/GET shapes (see
 * 03-08-PLAN.md's <interfaces> block). `TransactionDetail` is the shared
 * response shape returned by GET /api/transactions/{id} AND all 4 PATCH
 * mutation routes below (03-05's `BuildTransactionDetailResponseAsync`).
 */

export interface TransactionDetail {
  id: string
  date: string
  description: string
  descriptionRaw: string
  merchantName: string | null
  merchantDocument: string | null
  accountId: string
  accountName: string
  categoryId: string | null
  categoryName: string | null
  amount: number
  type: 'Debit' | 'Credit'
  status: 'Pending' | 'Posted'
  isTransfer: boolean
  isHidden: boolean
  notes: string | null
  installmentNumber: number | null
  totalInstallments: number | null
  billId: string | null
  categorySource: CategorySource
}

export interface SimilarCountResponse {
  count: number
  merchant: string
}

export interface ApplySimilarResponse {
  updatedCount: number
}

/**
 * Every mutation below invalidates both `['transactions']` (the list) and
 * `['transaction', id]` (the detail) on success, following 03-04's
 * `useToggleAccountHideMutation` invalidation pattern -- the feed and the
 * open detail Sheet/Offcanvas both stay in sync after any single-field mutation.
 */
function invalidateTransactionQueries(queryClient: QueryClient, id: string) {
  void queryClient.invalidateQueries({ queryKey: ['transactions'] })
  void queryClient.invalidateQueries({ queryKey: ['transaction', id] })
}

export function useTransactionDetailQuery(accessToken: string, id: string | null) {
  return useQuery({
    queryKey: ['transaction', id],
    queryFn: () => apiFetch<TransactionDetail>(`/api/transactions/${id}`, { accessToken }),
    enabled: !!accessToken && !!id,
  })
}

export interface RecategorizeRequest {
  accessToken: string
  id: string
  categoryId: string | null
}

export function useRecategorizeMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, categoryId }: RecategorizeRequest) =>
      apiFetch<TransactionDetail>(`/api/transactions/${id}/category`, {
        method: 'PATCH',
        body: { categoryId },
        accessToken,
      }),
    onSuccess: (_data, variables) => invalidateTransactionQueries(queryClient, variables.id),
  })
}

export interface UpdateNoteRequest {
  accessToken: string
  id: string
  note: string | null
}

export function useUpdateNoteMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, note }: UpdateNoteRequest) =>
      apiFetch<TransactionDetail>(`/api/transactions/${id}/note`, {
        method: 'PATCH',
        body: { note },
        accessToken,
      }),
    onSuccess: (_data, variables) => invalidateTransactionQueries(queryClient, variables.id),
  })
}

export interface ToggleHiddenRequest {
  accessToken: string
  id: string
  isHidden: boolean
}

export function useToggleHiddenMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, isHidden }: ToggleHiddenRequest) =>
      apiFetch<TransactionDetail>(`/api/transactions/${id}/hide`, {
        method: 'PATCH',
        body: { isHidden },
        accessToken,
      }),
    onSuccess: (_data, variables) => invalidateTransactionQueries(queryClient, variables.id),
  })
}

export interface ToggleTransferRequest {
  accessToken: string
  id: string
  isTransfer: boolean
}

export function useToggleTransferMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, isTransfer }: ToggleTransferRequest) =>
      apiFetch<TransactionDetail>(`/api/transactions/${id}/transfer`, {
        method: 'PATCH',
        body: { isTransfer },
        accessToken,
      }),
    onSuccess: (_data, variables) => invalidateTransactionQueries(queryClient, variables.id),
  })
}

export function useSimilarCountQuery(accessToken: string, id: string | null, categoryId: string | null) {
  return useQuery({
    queryKey: ['transaction-similar', id, categoryId],
    queryFn: () =>
      apiFetch<SimilarCountResponse>(
        `/api/transactions/${id}/similar?categoryId=${encodeURIComponent(categoryId ?? '')}`,
        { accessToken }
      ),
    enabled: !!accessToken && !!id && !!categoryId,
  })
}

export interface ApplySimilarRequest {
  accessToken: string
  id: string
  categoryId: string
}

export function useApplySimilarMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, categoryId }: ApplySimilarRequest) =>
      apiFetch<ApplySimilarResponse>(`/api/transactions/${id}/apply-similar`, {
        method: 'POST',
        body: { categoryId },
        accessToken,
      }),
    onSuccess: (_data, variables) => invalidateTransactionQueries(queryClient, variables.id),
  })
}
