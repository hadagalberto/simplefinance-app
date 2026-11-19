import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Ported verbatim from frontend/src/features/settings/categories/api.ts
 * (v1) -- request/response contracts mirroring the category CRUD
 * endpoints exactly, no `any`, so a backend contract change surfaces here
 * as a type error. Only the `apiFetch` import path changed.
 */

export type CategoryType = 'Expense' | 'Income' | 'Transfer'

export interface CategoryResponse {
  id: string
  name: string
  parentCategoryId: string | null
  type: CategoryType
  icon: string | null
  color: string | null
  isHidden: boolean
  isSystem: boolean
}

export interface AccessTokenRequest {
  accessToken: string
}

export interface CategoryWriteFields {
  name: string
  type: CategoryType
  icon: string
  color: string
  parentCategoryId: string | null
}

export interface CreateCategoryRequest extends AccessTokenRequest, CategoryWriteFields {}

export interface UpdateCategoryRequest extends AccessTokenRequest, CategoryWriteFields {
  id: string
}

export interface ToggleCategoryHideRequest extends AccessTokenRequest {
  id: string
  isHidden: boolean
}

export interface DeleteCategoryRequest extends AccessTokenRequest {
  id: string
}

export function useCategoriesQuery(accessToken: string) {
  return useQuery({
    queryKey: ['categories', accessToken],
    queryFn: () => apiFetch<CategoryResponse[]>('/api/categories', { accessToken }),
    enabled: !!accessToken,
  })
}

export function useCreateCategoryMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, ...body }: CreateCategoryRequest) =>
      apiFetch<CategoryResponse>('/api/categories', { method: 'POST', body, accessToken }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['categories'] })
    },
  })
}

export function useUpdateCategoryMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, ...body }: UpdateCategoryRequest) =>
      apiFetch<CategoryResponse>(`/api/categories/${id}`, { method: 'PUT', body, accessToken }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['categories'] })
    },
  })
}

export function useToggleCategoryHideMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id, isHidden }: ToggleCategoryHideRequest) =>
      apiFetch<CategoryResponse>(`/api/categories/${id}/hide`, {
        method: 'PATCH',
        body: { isHidden },
        accessToken,
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['categories'] })
    },
  })
}

export function useDeleteCategoryMutation() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: ({ accessToken, id }: DeleteCategoryRequest) =>
      apiFetch<void>(`/api/categories/${id}`, { method: 'DELETE', accessToken }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['categories'] })
    },
  })
}
