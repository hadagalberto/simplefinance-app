const API_BASE_URL = import.meta.env.VITE_API_BASE_URL as string

export type ApiValidationErrors = Record<string, string[]>

export class ApiError extends Error {
  readonly status: number
  readonly error?: string
  readonly errors?: ApiValidationErrors

  constructor(status: number, body: { error?: string; errors?: ApiValidationErrors } | undefined) {
    super(body?.error ?? 'request_failed')
    this.name = 'ApiError'
    this.status = status
    this.error = body?.error
    this.errors = body?.errors
  }
}

interface ApiFetchOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'
  body?: unknown
  accessToken?: string
}

export async function apiFetch<T>(path: string, options: ApiFetchOptions = {}): Promise<T> {
  let response: Response

  try {
    response = await fetch(`${API_BASE_URL}${path}`, {
      method: options.method ?? 'GET',
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
        ...(options.accessToken ? { Authorization: `Bearer ${options.accessToken}` } : {}),
      },
      body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
    })
  } catch {
    throw new ApiError(0, undefined)
  }

  if (response.status === 204) {
    return undefined as T
  }

  const text = await response.text()
  let payload: unknown

  if (text) {
    try {
      payload = JSON.parse(text)
    } catch {
      payload = undefined
    }
  }

  if (!response.ok) {
    throw new ApiError(response.status, payload as { error?: string; errors?: ApiValidationErrors } | undefined)
  }

  return payload as T
}
