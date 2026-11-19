import { useMutation } from '@tanstack/react-query'

import { apiFetch } from '@/lib/apiClient'

/**
 * Request/response contracts mirroring 01-04/01-05's backend exactly (see
 * 01-06-PLAN.md's <interfaces> block) — no `any`, so a backend contract
 * change surfaces here as a type error.
 */

export interface SetupRequest {
  name: string
  email: string
  password: string
}

export interface SetupResponse {
  userId: string
  email: string
}

export interface LoginRequest {
  email: string
  password: string
}

export interface AuthUser {
  id: string
  name: string
  email: string
}

/**
 * Exactly one of the two shapes is populated, matching LoginResponse's
 * backend contract: requiresTwoFactor === false carries a full session;
 * requiresTwoFactor === true carries only pendingToken.
 */
export interface LoginResponse {
  requiresTwoFactor: boolean
  accessToken?: string
  accessTokenExpiresAt?: string
  user?: AuthUser
  pendingToken?: string
}

export interface VerifyTwoFactorRequest {
  pendingToken: string
  code: string
}

/** /2fa/verify's success shape is the same "authenticated session" shape as a non-2FA login. */
export interface VerifyTwoFactorResponse {
  requiresTwoFactor: false
  accessToken: string
  accessTokenExpiresAt: string
  user: AuthUser
}

export interface RefreshResponse {
  accessToken: string
  accessTokenExpiresAt: string
}

/**
 * The three [Authorize]-gated requests below (01-05's /2fa/enable,
 * /2fa/activate and 01-04's /change-password) all carry an `accessToken`
 * field so the mutation hook itself stays argument-less (same shape as the
 * pre-auth hooks above) — callers pass the token from useAuth() at
 * mutateAsync() time, same pattern as pendingToken on VerifyTwoFactorRequest.
 */
export interface EnableTwoFactorRequest {
  accessToken: string
}

export interface EnableTwoFactorResponse {
  secret: string
  otpauthUri: string
  qrCodePngBase64: string
}

export interface ActivateTwoFactorRequest {
  accessToken: string
  code: string
}

export interface ActivateTwoFactorResponse {
  recoveryCodes: string[]
}

export interface ChangePasswordRequest {
  accessToken: string
  currentPassword: string
  newPassword: string
}

export function useSetup() {
  return useMutation({
    mutationFn: (request: SetupRequest) =>
      apiFetch<SetupResponse>('/api/auth/setup', { method: 'POST', body: request }),
  })
}

export function useLogin() {
  return useMutation({
    mutationFn: (request: LoginRequest) =>
      apiFetch<LoginResponse>('/api/auth/login', { method: 'POST', body: request }),
  })
}

export function useVerifyTwoFactor() {
  return useMutation({
    mutationFn: (request: VerifyTwoFactorRequest) =>
      apiFetch<VerifyTwoFactorResponse>('/api/auth/2fa/verify', { method: 'POST', body: request }),
  })
}

export function useRefresh() {
  return useMutation({
    mutationFn: () => apiFetch<RefreshResponse>('/api/auth/refresh', { method: 'POST' }),
  })
}

export function useLogout() {
  return useMutation({
    mutationFn: () => apiFetch<void>('/api/auth/logout', { method: 'POST' }),
  })
}

export function useEnableTwoFactor() {
  return useMutation({
    mutationFn: ({ accessToken }: EnableTwoFactorRequest) =>
      apiFetch<EnableTwoFactorResponse>('/api/auth/2fa/enable', { method: 'POST', accessToken }),
  })
}

export function useActivateTwoFactor() {
  return useMutation({
    mutationFn: ({ accessToken, code }: ActivateTwoFactorRequest) =>
      apiFetch<ActivateTwoFactorResponse>('/api/auth/2fa/activate', {
        method: 'POST',
        body: { code },
        accessToken,
      }),
  })
}

export function useChangePassword() {
  return useMutation({
    mutationFn: ({ accessToken, currentPassword, newPassword }: ChangePasswordRequest) =>
      apiFetch<void>('/api/auth/change-password', {
        method: 'POST',
        body: { currentPassword, newPassword },
        accessToken,
      }),
  })
}
