import { QueryClient } from '@tanstack/react-query'

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5_000,
      retry: (failureCount, error) => {
        const status = (error as { status?: number } | undefined)?.status
        if (status === 401 || status === 403) return false
        return failureCount < 2
      },
    },
    mutations: {
      retry: false,
    },
  },
})
