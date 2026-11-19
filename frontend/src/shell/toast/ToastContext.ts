import { createContext } from 'react'

/** Background color of a toast, one of the Bootstrap theme colors. */
export type ToastVariant = 'primary' | 'secondary' | 'success' | 'danger' | 'warning' | 'info' | 'light' | 'dark'

/** One toast to show. */
export type ToastRequest = {
  /** Optional bold heading shown above the message. */
  title?: string
  /** The text the user reads. */
  message: string
  /** Background color; defaults to secondary. */
  variant?: ToastVariant
  /** Milliseconds before the toast closes by itself; defaults to 4000. */
  delay?: number
}

export type ToastApi = {
  /** Adds a toast to the stack in the top-right corner. */
  showNotification: (request: ToastRequest) => void
}

export const ToastContext = createContext<ToastApi | null>(null)
