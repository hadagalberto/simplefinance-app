import { createContext, useCallback, useEffect, useRef, useState, type ReactNode } from 'react'

import { useLogout as useLogoutMutation, useRefresh, type AuthUser } from './api'

export interface AuthSession {
  accessToken: string
  accessTokenExpiresAt: string
  user: AuthUser
}

export interface AuthContextValue {
  /** In-memory only — NEVER persisted to localStorage/sessionStorage (T-01-20). */
  accessToken: string | null
  user: AuthUser | null
  isAuthenticated: boolean
  /** True while the mount-time silent refresh is in flight. */
  isBootstrapping: boolean
  /** Called by Setup/Login/2FA-verify on a fully-authenticated response. */
  setSession: (session: AuthSession) => void
  logout: () => Promise<void>
}

export const AuthContext = createContext<AuthContextValue | undefined>(undefined)

/**
 * Decodes a JWT's payload without verifying its signature — safe here
 * because the token was just issued/rotated by our own backend over an
 * authenticated channel; this is purely for reading claims locally (id,
 * email), not for trust decisions. Access tokens carry only `sub`/`email`
 * (see JwtAccessTokenService), so `name` isn't recoverable this way — the
 * silent-refresh path falls back to `email` for `name` until the user's
 * next full login/setup response repopulates it. 01-07's authenticated
 * shell should tolerate this (a future /me endpoint would close the gap).
 */
function decodeAccessTokenClaims(accessToken: string): { sub?: string; email?: string } {
  try {
    const payload = accessToken.split('.')[1]
    const normalized = payload.replace(/-/g, '+').replace(/_/g, '/')
    const json = decodeURIComponent(
      atob(normalized)
        .split('')
        .map((c) => '%' + c.charCodeAt(0).toString(16).padStart(2, '0'))
        .join('')
    )
    return JSON.parse(json) as { sub?: string; email?: string }
  } catch {
    return {}
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [accessToken, setAccessToken] = useState<string | null>(null)
  const [user, setUser] = useState<AuthUser | null>(null)
  const [isBootstrapping, setIsBootstrapping] = useState(true)

  const refreshMutation = useRefresh()
  const logoutMutation = useLogoutMutation()
  const hasBootstrapped = useRef(false)

  const setSession = useCallback((session: AuthSession) => {
    setAccessToken(session.accessToken)
    setUser(session.user)
  }, [])

  const clearSession = useCallback(() => {
    setAccessToken(null)
    setUser(null)
  }, [])

  const logout = useCallback(async () => {
    try {
      await logoutMutation.mutateAsync()
    } finally {
      clearSession()
    }
  }, [logoutMutation, clearSession])

  // Silent refresh on mount (AUTH-04: session survives a page reload). Guarded by a ref so
  // React 19 StrictMode's dev-mode double-effect-invoke doesn't fire two /refresh calls.
  useEffect(() => {
    // Ref guard ensures bootstrap runs exactly once even under React 19 StrictMode's
    // dev double-effect-invoke. Do NOT add a `cancelled`/cleanup flag here: StrictMode's
    // simulated cleanup would set it before the async finally runs, permanently stranding
    // `isBootstrapping=true` (endless spinner). The component is the always-mounted auth
    // root, so setState after the simulated unmount is safe (React 19 no longer warns).
    if (hasBootstrapped.current) return
    hasBootstrapped.current = true

    async function bootstrap() {
      try {
        const result = await refreshMutation.mutateAsync()
        const claims = decodeAccessTokenClaims(result.accessToken)
        setAccessToken(result.accessToken)
        setUser({
          id: claims.sub ?? '',
          email: claims.email ?? '',
          name: claims.email ?? '',
        })
      } catch {
        clearSession()
      } finally {
        setIsBootstrapping(false)
      }
    }

    void bootstrap()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return (
    <AuthContext.Provider
      value={{
        accessToken,
        user,
        isAuthenticated: accessToken !== null,
        isBootstrapping,
        setSession,
        logout,
      }}
    >
      {children}
    </AuthContext.Provider>
  )
}
