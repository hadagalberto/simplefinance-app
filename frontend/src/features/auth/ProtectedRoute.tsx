import { Spinner } from 'react-bootstrap'
import { Navigate, Outlet } from 'react-router'

import { useAuth } from './useAuth'

function BootstrappingSpinner() {
  return (
    <div className="d-flex align-items-center justify-content-center vh-100">
      <Spinner animation="border" variant="primary" />
    </div>
  )
}

/**
 * Wraps the authenticated route subtree (the app shell and everything inside it).
 * While AuthContext's mount-time silent refresh is in flight, renders a
 * spinner instead of Outlet — its mirror, PublicOnlyRoute below, covers the
 * pre-auth routes.
 *
 * This is a client-side UX convenience only (T-10-05) — the real
 * enforcement is server-side [Authorize] on every protected endpoint; a
 * bypassed client guard cannot reach real data, it would just get 401s.
 */
export function ProtectedRoute() {
  const { isBootstrapping, isAuthenticated } = useAuth()

  if (isBootstrapping) {
    return <BootstrappingSpinner />
  }

  if (!isAuthenticated) {
    return <Navigate to="/login" replace />
  }

  return <Outlet />
}

/**
 * Wraps /setup, /login, /2fa/verify. If the user is already authenticated
 * (e.g. a fresh page load silently restored the session via the refresh
 * cookie), redirect into the app instead of re-showing an auth screen.
 */
export function PublicOnlyRoute() {
  const { isBootstrapping, isAuthenticated } = useAuth()

  if (isBootstrapping) {
    return <BootstrappingSpinner />
  }

  if (isAuthenticated) {
    return <Navigate to="/" replace />
  }

  return <Outlet />
}
