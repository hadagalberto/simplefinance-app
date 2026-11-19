import { use } from 'react'

import { ToastContext, type ToastApi } from './ToastContext'

/**
 * Gives access to `showNotification`, which raises a toast. The name is kept
 * from the earlier notification module so feature call sites stay unchanged.
 */
export function useNotificationContext(): ToastApi {
  const value = use(ToastContext)
  if (value === null) {
    throw new Error('useNotificationContext must be used inside <ToastProvider>')
  }
  return value
}
